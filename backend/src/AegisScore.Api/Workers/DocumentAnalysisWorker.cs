using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Documents;
using AegisScore.Application.Services;
using AegisScore.Domain;
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
    /// <summary>Limiar de confiança da IA que separa cobertura Coberta de Parcial no ledger de cobertura.</summary>
    private const double DocumentCoverageThreshold = 0.7;

    /// <summary>
    /// Orçamento de caracteres da TRIAGEM (passada 1). Ela só precisa reconhecer os temas do documento,
    /// não julgar mérito — o julgamento vem depois, com trecho dirigido e a regra do 800-53 junto.
    /// </summary>
    private const int TriageCharBudget = 24_000;

    /// <summary>
    /// Orçamento do TRECHO por controle (passada 2). Enxuto de propósito: o parágrafo que prova um
    /// controle raramente passa disso, e texto irrelevante além de custar tokens dilui a atenção do
    /// modelo, empurrando-o a ancorar em passagens que não são evidência do controle sob julgamento.
    /// </summary>
    private const int ExcerptCharBudget = 6_000;

    /// <summary>
    /// Teto de crédito da evidência PURAMENTE DOCUMENTAL no Aegis Score. Um documento atesta processo e
    /// intenção — nunca prova que o controle está tecnicamente implementado. Por isso vale 50% dos pontos
    /// (<see cref="ControlStatus.MitigatedByThirdParty"/>) e JAMAIS <see cref="ControlStatus.Compliant"/>,
    /// que fica reservado à validação por telemetria. É o que separa conformidade real de teatro de
    /// segurança: nenhum PDF pode, sozinho, produzir um painel de "100% seguro".
    /// </summary>
    private const ControlStatus DocumentEvidenceStatus = ControlStatus.MitigatedByThirdParty;

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
        var ai = sp.GetRequiredService<IAiAssessmentService>();
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

            // Idempotência do reprocessamento (entrega at-least-once): zera os mapeamentos anteriores DESTE
            // documento antes de regravar — senão uma segunda passada os duplicaria. A cobertura e o ledger
            // já são upserts idempotentes por (tenant, subcategoria).
            var priorMappings = await db.DocumentControlMappings
                .Where(m => m.GovernanceDocumentId == doc.Id).ToListAsync(workCt);
            if (priorMappings.Count > 0) db.DocumentControlMappings.RemoveRange(priorMappings);

            // PASSADA 1 — TRIAGEM: quais controles este documento endereça? O documento não declara um
            // alvo (GovernanceDocument não tem esse campo), então é o modelo que aponta os candidatos.
            // O texto vai TRUNCADO: uma política de 80 páginas não cabe no contexto e a triagem só
            // precisa reconhecer os temas, não julgar o mérito.
            var analysis = await ai.AnalyzeDocumentAsync(
                new DocumentAnalysisRequest(lease.TenantId, Truncate(text, TriageCharBudget), doc.FileName), workCt);

            foreach (var claim in analysis.Claims)
            {
                // PASSADA 2 — RAG DIRIGIDO: agora que o alvo é conhecido, carrega a regra do 800-53 e
                // reavalia com payload enxuto (trecho relevante + controle + critérios de evidência).
                var refined = await RefineWithRuleAsync(db, ai, claim, text, doc.FileName, workCt);

                db.DocumentControlMappings.Add(new DocumentControlMapping
                {
                    GovernanceDocumentId = doc.Id,
                    SubcategoryCode = claim.SubcategoryCode,
                    Confidence = refined.Confidence,
                    Evidence = refined.Evidence,
                });
                await UpsertCoverageFromDocumentAsync(db, claim.SubcategoryCode, doc.Id, refined.Confidence, workCt);
            }

            doc.AnalysisSummary = analysis.Summary;
            await db.SaveChangesAsync(workCt);   // dados de negócio: mapeamentos, cobertura, resumo

            // Ponte Govern → Aegis Score. Roda DEPOIS do commit da trilha documental e é idempotente
            // (upsert por subcategoria): reprocessar reescreve a mesma célula, nunca duplica.
            await ProjectToScoreAsync(writer, lease.TenantId, analysis.Claims, doc.FileName, workCt);

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
    /// Projeta cada controle mapeado pelo documento no ledger de conformidade (TenantControlState),
    /// através da porta <see cref="IControlStateWriter"/> — a mesma usada pelo motor de telemetria.
    /// Um código alucinado pelo LLM (fora do catálogo NIST) é registrado e ignorado: nunca aborta os
    /// demais claims nem o documento, que já está persistido como Analyzed.
    /// </summary>
    private async Task ProjectToScoreAsync(
        IControlStateWriter writer, Guid tenantId, IReadOnlyList<DocumentClaim> claims,
        string? fileName, CancellationToken ct)
    {
        foreach (var claim in claims)
        {
            // A confiança da IA NÃO eleva o status — evidência documental tem teto de 50%. Ela é
            // preservada na trilha de auditoria, que registra origem, racional e a natureza parcial
            // do crédito concedido.
            var evidence =
                $"Evidência documental ({claim.Confidence:P0} de confiança) extraída de " +
                $"'{fileName ?? "documento"}': {claim.Claim} " +
                $"— crédito parcial (50%); conformidade plena aguarda validação por telemetria.";

            try
            {
                // Documentary: o writer só aplica se este veredito PONTUAR MAIS que o estado vigente.
                // Um controle já validado por telemetria (Compliant) nunca é rebaixado por um PDF.
                await writer.ApplyVerdictAsync(
                    tenantId, claim.SubcategoryCode, DocumentEvidenceStatus, evidence, VerdictSource.Documentary, ct: ct);
            }
            catch (InvalidOperationException ex)
            {
                _log.LogWarning(ex,
                    "Claim ignorado: a subcategoria '{Code}' não existe no catálogo NIST (documento {File}).",
                    claim.SubcategoryCode, fileName);
            }
        }
    }

    /// <summary>
    /// PASSADA 2 do RAG documental: carrega a <c>AegisAssessmentRule</c> do controle apontado na triagem,
    /// seleciona do documento apenas o trecho que o endereça (<see cref="DocumentChunker"/>) e pede ao
    /// motor um veredito com a régua do 800-53 na mão. É o que transforma "o texto fala de MFA" em
    /// "o texto PROVA (ou não) o outcome de PR.AA-01".
    ///
    /// RESILIENTE por decisão: se a regra não existir no catálogo, ou se o motor estiver indisponível, o
    /// resultado da triagem é mantido em vez de derrubar o documento. Um refinamento é MELHORIA de
    /// precisão — perdê-lo degrada a nota da evidência, não a capacidade de registrar que ela existe.
    /// </summary>
    private async Task<(double Confidence, string Evidence)> RefineWithRuleAsync(
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

        if (rule is null || outcome is null)
        {
            _log.LogDebug(
                "Sem regra/catálogo para {Code}: mantendo a confiança da triagem ({Confidence:P0}).",
                claim.SubcategoryCode, claim.Confidence);
            return (claim.Confidence, claim.Claim);
        }

        // Duas faixas de peso para a seleção do trecho.
        //
        // ⚠️ A faixa PRIMÁRIA são as `evaluation_metrics`, NÃO o outcome do catálogo — e a razão é o
        // IDIOMA. O catálogo NIST é em inglês ("The recovery portion of the incident response plan…")
        // enquanto as políticas do cliente são em português: casar léxico cross-língua não funciona, e
        // num teste ao vivo o outcome não pontuou NADA, deixando a escolha do trecho inteiramente nas
        // mãos do vocabulário genérico. As métricas do `aegis_assessment_rules.json` são PT-BR e
        // específicas do controle ("recuperação", "restauração", "RTO"), então são elas que definem o
        // assunto. O outcome fica na faixa de apoio — ajuda em documentos em inglês, não atrapalha.
        var primaryTerms = new List<string> { claim.SubcategoryCode };
        primaryTerms.AddRange(rule.EvaluationMetrics);

        var supportingTerms = new List<string> { outcome };
        supportingTerms.AddRange(rule.EvidenceRequirements);

        var excerpt = DocumentChunker.SelectRelevantExcerpt(
            fullText, primaryTerms, supportingTerms, ExcerptCharBudget);

        try
        {
            var verdict = await ai.EvaluateDocumentControlAsync(
                new DocumentControlEvaluationRequest(
                    claim.SubcategoryCode, outcome, rule.EvidenceRequirements,
                    rule.CalculationLogic ?? "", excerpt, fileName),
                ct);

            _log.LogInformation(
                "RAG documental {Code}: triagem {Triage:P0} → dirigido {Refined:P0} ({Chars} chars de trecho).",
                claim.SubcategoryCode, claim.Confidence, verdict.Confidence, excerpt.Length);

            return (verdict.Confidence, verdict.Rationale);
        }
        // ⚠️ O `when` exclui o cancelamento REAL do serviço. TaskCanceledException deriva de
        // OperationCanceledException e chega tanto por timeout do HttpClient (transitório, degrada) quanto
        // por shutdown (deve propagar). Sem essa distinção, parar o serviço seria silenciosamente tratado
        // como "LLM indisponível", o documento seguiria sendo gravado e o desligamento não seria gracioso.
        catch (Exception ex) when (
            !ct.IsCancellationRequested
            && ex is AiUnavailableException or HttpRequestException or TaskCanceledException)
        {
            // Indisponibilidade do LLM não pode custar o documento inteiro: a triagem já foi paga e é
            // uma evidência válida, só menos calibrada.
            _log.LogWarning(ex,
                "Refinamento de {Code} indisponível; mantendo a confiança da triagem ({Confidence:P0}).",
                claim.SubcategoryCode, claim.Confidence);
            return (claim.Confidence, claim.Claim);
        }
    }

    /// <summary>Corta o texto no orçamento, sem quebrar no meio de uma palavra quando dá para evitar.</summary>
    private static string Truncate(string text, int budget)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= budget) return text;
        var cut = text[..budget];
        var lastSpace = cut.LastIndexOf(' ');
        return lastSpace > budget / 2 ? cut[..lastSpace] : cut;
    }

    /// <summary>Documento cobre a subcategoria: confiança alta = Coberto, senão Parcial. Nunca rebaixa.</summary>
    private static async Task UpsertCoverageFromDocumentAsync(
        AegisScoreDbContext db, string code, Guid docId, double confidence, CancellationToken ct)
    {
        var cov = await db.SubcategoryCoverages.FirstOrDefaultAsync(c => c.SubcategoryCode == code, ct);
        var status = confidence >= DocumentCoverageThreshold ? CoverageStatus.Coberto : CoverageStatus.Parcial;

        if (cov is null)
        {
            db.SubcategoryCoverages.Add(new SubcategoryCoverage
            {
                SubcategoryCode = code,
                Status = status,
                EvidenceSource = CoverageEvidenceSource.Document,
                OriginDocumentId = docId,
                Confidence = confidence,
                LastEvaluatedAt = DateTimeOffset.UtcNow,
            });
            return;
        }

        if (status == CoverageStatus.Coberto) cov.Status = CoverageStatus.Coberto;
        else if (cov.Status == CoverageStatus.NaoCoberto) cov.Status = CoverageStatus.Parcial;
        cov.EvidenceSource = cov.EvidenceSource == CoverageEvidenceSource.Interview
            ? CoverageEvidenceSource.Both : CoverageEvidenceSource.Document;
        cov.OriginDocumentId = docId;
        cov.Confidence = confidence;
        cov.LastEvaluatedAt = DateTimeOffset.UtcNow;
    }
}
