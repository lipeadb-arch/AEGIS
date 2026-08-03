using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Knight;

/// <summary>
/// Serviço de aplicação do AEGIS KNIGHT. Orquestra a execução persistida do assessment de identidade e
/// exposição: coleta o snapshot pelo provedor, avalia os indicadores de forma DETERMINÍSTICA
/// (<see cref="KnightIndicatorEvaluator"/>), calcula score e cobertura pela fórmula PRÓPRIA do KNIGHT
/// (<see cref="KnightScoreFormula"/> — nunca a global aegis-score-v1), persiste, tenta gerar a narrativa
/// consultiva pela IA (com fallback determinístico) e conclui MESMO SE a IA estiver indisponível.
///
/// A IA jamais decide status, severidade, score, cobertura ou mapeamento — só interpreta/prioriza. Persiste
/// no <see cref="AegisScoreDbContext"/> com Global Query Filter + stamping de tenant fail-closed. Não grava
/// segredo, token nem payload bruto sensível.
/// </summary>
public sealed class AegisKnightAssessmentService : IAegisKnightAssessmentService
{
    private static readonly JsonSerializerOptions AdvisoryJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AegisScoreDbContext _db;
    private readonly IKnightPostureProvider _provider;
    private readonly IKnightAdvisoryGenerator _advisory;
    private readonly ITenantContext _tenant;
    private readonly ILogger<AegisKnightAssessmentService>? _log;

    public AegisKnightAssessmentService(
        AegisScoreDbContext db,
        IKnightPostureProvider provider,
        IKnightAdvisoryGenerator advisory,
        ITenantContext tenant,
        ILogger<AegisKnightAssessmentService>? log = null)
    {
        _db = db;
        _provider = provider;
        _advisory = advisory;
        _tenant = tenant;
        _log = log;
    }

    public async Task<KnightAssessment> RunDemoAssessmentAsync(CancellationToken ct = default)
    {
        // Fail-fast: sem tenant resolvido no contexto, nada é coletado nem persistido (defesa em profundidade;
        // o SaveChanges do DbContext também barra).
        _ = _tenant.TenantId
            ?? throw new TenantSecurityException(
                "Execução do assessment KNIGHT sem tenant resolvido no contexto (fail-closed).");

        // 1) Coleta o snapshot (Demo = sintético, sem rede; nunca consultou Graph/AD/Okta).
        var snapshot = await _provider.CollectAsync(_tenant.TenantId!.Value, ct);

        // 2) Avaliação DETERMINÍSTICA dos indicadores (pura).
        var evaluated = KnightIndicatorEvaluator.Evaluate(snapshot);

        // 3) Score e cobertura pela fórmula PRÓPRIA do KNIGHT.
        var score = KnightScoreFormula.Compute(evaluated.Select(e => (e.Definition.Severity, e.Status)));

        // 4) Monta a execução + resultados (o TenantId é carimbado no SaveChanges, fail-closed).
        var run = new KnightAssessmentRun
        {
            Mode = _provider.Mode,
            Source = snapshot.Source,
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
        };

        foreach (var e in evaluated)
        {
            run.Indicators.Add(new KnightIndicatorResult
            {
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
                CollectedAt = snapshot.CollectedAt,
            });
        }

        // 5) IA CONSULTIVA (uma chamada) — nunca altera os vereditos; a falha NÃO reprova a execução.
        var advisoryResult = await GenerateAdvisorySafeAsync(BuildAdvisoryInput(snapshot.Mode, score, evaluated), ct);
        run.AdvisoryJson = JsonSerializer.Serialize(advisoryResult.Advisory, AdvisoryJson);
        run.AdvisoryFromAi = advisoryResult.FromAi;

        // 6) Conclui e persiste (cascade run → indicadores num único SaveChanges).
        run.Status = KnightRunStatus.Completed;
        run.CompletedAt = DateTimeOffset.UtcNow;

        _db.KnightAssessmentRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        return ToAssessment(run);
    }

    public async Task<KnightAssessment?> GetLatestAsync(CancellationToken ct = default)
    {
        var run = await _db.KnightAssessmentRuns
            .AsNoTracking()
            .Include(r => r.Indicators)
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        return run is null ? null : ToAssessment(run);
    }

    public async Task<KnightAssessment?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        // O Global Query Filter (fail-closed) restringe ao tenant do contexto: um Id de outro tenant não é
        // encontrado (retorna null → 404 no controller), nunca vaza.
        var run = await _db.KnightAssessmentRuns
            .AsNoTracking()
            .Include(r => r.Indicators)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        return run is null ? null : ToAssessment(run);
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private async Task<KnightAdvisoryResult> GenerateAdvisorySafeAsync(KnightAdvisoryInput input, CancellationToken ct)
    {
        try
        {
            return await _advisory.GenerateAsync(input, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Defesa extra: mesmo que a impl do gerador lance, o assessment não se perde — fallback determinístico.
            _log?.LogWarning(ex, "Gerador de narrativa consultiva do KNIGHT falhou; aplicando fallback determinístico.");
            return new KnightAdvisoryResult(KnightAdvisoryFallback.Build(input), FromAi: false);
        }
    }

    private static KnightAdvisoryInput BuildAdvisoryInput(
        KnightAssessmentMode mode, KnightScoreResult score, IReadOnlyList<KnightEvaluatedIndicator> evaluated) =>
        new(
            mode,
            score.Score,
            score.Coverage,
            evaluated.Select(e => new KnightAdvisoryIndicator(
                e.Definition.Id,
                e.Definition.Title,
                e.Definition.Category,
                e.Definition.Severity,
                e.Status,
                e.Evidence,
                e.AffectedObjectCount,
                e.Definition.NistCodes,
                e.Definition.MitreTechniques)).ToList());

    private KnightAssessment ToAssessment(KnightAssessmentRun run)
    {
        var indicators = run.Indicators
            .OrderBy(i => i.IndicatorId, StringComparer.Ordinal)
            .Select(i => new KnightIndicatorView(
                i.IndicatorId, i.Title, i.Category, i.Severity, i.Status, i.Evidence, i.AffectedObjectCount,
                i.NistCodes, i.MitreTechniques, i.Recommendation, i.CollectedAt))
            .ToList();

        return new KnightAssessment(
            run.Id, run.Mode, run.Source, run.Status, run.CatalogVersion, run.ScoreFormulaVersion,
            run.StartedAt, run.CompletedAt, run.Score, run.Coverage,
            run.PassedCount, run.ExposedCount, run.MitigatedCount,
            run.NotEvaluatedCount, run.ErrorCount, run.NotApplicableCount,
            indicators, DeserializeAdvisory(run.AdvisoryJson), run.AdvisoryFromAi);
    }

    private KnightAdvisory? DeserializeAdvisory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<KnightAdvisory>(json, AdvisoryJson);
        }
        catch (JsonException ex)
        {
            _log?.LogWarning(ex, "AdvisoryJson do KNIGHT ilegível; retornando sem narrativa.");
            return null;
        }
    }
}
