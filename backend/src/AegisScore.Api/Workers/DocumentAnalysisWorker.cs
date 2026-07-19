using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Documents;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;

namespace AegisScore.Api.Workers;

/// <summary>
/// Consome a fila de leitura: para cada documento, extrai o texto, chama a IA para mapear os
/// controles NIST e atualiza o ledger de cobertura. Roda sob um SystemTenantContext do tenant
/// dono do documento (o stamping é fail-closed e não há header de request aqui).
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
    private readonly ILogger<DocumentAnalysisWorker> _log;

    public DocumentAnalysisWorker(
        IServiceScopeFactory scopes, IDocumentAnalysisQueue queue, ILogger<DocumentAnalysisWorker> log)
    {
        _scopes = scopes;
        _queue = queue;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // A fila é em MEMÓRIA (Channel): um restart perde tudo o que estava enfileirado, e um documento
        // interrompido no meio ficaria parado para sempre — ninguém o reenfileira sozinho. Sem esta
        // varredura, "devolver a Pending" no shutdown seria só trocar um limbo por outro.
        await RequeueOrphansAsync(ct);

        try
        {
            await foreach (var docId in _queue.DequeueAllAsync(ct))
            {
                // Shutdown pedido entre dois itens da fila: sai limpo em vez de começar um documento que
                // será abortado no meio, deixando-o preso em Processing.
                if (ct.IsCancellationRequested) break;

                try
                {
                    await ProcessAsync(docId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Desligamento gracioso NÃO é falha. O catch genérico abaixo registrava um Error a
                    // cada parada do serviço e poluía o alarme operacional com ruído de deploy.
                    _log.LogInformation(
                        "Análise do documento {DocId} interrompida pelo desligamento do serviço; será " +
                        "reprocessada na próxima execução.", docId);
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Falha inesperada ao analisar documento {DocId}", docId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // O próprio enumerador da fila é cancelado no shutdown — desfecho esperado, não erro.
        }
    }

    /// <summary>
    /// Recupera os documentos que ficaram órfãos entre execuções: os que estavam <c>Processing</c>
    /// quando o serviço caiu e os que voltaram a <c>Pending</c>/<c>Queued</c> num desligamento. Roda UMA
    /// vez, no arranque do worker, antes de consumir a fila.
    ///
    /// Sem tenant ambiente (é varredura global de manutenção), daí <c>IgnoreQueryFilters</c> — mesmo
    /// idioma da sondagem de dono em <c>ProcessAsync</c>. Só re-enfileira quem tem binário: documento
    /// registrado sem <c>StorageUri</c> (caminho /connect) não tem o que analisar.
    /// </summary>
    private async Task RequeueOrphansAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
            await using var db = new AegisScoreDbContext(options, new SystemTenantContext(null));

            var orphans = await db.GovernanceDocuments.IgnoreQueryFilters()
                .Where(d => d.StorageUri != null
                         && (d.AnalysisStatus == AiAnalysisStatus.Processing
                          || d.AnalysisStatus == AiAnalysisStatus.Queued
                          || d.AnalysisStatus == AiAnalysisStatus.Pending))
                .Select(d => d.Id)
                .ToListAsync(ct);

            if (orphans.Count == 0) return;

            foreach (var id in orphans)
                await _queue.EnqueueAsync(id, ct);

            _log.LogInformation(
                "Recuperação de fila: {Count} documento(s) pendente(s) reenfileirado(s) no arranque.",
                orphans.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Serviço derrubado durante o arranque — nada a recuperar, e o próximo boot tenta de novo.
        }
        catch (Exception ex)
        {
            // A recuperação é BEST-EFFORT: falhar aqui (banco indisponível no arranque, por exemplo) não
            // pode impedir o worker de subir e atender os documentos que chegarem a partir de agora.
            _log.LogWarning(ex, "Não foi possível reenfileirar documentos pendentes no arranque.");
        }
    }

    private async Task ProcessAsync(Guid docId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var options = sp.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        var ai = sp.GetRequiredService<IAiAssessmentService>();
        var storage = sp.GetRequiredService<IDocumentStorage>();
        var extractors = sp.GetServices<IDocumentTextExtractor>().ToList();

        // Descobre o tenant dono (sem tenant ambiente → IgnoreQueryFilters).
        Guid tenantId;
        await using (var probe = new AegisScoreDbContext(options, new SystemTenantContext(null)))
        {
            var owner = await probe.GovernanceDocuments.IgnoreQueryFilters()
                .Where(d => d.Id == docId)
                .Select(d => (Guid?)d.TenantId)
                .FirstOrDefaultAsync(ct);
            if (owner is null) return;
            tenantId = owner.Value;
        }

        // O writer precisa do MESMO tenant ambiente do DbContext. Fora de um request HTTP não há
        // ITenantContext utilizável no container (o HttpTenantContext devolveria null e o guard
        // fail-closed abortaria), então o construímos com o SystemTenantContext do documento —
        // exatamente como já fazemos com o DbContext. A dependência declarada é a PORTA.
        var tenantCtx = new SystemTenantContext(tenantId);
        await using var db = new AegisScoreDbContext(options, tenantCtx);
        IControlStateWriter writer = new ControlStateWriter(
            db, tenantCtx, sp.GetRequiredService<ILogger<ControlStateWriter>>());

        var doc = await db.GovernanceDocuments.FirstOrDefaultAsync(d => d.Id == docId, ct);
        if (doc is null || doc.StorageUri is null) return;

        doc.AnalysisStatus = AiAnalysisStatus.Processing;
        await db.SaveChangesAsync(ct);

        try
        {
            await using var stream = await storage.OpenAsync(doc.StorageUri, ct);
            var extractor = extractors.FirstOrDefault(e => e.CanHandle(doc.ContentType, doc.FileName))
                ?? throw new NotSupportedException($"Sem extrator de texto para '{doc.ContentType ?? doc.FileName}'.");
            var text = await extractor.ExtractAsync(stream, doc.ContentType, ct);

            // PASSADA 1 — TRIAGEM: quais controles este documento endereça? O documento não declara um
            // alvo (GovernanceDocument não tem esse campo), então é o modelo que aponta os candidatos.
            // O texto vai TRUNCADO: uma política de 80 páginas não cabe no contexto e a triagem só
            // precisa reconhecer os temas, não julgar o mérito.
            var analysis = await ai.AnalyzeDocumentAsync(
                new DocumentAnalysisRequest(tenantId, Truncate(text, TriageCharBudget), doc.FileName), ct);

            foreach (var claim in analysis.Claims)
            {
                // PASSADA 2 — RAG DIRIGIDO: agora que o alvo é conhecido, carrega a regra do 800-53 e
                // reavalia com payload enxuto (trecho relevante + controle + critérios de evidência).
                var refined = await RefineWithRuleAsync(db, ai, claim, text, doc.FileName, ct);

                db.DocumentControlMappings.Add(new DocumentControlMapping
                {
                    GovernanceDocumentId = doc.Id,
                    SubcategoryCode = claim.SubcategoryCode,
                    Confidence = refined.Confidence,
                    Evidence = refined.Evidence,
                });
                await UpsertCoverageFromDocumentAsync(db, claim.SubcategoryCode, doc.Id, refined.Confidence, ct);
            }

            doc.AnalysisSummary = analysis.Summary;
            doc.AnalysisStatus = AiAnalysisStatus.Analyzed;
            doc.AnalyzedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // Ponte Govern → Aegis Score. Roda DEPOIS do commit da trilha documental: a análise é atômica
            // em si, e a projeção no ledger é um passo idempotente (upsert) — reprocessar o documento
            // reescreve a mesma célula, nunca duplica. Sem acoplamento ao motor de telemetria: ambas as
            // fontes de evidência gravam pelo mesmo IControlStateWriter.
            await ProjectToScoreAsync(writer, tenantId, analysis.Claims, doc.FileName, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // DESLIGAMENTO, não falha do documento. Volta a Pending e o RequeueOrphansAsync do próximo
            // arranque o recoloca na fila: marcá-lo Failed acusaria de defeito um documento que nunca
            // chegou a ser julgado, e deixá-lo em Processing o prenderia para sempre.
            //
            // ⚠️ Grava com CancellationToken.None DE PROPÓSITO: o token do ciclo já está cancelado e
            // reusá-lo aqui faria o próprio SaveChanges lançar, que é exatamente como um documento fica
            // preso em Processing. Esta escrita é curta, local e precisa acontecer para o estado ficar
            // coerente — é o padrão de "limpeza no encerramento".
            doc.AnalysisStatus = AiAnalysisStatus.Pending;
            doc.AnalysisError = null;
            await db.SaveChangesAsync(CancellationToken.None);

            _log.LogInformation(
                "Documento {DocId} devolvido à fila (Pending) pelo desligamento do serviço.", docId);
            throw;   // deixa o ExecuteAsync encerrar o laço com elegância
        }
        catch (Exception ex)
        {
            doc.AnalysisStatus = AiAnalysisStatus.Failed;
            doc.AnalysisError = ex.Message;
            // Idem: se a causa foi um timeout que também cancelou o token, gravar com `ct` esconderia o
            // erro real por trás de um segundo cancelamento e o documento nunca sairia de Processing.
            await db.SaveChangesAsync(CancellationToken.None);
            _log.LogWarning(ex, "Leitura da IA falhou para o documento {DocId}", docId);
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
