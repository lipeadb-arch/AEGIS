using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Documents;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Documents;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;

namespace AegisScore.Api.Workers;

/// <summary>
/// [AEGIS-AUD-050] Consome a fila operacional DURÁVEL de análise de documentos. Não há mais canal em
/// memória: o worker SONDA o banco, ADQUIRE o próximo documento disponível com um lease atômico
/// (<see cref="IDocumentAnalysisQueue"/> → FOR UPDATE SKIP LOCKED), extrai o texto, chama a IA para mapear
/// os controles NIST e atualiza o ledger — tudo sob um <see cref="SystemTenantContext"/> do tenant DONO do
/// documento (a varredura cross-tenant vive só na aquisição). O trabalho sobrevive a reinício, encerramento
/// no meio e múltiplas réplicas: a entrega é at-least-once (idempotente), o lease vencido é reaproveitado, a
/// falha transitória agenda retry e o limite de tentativas termina em Failed.
/// </summary>
public sealed class DocumentAnalysisWorker : BackgroundService
{
    /// <summary>
    /// Orçamento do TRECHO por controle (passada 2). Enxuto de propósito: o parágrafo que prova um
    /// controle raramente passa disso, e texto irrelevante além de custar tokens dilui a atenção do
    /// modelo, empurrando-o a ancorar em passagens que não são evidência do controle sob julgamento.
    /// </summary>
    private const int ExcerptCharBudget = 6_000;

    private readonly IServiceScopeFactory _scopes;
    private readonly IDocumentAnalysisQueue _queue;
    private readonly TimeProvider _clock;
    private readonly ILogger<DocumentAnalysisWorker> _log;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _heartbeatInterval;
    private readonly int _maxAttempts;

