using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Identity;
using AegisScore.Application.Identity.Adm;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Knight;

/// <summary>
/// Serviço de aplicação do AEGIS KNIGHT — MULTICOLETOR. Resolve a configuração da fonte, coleta pelo coletor
/// da fonte (Demo sintético, Entra real…), avalia os fatos normalizados de forma DETERMINÍSTICA
/// (<see cref="KnightIndicatorEvaluator"/>), calcula score/cobertura pela fórmula PRÓPRIA do KNIGHT
/// (<see cref="KnightScoreFormula"/> — nunca a global aegis-score-v1), persiste (com fonte/estado/capacidades)
/// e gera a narrativa consultiva pela IA (com fallback), concluindo mesmo se a IA estiver indisponível.
///
/// Uma falha REAL da coleta (permissão/indisponibilidade) é persistida com o estado real — NUNCA há
/// substituição silenciosa por dados sintéticos (Demo). A IA jamais decide status/severidade/score/cobertura.
/// Persiste no <see cref="AegisScoreDbContext"/> com Global Query Filter + stamping de tenant fail-closed.
/// </summary>
public sealed class AegisKnightAssessmentService : IAegisKnightAssessmentService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly AegisScoreDbContext _db;
    private readonly IKnightCollectorRegistry _registry;
    private readonly IKnightSourceConfigurationProvider _config;
    private readonly IKnightAdvisoryGenerator _advisory;
    private readonly IIdentityEvidenceService _identityEvidence;
    private readonly IIdentityAcquisitionStore _identityAcquisitions;
    private readonly ITenantContext _tenant;
    private readonly ILogger<AegisKnightAssessmentService>? _log;

    public AegisKnightAssessmentService(
        AegisScoreDbContext db,
        IKnightCollectorRegistry registry,
        IKnightSourceConfigurationProvider config,
        IKnightAdvisoryGenerator advisory,
        IIdentityEvidenceService identityEvidence,
        IIdentityAcquisitionStore identityAcquisitions,
        ITenantContext tenant,
        ILogger<AegisKnightAssessmentService>? log = null)
    {
        _db = db;
        _registry = registry;
        _config = config;
        _advisory = advisory;
        _identityEvidence = identityEvidence;
        _identityAcquisitions = identityAcquisitions;
        _tenant = tenant;
        _log = log;
    }

    public Task<KnightAssessment> RunDemoAssessmentAsync(CancellationToken ct = default) =>
        RunAssessmentAsync(KnightSourceType.Demo, ct);

    public async Task<KnightAssessment> RunAssessmentAsync(KnightSourceType source, CancellationToken ct = default)
    {
        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException("Execução do assessment KNIGHT sem tenant resolvido no contexto (fail-closed).");

        // 1+2) COLETA → fatos normalizados + estado + capacidades. Fonte real sem configuração NÃO executa e NÃO
        //       cai para Demo; o coletor NUNCA devolve dados sintéticos numa falha real.
        KnightCollectionResult result;
        Guid? identityAcquisitionId = null;
        if (source == KnightSourceType.MicrosoftEntraId)
        {
            // [AEGIS-MVP-EVIDENCE-FABRIC-01] Converge na Evidence Fabric: UMA aquisição real do Entra ID, que
            // persiste o snapshot NORMALIZADO compartilhado; o KNIGHT avalia os MESMOS fatos. Sem segundo cliente
            // Graph nem consulta duplicada. Conector não configurado/desabilitado/sem credencial → não executa.
            //
            // [AEGIS-ADM-01] O resultado agora vem RECONSTRUÍDO da aquisição PERSISTIDA do ADM, e a execução
            // guarda qual aquisição a sustentou. Nenhuma regra deste avaliador muda: ele continua recebendo o
            // mesmo contrato, com os mesmos fatos tipados — só que lidos da evidência gravada.
            var acquisition = await _identityEvidence.CollectAsync(ct);
            if (acquisition.CollectionResult is null)
                throw new KnightSourceNotConfiguredException(source);
            result = acquisition.CollectionResult;
            identityAcquisitionId = acquisition.AcquisitionId;
        }
        else
        {
            var configuration = await _config.ResolveAsync(tenantId, source, ct);
            if (source != KnightSourceType.Demo && !configuration.IsConfigured)
                throw new KnightSourceNotConfiguredException(source);

            var collector = _registry.Resolve(source);
            result = await collector.CollectAsync(new KnightCollectionContext(tenantId, configuration), ct);
        }

        // 3) Avaliação DETERMINÍSTICA dos indicadores aplicáveis à fonte sobre os fatos. Dado ausente → NotEvaluated.
        var evaluated = KnightIndicatorEvaluator.Evaluate(result.Facts, source);

        // 4) Score e cobertura pela fórmula PRÓPRIA do KNIGHT.
        var score = KnightScoreFormula.Compute(evaluated.Select(e => (e.Definition.Severity, e.Status)));

        // 5) Monta a execução + resultados (TenantId carimbado no SaveChanges, fail-closed).
        var run = new KnightAssessmentRun
        {
            Mode = source == KnightSourceType.Demo ? KnightAssessmentMode.Demo : KnightAssessmentMode.Live,
            SourceType = source,
            SourceState = result.State,
            Source = result.SourceLabel,
            // [AEGIS-KNIGHT-DURABLE-01] Estado inicial APENAS em memória: a primeira gravação já leva a
            // execução CONCLUÍDA (ver passo 6). Nunca existe uma linha em Running esperando pela IA.
            Status = KnightRunStatus.Running,
            CatalogVersion = KnightCatalog.Version,
            ScoreFormulaVersion = score.FormulaVersion,
            StartedAt = DateTimeOffset.UtcNow,
            Score = score.Score,
            Coverage = score.Coverage,
            PassedCount = score.PassedCount,
            ExposedCount = score.ExposedCount,
            MitigatedCount = score.MitigatedCount,
            NotEvaluatedCount = score.NotEvaluatedCount,
            ErrorCount = score.ErrorCount,
            NotApplicableCount = score.NotApplicableCount,
            CapabilitiesJson = JsonSerializer.Serialize(result.Capabilities, Json),
            // [AEGIS-ADM-01] Procedência da evidência: qual aquisição sustentou este veredito. Fica null nas
            // fontes que ainda não passam pelo ADM (Demo, Google Workspace) — e as execuções ANTERIORES a este
            // pacote permanecem null, nunca retropreenchidas com a coleta de hoje.
            IdentityAcquisitionId = identityAcquisitionId,
        };

        // [AEGIS-MVP-PRODUCT-02] Objetos afetados preservados por SINAL pela mesma coleta — indexados pelo
        // indicador que o mapa explicito de escopo aponta. Um sinal fora do escopo simplesmente nao gera
        // detalhe, e o indicador correspondente se declara "sem detalhe preservado".
        var affectedByIndicator = new Dictionary<string, KnightAffectedObjectEvidence>(StringComparer.Ordinal);
        foreach (var set in result.AffectedObjectSets)
        {
            var mapped = KnightAffectedObjectScope.IndicatorFor(set.Signal);
            if (mapped is not null) affectedByIndicator[mapped] = set;
        }

        foreach (var e in evaluated)
        {
            var indicator = new KnightIndicatorResult
            {
                // O Id da execução já existe em memória (Entity o gera na construção); carimbá-lo aqui deixa
                // o RunId denormalizado dos objetos afetados correto ANTES do SaveChanges.
                RunId = run.Id,
                IndicatorId = e.Definition.Id,
                Title = e.Definition.Title,
                Category = e.Definition.Category,
                Severity = e.Definition.Severity,
                Status = e.Status,
                Evidence = e.Evidence,
                AffectedObjectCount = e.AffectedObjectCount,
                NistCodes = e.Definition.NistCodes.ToList(),
                MitreTechniques = e.Definition.MitreTechniques.ToList(),
                Recommendation = e.Definition.Recommendation,
                SourceType = source,
                NotEvaluatedReason = e.NotEvaluatedReason,
                CollectedAt = result.CollectedAt,
            };

            // Só há "objetos afetados" quando o veredito sinalizou algum: num indicador CONFORME a contagem é
            // zero por definição, e anexar ali a lista de objetos observados faria a tabela contradizer o
            // número exibido ao lado dela.
            if (e.AffectedObjectCount > 0 && affectedByIndicator.TryGetValue(e.Definition.Id, out var evidence))
                AttachAffected(indicator, evidence);

            run.Indicators.Add(indicator);
        }

        // 6) A avaliação FECHA AQUI, antes de qualquer dependência da IA.
        //
        // [AEGIS-KNIGHT-DURABLE-01] Antes deste pacote a linha era gravada em Running, a narrativa era pedida
        // à IA com o MESMO token da requisição e só então o status virava Completed. Um cancelamento nessa
        // janela — o navegador cortando a requisição no seu próprio tempo limite — propagava a exceção e
        // deixava o registro em Running para sempre: nenhum caminho grava Failed e não há rotina que feche
        // execução órfã. O veredito determinístico, porém, JÁ ESTAVA PRONTO; o que faltava era só a narrativa.
        //
        // A ordem passa a dizer a verdade sobre o que é a avaliação: a narrativa determinística do
        // KnightAdvisoryFallback — a MESMA que já cobria a IA indisponível — entra junto com os resultados,
        // e a execução nasce Completed, com CompletedAt, numa única escrita. A IA vem depois, como
        // ENRIQUECIMENTO de algo que já é válido e recuperável.
        var advisoryInput = BuildAdvisoryInput(run, score, evaluated, result);
        var fallbackJson = JsonSerializer.Serialize(KnightAdvisoryFallback.Build(advisoryInput), Json);
        run.AdvisoryJson = fallbackJson;
        run.AdvisoryFromAi = false;
        run.Status = KnightRunStatus.Completed;
        run.CompletedAt = DateTimeOffset.UtcNow;

        // A ÚNICA escrita que a avaliação precisa — resultados, evidências congeladas, narrativa determinística,
        // status final e CompletedAt saem juntos, coerentes entre si.
        //
        // [AEGIS-ADM-02] Quando a execução CITA uma aquisição do ADM, a citação e a fixação daquela aquisição
        // entram na MESMA transação: a retenção operacional pode estar decidindo, agora, se aquela linha é
        // removível. Perguntar "ela ainda existe?" antes da transação seria uma foto vencida — a resposta
        // valeria até o instante seguinte. A trava COMPARTILHADA conflita com a EXCLUSIVA que a retenção toma
        // sobre os candidatos: um dos dois espera, e o que passa enxerga o estado já decidido.
        _db.KnightAssessmentRuns.Add(run);

        if (identityAcquisitionId is { } citada)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var fixada = await _identityAcquisitions.PinForReferenceAsync(citada, ct);

            if (!fixada.Exists)
                throw new InvalidOperationException(
                    $"A aquisição de identidade {citada} não está mais disponível para ser citada por esta "
                    + "avaliação. Gravar a execução assim produziria um veredito apontando para uma coleta "
                    + "inexistente — o oposto do que a procedência existe para garantir.");

            if (fixada.DetailRetiredAt is { } expiradoEm)
            {
                // [AEGIS-ADM-02] A linha está lá e está TRAVADA (a retenção não a remove por baixo desta
                // transação), mas o detalhe observado já expirou. Citar assim é legítimo — o cabeçalho, os
                // fatos agregados e a completude por conjunto continuam sustentando o veredito, e os objetos
                // afetados desta execução são congelados aqui mesmo, em KnightAffectedObjects, que nenhuma
                // retenção alcança. O que não pode acontecer é isso passar em silêncio: "a linha existe" e "a
                // evidência está íntegra" são afirmações diferentes.
                _log?.LogWarning(
                    "Execução do KNIGHT cita a aquisição de identidade {Aquisicao}, cujo detalhe operacional "
                    + "expirou por retenção em {ExpiradoEm}. O veredito segue reproduzível pelos fatos e pela "
                    + "completude por conjunto, e os objetos afetados ficam congelados nesta execução — mas a "
                    + "lista de objetos daquela coleta não é mais recuperável a partir dela.",
                    citada, expiradoEm);
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        else
        {
            await _db.SaveChangesAsync(ct);
        }

        // 7) IA CONSULTIVA (uma chamada, fora da transação) — ENRIQUECIMENTO de um resultado que já está
        //    gravado e concluído. Cancelamento, indisponibilidade ou falha de gravação aqui não apagam o
        //    veredito, não mudam status/score e não deixam nada pendente.
        await TryEnrichWithAiAsync(run, advisoryInput, fallbackJson, ct);

        return ToAssessment(run);
    }

    /// <summary>
    /// [AEGIS-KNIGHT-DURABLE-01] Leitura da ÚLTIMA avaliação. Devolve o último resultado CONCLUÍDO e,
    /// separadamente, a tentativa mais recente que NÃO concluiu — quando ela começou depois dele.
    ///
    /// As duas metades importam. Apresentar uma execução em <c>Running</c> como resultado mostraria uma
    /// avaliação sem narrativa e sem conclusão como se fosse a foto atual da postura; simplesmente
    /// ignorá-la faria uma avaliação antiga passar por atual, escondendo que houve uma tentativa depois
    /// dela. A leitura declara as duas coisas e deixa a tela dizer qual é qual.
    /// </summary>
    public async Task<KnightLatestAssessment> GetLatestAsync(CancellationToken ct = default)
    {
        // Ordenação por instante do lado do cliente, pelo mesmo motivo que a publicação de fotografias já
        // adota: o SQLite dos testes não traduz ORDER BY de DateTimeOffset. O conjunto por tenant é pequeno
        // (execuções de assessment) e só o cabeçalho é materializado — a execução escolhida é lida depois,
        // sozinha, com os indicadores.
        var headers = await _db.KnightAssessmentRuns.AsNoTracking()
            .Select(r => new { r.Id, r.Status, r.SourceType, r.Mode, r.StartedAt })
            .ToListAsync(ct);

        var latestHeader = headers
            .Where(r => r.Status == KnightRunStatus.Completed)
            .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id)
            .FirstOrDefault();

        // A tentativa só é PENDÊNCIA enquanto nenhum resultado a sucedeu: uma execução abandonada ANTES da
        // última concluída é história, e avisar sobre ela para sempre seria ruído, não informação.
        var unfinished = headers
            .Where(r => r.Status != KnightRunStatus.Completed)
            .Where(r => latestHeader is null || r.StartedAt > latestHeader.StartedAt)
            .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id)
            .Select(r => new KnightUnfinishedRun(r.Id, r.Status, r.SourceType, r.Mode, r.StartedAt))
            .FirstOrDefault();

        if (latestHeader is null) return new KnightLatestAssessment(null, unfinished);

        var latest = await _db.KnightAssessmentRuns
            .AsNoTracking().Include(r => r.Indicators)
            .FirstOrDefaultAsync(r => r.Id == latestHeader.Id, ct);

        return new KnightLatestAssessment(latest is null ? null : ToAssessment(latest), unfinished);
    }

    public async Task<KnightAssessment?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var run = await _db.KnightAssessmentRuns
            .AsNoTracking().Include(r => r.Indicators)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        return run is null ? null : ToAssessment(run);
    }

    public async Task<KnightSourcesStatus> GetSourcesStatusAsync(CancellationToken ct = default)
    {
        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException("Consulta de fontes KNIGHT sem tenant resolvido no contexto (fail-closed).");
        var availability = await _config.ListAvailabilityAsync(tenantId, ct);
        var real = availability
            .Select(a => new KnightSourceInfo(a.Source, a.Label, a.Configured, a.Enabled))
            .ToList();
        return new KnightSourcesStatus(DemoAvailable: true, real);
    }

    /// <inheritdoc />
    public async Task<KnightAffectedObjectsPage?> GetAffectedObjectsAsync(
        Guid runId, string indicatorId, int page, int pageSize, string? search, CancellationToken ct = default)
    {
        // Paginacao SANEADA no servidor: um pageSize absurdo vindo do cliente nao vira varredura de tabela.
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize < 1
            ? KnightAffectedObjectsPage.DefaultPageSize
            : Math.Min(pageSize, KnightAffectedObjectsPage.MaxPageSize);
        var id = (indicatorId ?? "").Trim();

        // O indicador e lido DENTRO da execucao pedida — e o Global Query Filter (fail-closed) faz de uma
        // execucao de outro tenant algo indistinguivel de inexistente. Nada aqui aciona coleta na fonte.
        var indicator = await _db.KnightIndicatorResults.AsNoTracking()
            .Where(i => i.RunId == runId && i.IndicatorId == id)
            .Select(i => new
            {
                i.Id,
                i.AffectedObjectCount,
                i.HasAffectedDetail,
                i.AffectedDetailComplete,
                i.AffectedDetailLimitation,
                i.CollectedAt,
            })
            .FirstOrDefaultAsync(ct);

        if (indicator is null) return null;

        if (!indicator.HasAffectedDetail)
        {
            // TRÊS ausências distintas, e a tela precisa dizer qual é qual:
            //   • o veredito não sinalizou objeto algum (contagem zero) → o conjunto vazio É a resposta certa,
            //     e vale tanto para uma execução nova quanto para uma antiga;
            //   • o achado está no escopo mas a execução é ANTERIOR à preservação → declara isso e não recebe
            //     a coleta atual, que seria o presente apresentado como prova do passado;
            //   • o achado não preserva detalhe nesta entrega.
            var inScope = KnightAffectedObjectScope.IsInScope(id);
            var missing = (inScope, indicator.AffectedObjectCount) switch
            {
                (true, 0) => KnightAffectedDetailState.Available,
                (true, _) => KnightAffectedDetailState.NotPreserved,
                _ => KnightAffectedDetailState.OutOfScope,
            };
            return KnightAffectedObjectsPage.Without(
                runId, id, missing, indicator.AffectedObjectCount, safePage, safeSize,
                indicator.AffectedDetailLimitation, indicator.CollectedAt);
        }

        var query = _db.KnightAffectedObjects.AsNoTracking()
            .Where(o => o.IndicatorResultId == indicator.Id);

        var totalPreserved = await query.CountAsync(ct);

        // BUSCA NO BANCO (nome, UPN ou identificador). O navegador nunca recebe milhares de objetos para
        // filtrar depois — e o servidor que filtra, conta e pagina.
        var term = (search ?? "").Trim();
        if (term.Length > 0)
        {
            var pattern = "%" + term + "%";
            query = query.Where(o =>
                EF.Functions.Like(o.ExternalId, pattern)
                || (o.DisplayName != null && EF.Functions.Like(o.DisplayName, pattern))
                || (o.UserPrincipalName != null && EF.Functions.Like(o.UserPrincipalName, pattern)));
        }

        var matchCount = term.Length > 0 ? await query.CountAsync(ct) : totalPreserved;

        var items = await query
            // Ordenacao deterministica e estavel entre paginas: nome quando existe, depois o identificador.
            .OrderBy(o => o.DisplayName ?? o.UserPrincipalName ?? o.ExternalId).ThenBy(o => o.ExternalId)
            .Skip((safePage - 1) * safeSize).Take(safeSize)
            .Select(o => new KnightAffectedObjectView(
                o.ExternalId, o.Kind, o.DisplayName, o.UserPrincipalName, o.Roles, o.Detail))
            .ToListAsync(ct);

        return new KnightAffectedObjectsPage(
            runId, id,
            indicator.AffectedDetailComplete ? KnightAffectedDetailState.Available : KnightAffectedDetailState.Partial,
            indicator.AffectedObjectCount, totalPreserved, matchCount, safePage, safeSize, items,
            indicator.AffectedDetailLimitation, indicator.CollectedAt);
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    /// <summary>
    /// Anexa ao resultado do indicador os objetos que sustentam o veredito, deduplicados pela MESMA chave que
    /// o coletor usou (identificador do objeto na fonte). A completude e DECLARADA, nao deduzida: a lista fica
    /// incompleta quando a coleta disse que nao enumerou tudo OU quando o numero de objetos preservados
    /// diverge da contagem do veredito — e essa divergencia vira uma limitacao VISIVEL, em vez de uma tabela
    /// que silenciosamente contradiz o numero exibido ao lado dela.
    /// </summary>
    private static void AttachAffected(KnightIndicatorResult indicator, KnightAffectedObjectEvidence evidence)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in evidence.Objects)
        {
            if (string.IsNullOrWhiteSpace(o.ExternalId) || !seen.Add(o.ExternalId)) continue;
            indicator.AffectedObjects.Add(new KnightAffectedObject
            {
                RunId = indicator.RunId,
                IndicatorId = indicator.IndicatorId,
                ExternalId = o.ExternalId,
                Kind = o.Kind,
                DisplayName = o.DisplayName,
                UserPrincipalName = o.UserPrincipalName,
                Roles = o.Roles?.ToList() ?? new List<string>(),
                Detail = o.Detail,
            });
        }

        var preserved = indicator.AffectedObjects.Count;
        var matchesCount = preserved == indicator.AffectedObjectCount;

        indicator.HasAffectedDetail = true;
        indicator.AffectedDetailComplete = evidence.IsComplete && matchesCount;
        indicator.AffectedDetailLimitation = matchesCount
            ? evidence.Limitation
            : string.Join(" ", new[]
            {
                evidence.Limitation,
                $"A lista preservada tem {preserved} objeto(s) e o veredito contou {indicator.AffectedObjectCount} — "
                + "a diferenca e declarada aqui em vez de ser escondida.",
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    /// <summary>
    /// [AEGIS-KNIGHT-DURABLE-01] Tenta ENRIQUECER com a narrativa da IA uma avaliação que já está gravada e
    /// concluída, com a narrativa determinística no lugar. Nada aqui pode reprovar, apagar ou deixar
    /// pendente o veredito: qualquer desfecho ruim simplesmente mantém o que já está no banco.
    ///
    /// Continua sendo UMA chamada, no escopo da requisição. Não há tarefa solta, nem <c>DbContext</c>
    /// sobrevivendo ao escopo, nem timeout ampliado — o que mudou foi de que a conclusão depende.
    /// </summary>
    private async Task TryEnrichWithAiAsync(
        KnightAssessmentRun run, KnightAdvisoryInput input, string fallbackJson, CancellationToken ct)
    {
        KnightAdvisoryResult enrichment;
        try
        {
            enrichment = await _advisory.GenerateAsync(input, ct);
        }
        catch (OperationCanceledException)
        {
            // O chamador desistiu no meio da narrativa. Antes isto abandonava o registro em Running; agora
            // é só um enriquecimento que não aconteceu — a avaliação já está concluída e recuperável.
            _log?.LogInformation(
                "Narrativa consultiva da execução {Run} do KNIGHT cancelada pelo chamador. O veredito "
                + "determinístico já está concluído e gravado com a narrativa determinística.", run.Id);
            return;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex,
                "Gerador de narrativa consultiva do KNIGHT falhou na execução {Run}; a avaliação mantém a "
                + "narrativa determinística. Falha consultiva NÃO é falha da avaliação.", run.Id);
            return;
        }

        // O próprio gerador já caiu no fallback (IA indisponível/resposta inválida): o que está gravado é
        // exatamente isso. Uma segunda gravação idêntica não acrescentaria nada.
        if (!enrichment.FromAi) return;

        run.AdvisoryJson = JsonSerializer.Serialize(enrichment.Advisory, Json);
        run.AdvisoryFromAi = true;

        try
        {
            // CancellationToken.None de propósito, e só aqui: a linha já é durável, este UPDATE toca duas
            // colunas consultivas e a narrativa JÁ FOI produzida e paga. Honrar um cancelamento neste ponto
            // descartaria trabalho concluído sem proteger coisa alguma. A operação continua dentro do escopo
            // da requisição — não é tarefa em segundo plano.
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // O enriquecimento não chegou ao banco. O que o chamador recebe tem de ser o que está GRAVADO:
            // devolver a narrativa da IA aqui apresentaria como salva uma narrativa que se perdeu.
            run.AdvisoryJson = fallbackJson;
            run.AdvisoryFromAi = false;
            _log?.LogWarning(ex,
                "Falha ao gravar o enriquecimento consultivo da execução {Run} do KNIGHT. O resultado "
                + "anteriormente confirmado (veredito + narrativa determinística) permanece intacto.", run.Id);
        }
    }

    private static KnightAdvisoryInput BuildAdvisoryInput(
        KnightAssessmentRun run, KnightScoreResult score,
        IReadOnlyList<KnightEvaluatedIndicator> evaluated, KnightCollectionResult result)
    {
        var limitations = result.Capabilities
            .Where(c => c.Outcome != KnightCapabilityOutcome.Collected)
            .Select(c => $"{CapabilityLabel(c.Capability)}: {c.Detail ?? c.Outcome.ToString()}")
            .ToList();

        return new KnightAdvisoryInput(
            run.SourceType,
            run.Mode,
            score.Score,
            score.Coverage,
            evaluated.Select(e => new KnightAdvisoryIndicator(
                e.Definition.Id, e.Definition.Title, e.Definition.Category, e.Definition.Severity,
                e.Status, e.Evidence, e.AffectedObjectCount,
                e.Definition.NistCodes, e.Definition.MitreTechniques)).ToList(),
            limitations);
    }

    private KnightAssessment ToAssessment(KnightAssessmentRun run)
    {
        var indicators = run.Indicators
            .OrderBy(i => i.IndicatorId, StringComparer.Ordinal)
            .Select(i => new KnightIndicatorView(
                i.IndicatorId, i.Title, i.Category, i.Severity, i.Status, i.Evidence, i.AffectedObjectCount,
                i.NistCodes, i.MitreTechniques, i.Recommendation, i.CollectedAt, i.SourceType, i.NotEvaluatedReason,
                i.HasAffectedDetail, i.AffectedDetailComplete, i.AffectedDetailLimitation))
            .ToList();

        return new KnightAssessment(
            run.Id, run.Mode, run.SourceType, run.SourceState, run.Source, run.Status,
            run.CatalogVersion, run.ScoreFormulaVersion, run.StartedAt, run.CompletedAt, run.Score, run.Coverage,
            run.PassedCount, run.ExposedCount, run.MitigatedCount,
            run.NotEvaluatedCount, run.ErrorCount, run.NotApplicableCount,
            indicators, DeserializeCapabilities(run.CapabilitiesJson),
            DeserializeAdvisory(run.AdvisoryJson), run.AdvisoryFromAi);
    }

    /// <summary>
    /// [AEGIS-MVP-PRODUCT-03] Delega à autoridade ÚNICA de leitura das capacidades. A validação de remediação
    /// lê o MESMO JSON para decidir suficiência de evidência: duas desserializações independentes divergiriam,
    /// e a divergência apareceria como "capacidade ausente" — bloqueando uma comprovação legítima.
    /// </summary>
    private static IReadOnlyList<KnightCapabilityStatus> DeserializeCapabilities(string? json) =>
        KnightCapabilitiesJson.Deserialize(json);

    private KnightAdvisory? DeserializeAdvisory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<KnightAdvisory>(json, Json);
        }
        catch (JsonException ex)
        {
            _log?.LogWarning(ex, "AdvisoryJson do KNIGHT ilegível; retornando sem narrativa.");
            return null;
        }
    }

    private static string CapabilityLabel(KnightCapability capability) => capability switch
    {
        KnightCapability.PrivilegedRoleInventory => "Inventário de papéis privilegiados",
        KnightCapability.MfaRegistration => "Registro de MFA",
        KnightCapability.GuestAccounts => "Contas de convidado",
        KnightCapability.ConditionalAccessPolicies => "Políticas de acesso condicional",
        KnightCapability.ApplicationInventory => "Inventário de aplicações (credenciais)",
        KnightCapability.ApplicationPermissions => "Permissões de aplicativo concedidas",
        KnightCapability.ApplicationConsents => "Consentimentos delegados (tenant-wide)",
        KnightCapability.ServiceAccountExemptions => "Isenções de contas de serviço",
        KnightCapability.SecurityBaseline => "Baseline de segurança",
        KnightCapability.BreakGlassDesignation => "Designação de contas de emergência",
        KnightCapability.DirectoryUsers => "Diretório de usuários (2SV/superadmins)",
        KnightCapability.DirectoryGroups => "Grupos e membros externos",
        KnightCapability.DriveSharingAudit => "Auditoria de compartilhamento no Drive",
        KnightCapability.OAuthTokenAudit => "Auditoria de autorizações OAuth",
        _ => capability.ToString(),
    };
}