    public DocumentAnalysisWorker(
        IServiceScopeFactory scopes, IDocumentAnalysisQueue queue, TimeProvider clock,
        IOptions<DocumentAnalysisQueueOptions> options, ILogger<DocumentAnalysisWorker> log)
    {
        _scopes = scopes;
        _queue = queue;
        _clock = clock;
        _log = log;
        var opt = options.Value;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, opt.PollSeconds));
        _heartbeatInterval = TimeSpan.FromSeconds(Math.Max(1, opt.EffectiveHeartbeatSeconds));
        _maxAttempts = Math.Max(1, opt.MaxAttempts);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // SEM varredura de órfãos no arranque: a durabilidade a torna desnecessária. Um documento
        // interrompido fica Processing com lease, que EXPIRA e volta a ser adquirível sozinho; um Queued
        // nunca saiu da fila. O PeriodicTimer é só o AGENDADOR da sondagem — o transporte e a memória do
        // trabalho são o banco.
        try
        {
            using var timer = new PeriodicTimer(_pollInterval);
            do
            {
                try
                {
                    await DrainAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;   // desligamento gracioso durante o dreno
                }
                catch (Exception ex)
                {
                    // Um ciclo com falha (ex.: banco momentaneamente indisponível) NUNCA derruba o worker.
                    _log.LogError(ex, "Ciclo de análise de documentos falhou; retomará no próximo tick.");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Encerramento do host durante a espera — saída limpa, sem ruído de erro no log.
        }
    }

    /// <summary>Adquire e processa documentos em sequência até a fila esvaziar; então aguarda o próximo tick.</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var lease = await _queue.TryClaimNextAsync(ct);
            if (lease is null) return;   // nada disponível agora
            await ProcessLeasedAsync(lease, ct);
        }
    }

    private async Task ProcessLeasedAsync(DocumentAnalysisLease lease, CancellationToken ct)
    {
        // Poison reclamado ALÉM do limite — só alcançável por crash repetido ANTES de o catch marcar o
        // desfecho. Encerra terminal sem reprocessar. O limite no fluxo normal (falha capturada) é tratado
        // no catch abaixo, onde a última tentativa (== limite) ainda É processada.
        if (lease.Attempts > _maxAttempts)
        {
            await _queue.FailAsync(lease.DocumentId, lease.LeaseId, "AttemptsExhausted", CancellationToken.None);
            _log.LogWarning(
                "Documento {DocId} excedeu o limite de tentativas ({Max}) por reaquisições sucessivas; marcado Failed.",
                lease.DocumentId, _maxAttempts);
            return;
        }

        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var options = sp.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        // Gate do Free Tier: o worker NÃO tem tenant HTTP ambiente, então FIXA o tenant DONO do lease no
        // resolver ANTES de resolver a IA — os roteadores (assessment + transporte) decidem Gemini × stub
        // pelo slug desse tenant. Sem isto, um tenant da allowlist cairia sempre no stub aqui.
        sp.GetRequiredService<IAiTenantResolver>().OverrideTenant(lease.TenantId);
        var ai = sp.GetRequiredService<IAiAssessmentService>();
        var freeTier = sp.GetRequiredService<IOptions<AiOptions>>().Value.FreeTier;
        var storage = sp.GetRequiredService<IDocumentStorage>();
        var extractors = sp.GetServices<IDocumentTextExtractor>().ToList();

        // O processamento segue sob o tenant DONO do item (resolvido na aquisição) — nunca cross-tenant.
        // O writer precisa do MESMO tenant ambiente do DbContext; ambos usam o SystemTenantContext do lease.
        var tenantCtx = new SystemTenantContext(lease.TenantId);
        await using var db = new AegisScoreDbContext(options, tenantCtx);
        IControlStateWriter writer = new ControlStateWriter(
            db, tenantCtx, sp.GetRequiredService<ILogger<ControlStateWriter>>());

        // BATIMENTO DE LEASE: uma extração/IA lenta pode durar mais que o lease. O heartbeat renova o lease
        // durante o trabalho; se o lease for PERDIDO (expirou e outra réplica assumiu), ele cancela `leaseCts`,
        // e o processamento aborta. `leaseCts` liga o shutdown (ct) ao sinal de lease-perdido — o trabalho roda
        // sob `workCt`. É isso que impede duas réplicas no MESMO documento por excesso de duração.
        using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var heartbeat = LeaseHeartbeat.Start(
            c => _queue.RenewAsync(lease.DocumentId, lease.LeaseId, c),
            _heartbeatInterval, _clock, leaseCts, _log);
        var workCt = leaseCts.Token;

        var doc = await db.GovernanceDocuments.FirstOrDefaultAsync(d => d.Id == lease.DocumentId, workCt);
        if (doc is null || doc.StorageUri is null)
        {
            // A aquisição exige StorageUri IS NOT NULL, então isto é defensivo: sem binário não há o que ler.
            await _queue.FailAsync(lease.DocumentId, lease.LeaseId, "NoBinary", CancellationToken.None);
            return;
        }

        try
        {
            await using var stream = await storage.OpenAsync(doc.StorageUri, workCt);
            var extractor = extractors.FirstOrDefault(e => e.CanHandle(doc.ContentType, doc.FileName))
                ?? throw new NotSupportedException($"Sem extrator de texto para '{doc.ContentType ?? doc.FileName}'.");
            var text = await extractor.ExtractAsync(stream, doc.ContentType, workCt);

            // Idempotência do reprocessamento (entrega at-least-once): CAPTURA os códigos anteriores DESTE
            // documento e zera os mapeamentos antes de regravar. Os códigos antigos entram na reconciliação
            // (união com os novos) — um controle que deixe de ser sustentado precisa ser RETRAÍDO, não só
            // sobrescrito.
            var priorMappings = await db.DocumentControlMappings
                .Where(m => m.GovernanceDocumentId == doc.Id).ToListAsync(workCt);
            var oldCodes = priorMappings.Select(m => m.SubcategoryCode).ToList();
            if (priorMappings.Count > 0) db.DocumentControlMappings.RemoveRange(priorMappings);

            // PASSADA 1 — TRIAGEM: quais controles este documento PODE endereçar (CANDIDATOS). O documento
            // não declara um alvo, então é o modelo que aponta os candidatos. A triagem NÃO é prova: um
            // candidato só vira evidência se a passada 2 o SUSTENTAR com trecho literal. O texto vai
            // TRUNCADO: uma política de 80 páginas não cabe no contexto e a triagem só reconhece temas.
            var analysis = await ai.AnalyzeDocumentAsync(
                new DocumentAnalysisRequest(lease.TenantId, Truncate(text, freeTier.MaxDocumentChars), doc.FileName), workCt);

            // PASSADA 2 — JULGAMENTO DIRIGIDO + VALIDAÇÃO LITERAL. Só o que volta SUSTENTADO e com trecho
            // presente no texto vira mapping probatório; o resto é descartado fail-closed (zero mapping,
            // zero cobertura, zero score) — mas o documento ainda termina Analisado.
            // Teto de chamadas por análise (Free Tier): a triagem já consumiu 1 chamada; os julgamentos
            // dirigidos ficam limitados ao restante do orçamento, preservando a cota gratuita. Os candidatos
            // vêm ordenados pela triagem, então o corte mantém os mais relevantes.
            var maxControlCalls = Math.Max(0, freeTier.MaxCallsPerAnalysis - 1);
            var validated = new List<RefinedControlResult>();
            foreach (var claim in analysis.Claims.Take(maxControlCalls))
            {
                // Indisponibilidade do refinamento NÃO cai para a triagem como prova: RefineWithRuleAsync
                // deixa a exceção propagar e a fila durável reprocessa (retry/falha controlada).
                var refined = await RefineWithRuleAsync(db, ai, claim, text, doc.FileName, workCt);

                if (!refined.Supported)
                {
                    _log.LogInformation(
                        "Documento {DocId}: {Code} descartado — o refinamento não sustentou o controle.",
                        doc.Id, claim.SubcategoryCode);
                    continue;
                }

                // AUTORIDADE FINAL: o trecho tem de existir LITERALMENTE no texto extraído. Um trecho
                // inventado — por modelo real OU pelo stub — é descartado: jamais gera mapping/cobertura/score.
                if (!EvidenceQuoteValidator.IsLiterallyPresent(text, refined.EvidenceQuote))
                {
                    _log.LogWarning(
                        "Documento {DocId}: {Code} descartado — trecho probatório ausente do texto (não literal).",
                        doc.Id, claim.SubcategoryCode);
                    continue;
                }

                validated.Add(refined);
                db.DocumentControlMappings.Add(new DocumentControlMapping
                {
                    GovernanceDocumentId = doc.Id,
                    SubcategoryCode = refined.SubcategoryCode,
                    Confidence = refined.Confidence,
                    EvidenceQuote = refined.EvidenceQuote,   // trecho literal validado (separado do racional)
                    Evidence = refined.Rationale,            // racional da análise
                });
            }

            // Resumo HONESTO: reflete a evidência PROBATÓRIA, não os candidatos da triagem — a interface
            // nunca deve alegar mais do que o documento prova.
            doc.AnalysisSummary = validated.Count > 0
                ? $"{validated.Count} controle(s) NIST com evidência probatória literal no documento."
                : "Nenhum controle NIST com evidência probatória literal — documento sem valor probatório.";

            // ATOMICIDADE: a substituição dos mapeamentos + a reconciliação de ledger/cobertura acontecem
            // numa ÚNICA transação. Se a reconciliação falhar no meio, o ROLLBACK integral desfaz também a
            // troca de mapeamentos — o documento volta ao estado anterior e a fila durável reprocessa com a
            // lista ORIGINAL de códigos intacta (nada de estado parcialmente atualizado). O trabalho de IA
            // já terminou aqui; a transação cobre só as escritas de banco, então é curta.
            var reconciler = new DocumentEvidenceReconciler(
                db, tenantCtx, writer, sp.GetRequiredService<ILogger<DocumentEvidenceReconciler>>());
            var affectedCodes = oldCodes.Concat(validated.Select(v => v.SubcategoryCode)).ToList();

            await using (var tx = await db.Database.BeginTransactionAsync(workCt))
            {
                await db.SaveChangesAsync(workCt);   // mapeamentos probatórios + metadados do documento
                await reconciler.ReconcileAsync(lease.TenantId, affectedCodes, workCt);
                await tx.CommitAsync(workCt);
            }

            // Confirmação ATÔMICA guardada pelo lease: Processing → Analyzed. Usa CancellationToken.None — o
            // trabalho ACABOU e a confirmação não pode ser interrompida por shutdown. Se o lease já não é o
            // vigente (perdido no fio final), a confirmação vira no-op e a PERDA É DETECTADA (completed=false).
            var completed = await _queue.CompleteAsync(doc.Id, lease.LeaseId, CancellationToken.None);
            if (!completed)
                _log.LogWarning(
                    "Documento {DocId}: lease não era mais o vigente ao confirmar; outra réplica assumiu.", doc.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // DESLIGAMENTO, não falha do documento: solta o lease e devolve à fila SEM custar tentativa. Grava
            // com CancellationToken.None de propósito (o token do ciclo já está cancelado); o próximo boot /
            // outra réplica o retoma de imediato. É o que garante "shutdown não perde trabalho".
            await _queue.ReleaseAsync(doc.Id, lease.LeaseId, CancellationToken.None);
            _log.LogInformation("Documento {DocId} devolvido à fila pelo desligamento do serviço.", doc.Id);
            throw;   // deixa o dreno/laço encerrar com elegância
        }
        catch (OperationCanceledException) when (leaseCts.IsCancellationRequested)
        {
            // LEASE PERDIDO no meio do trabalho (o heartbeat detectou expiração + reaquisição por outra
            // réplica). Abandona SILENCIOSAMENTE: a outra réplica é a dona agora — não confirmamos, não
            // agendamos retry e não soltamos (mexer no item alheio corromperia a entrega).
            _log.LogWarning(
                "Documento {DocId}: lease perdido durante o processamento; abandonando (outra réplica assumiu).",
                doc.Id);
        }
        catch (Exception ex)
        {
            // Falha ao processar. Categoria SANITIZADA (nome do tipo de exceção), NUNCA a mensagem bruta —
            // não amplia o AEGIS-AUD-054. Com orçamento de tentativas, agenda retry; no limite, termina Failed.
            // As transições são guardadas pelo lease: se ele já se perdeu, viram no-op (sem corromper o item alheio).
            var category = ex.GetType().Name;
            if (lease.Attempts >= _maxAttempts)
            {
                await _queue.FailAsync(doc.Id, lease.LeaseId, category, CancellationToken.None);
                _log.LogWarning(ex,
                    "Análise do documento {DocId} falhou na tentativa {Attempt}/{Max}; marcado Failed (terminal).",
                    doc.Id, lease.Attempts, _maxAttempts);
            }
            else
            {
                await _queue.ScheduleRetryAsync(doc.Id, lease.LeaseId, CancellationToken.None);
                _log.LogWarning(ex,
                    "Análise do documento {DocId} falhou na tentativa {Attempt}/{Max}; reagendada para retry.",
                    doc.Id, lease.Attempts, _maxAttempts);
            }
        }
    }

    /// <summary>
    /// Resultado REFINADO e SEPARADO por eixo de um controle candidato: se o texto SUSTENTA o controle,
    /// o TRECHO LITERAL que o prova, o RACIONAL (análise, não prova) e a CONFIANÇA. É o único insumo que
    /// vira mapping/cobertura/score — e mesmo assim só depois da validação literal do trecho.
    /// </summary>
    private sealed record RefinedControlResult(
        string SubcategoryCode, bool Supported, string EvidenceQuote, string Rationale, double Confidence);

    /// <summary>
    /// PASSADA 2 do RAG documental: carrega a <c>AegisAssessmentRule</c> do controle apontado na triagem,
    /// seleciona do documento apenas o trecho que o endereça (<see cref="DocumentChunker"/>) e pede ao
    /// motor um veredito PROBATÓRIO com a régua do 800-53 na mão — se o trecho SUSTENTA o controle, e qual
    /// é o TRECHO LITERAL que o prova.
    ///
    /// FAIL-CLOSED por decisão:
    /// <list type="bullet">
    /// <item>código fora do catálogo (alucinação da triagem) → não sustentado (não há outcome a provar);</item>
    /// <item>indisponibilidade do refinamento (LLM fora) → a exceção PROPAGA. A triagem JAMAIS é usada como
    /// prova; a fila durável reprocessa (retry/falha controlada). É o oposto do comportamento anterior, que
    /// silenciava a indisponibilidade e gravava a triagem crua como se fosse evidência.</item>
    /// </list>
    /// </summary>
    private async Task<RefinedControlResult> RefineWithRuleAsync(
        AegisScoreDbContext db, IAiAssessmentService ai, DocumentClaim claim,
        string fullText, string? fileName, CancellationToken ct)
    {
        // Reference data GLOBAL (sem filtro de tenant) — a regra é do framework, não do cliente.
        var rule = await db.AssessmentRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.SubcategoryCode == claim.SubcategoryCode, ct);
        var outcome = await db.Subcategories.AsNoTracking()
            .Where(s => s.Code == claim.SubcategoryCode)
            .Select(s => s.Description)
            .FirstOrDefaultAsync(ct);

        // Sem outcome não há controle NIST a provar: código alucinado pela triagem. Não sustentado — nunca
        // usa a triagem como prova (fail-closed).
        if (outcome is null)
        {
            _log.LogDebug(
                "Refinamento de {Code}: subcategoria fora do catálogo — não sustentado.", claim.SubcategoryCode);
            return new RefinedControlResult(claim.SubcategoryCode, Supported: false, "", "Código fora do catálogo NIST.", 0);
        }

        // Seleção de trecho em duas faixas (a regra pode não existir para todo controle; sem ela, ainda se
        // julga contra o outcome). A faixa primária são as métricas PT-BR do controle (o catálogo é inglês).
        var primaryTerms = new List<string> { claim.SubcategoryCode };
        if (rule is not null) primaryTerms.AddRange(rule.EvaluationMetrics);

        var supportingTerms = new List<string> { outcome };
        if (rule is not null) supportingTerms.AddRange(rule.EvidenceRequirements);

        var excerpt = DocumentChunker.SelectRelevantExcerpt(
            fullText, primaryTerms, supportingTerms, ExcerptCharBudget);

        // Indisponibilidade do motor PROPAGA (não é capturada aqui): o chamador deixa a fila durável
        // reprocessar. Nunca se degrada para "triagem como prova".
        var verdict = await ai.EvaluateDocumentControlAsync(
            new DocumentControlEvaluationRequest(
                claim.SubcategoryCode, outcome,
                (IReadOnlyList<string>?)rule?.EvidenceRequirements ?? Array.Empty<string>(),
                rule?.CalculationLogic ?? "", excerpt, fileName),
            ct);

        _log.LogInformation(
            "RAG documental {Code}: sustentado={Supported}, confiança {Refined:P0} ({Chars} chars de trecho).",
            claim.SubcategoryCode, verdict.Supported, verdict.Confidence, excerpt.Length);

        return new RefinedControlResult(
            claim.SubcategoryCode, verdict.Supported, verdict.EvidenceQuote ?? "",
            verdict.Rationale ?? "", verdict.Confidence);
    }

    /// <summary>Corta o texto no orçamento, sem quebrar no meio de uma palavra quando dá para evitar.</summary>
    private static string Truncate(string text, int budget)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= budget) return text;
        var cut = text[..budget];
        var lastSpace = cut.LastIndexOf(' ');
        return lastSpace > budget / 2 ? cut[..lastSpace] : cut;
    }
}
