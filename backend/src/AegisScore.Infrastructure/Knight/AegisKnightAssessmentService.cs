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
    private readonly ITenantContext _tenant;
    private readonly ILogger<AegisKnightAssessmentService>? _log;

    public AegisKnightAssessmentService(
        AegisScoreDbContext db,
        IKnightCollectorRegistry registry,
        IKnightSourceConfigurationProvider config,
        IKnightAdvisoryGenerator advisory,
        ITenantContext tenant,
        ILogger<AegisKnightAssessmentService>? log = null)
    {
        _db = db;
        _registry = registry;
        _config = config;
        _advisory = advisory;
        _tenant = tenant;
        _log = log;
    }

    public Task<KnightAssessment> RunDemoAssessmentAsync(CancellationToken ct = default) =>
        RunAssessmentAsync(KnightSourceType.Demo, ct);

    public async Task<KnightAssessment> RunAssessmentAsync(KnightSourceType source, CancellationToken ct = default)
    {
        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException("Execução do assessment KNIGHT sem tenant resolvido no contexto (fail-closed).");

        // 1) Resolve a CONFIGURAÇÃO da fonte. Fonte real sem configuração NÃO executa e NÃO cai para Demo.
        var configuration = await _config.ResolveAsync(tenantId, source, ct);
        if (source != KnightSourceType.Demo && !configuration.IsConfigured)
            throw new KnightSourceNotConfiguredException(source);

        var collector = _registry.Resolve(source);

        // 2) COLETA pela fonte → fatos normalizados + estado + capacidades. O coletor NUNCA devolve dados
        //    sintéticos numa falha real: devolve o estado real (AuthenticationFailure/Partial/…).
        var result = await collector.CollectAsync(new KnightCollectionContext(tenantId, configuration), ct);

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
                SourceType = source,
                NotEvaluatedReason = e.NotEvaluatedReason,
                CollectedAt = result.CollectedAt,
            });
        }

        // 6) Persiste o veredito DETERMINÍSTICO ANTES da IA (durável já em Running).
        _db.KnightAssessmentRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        // 7) IA CONSULTIVA (uma chamada, fora da transação) — nunca altera vereditos; falha → fallback.
        var advisoryResult = await GenerateAdvisorySafeAsync(BuildAdvisoryInput(run, score, evaluated, result), ct);
        run.AdvisoryJson = JsonSerializer.Serialize(advisoryResult.Advisory, Json);
        run.AdvisoryFromAi = advisoryResult.FromAi;

        // 8) Conclui e grava novamente.
        run.Status = KnightRunStatus.Completed;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return ToAssessment(run);
    }

    public async Task<KnightAssessment?> GetLatestAsync(CancellationToken ct = default)
    {
        var run = await _db.KnightAssessmentRuns
            .AsNoTracking().Include(r => r.Indicators)
            .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        return run is null ? null : ToAssessment(run);
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

    // ---- Helpers ----------------------------------------------------------------------------------

    private async Task<KnightAdvisoryResult> GenerateAdvisorySafeAsync(KnightAdvisoryInput input, CancellationToken ct)
    {
        try
        {
            return await _advisory.GenerateAsync(input, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Gerador de narrativa consultiva do KNIGHT falhou; aplicando fallback determinístico.");
            return new KnightAdvisoryResult(KnightAdvisoryFallback.Build(input), FromAi: false);
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
                i.NistCodes, i.MitreTechniques, i.Recommendation, i.CollectedAt, i.SourceType, i.NotEvaluatedReason))
            .ToList();

        return new KnightAssessment(
            run.Id, run.Mode, run.SourceType, run.SourceState, run.Source, run.Status,
            run.CatalogVersion, run.ScoreFormulaVersion, run.StartedAt, run.CompletedAt, run.Score, run.Coverage,
            run.PassedCount, run.ExposedCount, run.MitigatedCount,
            run.NotEvaluatedCount, run.ErrorCount, run.NotApplicableCount,
            indicators, DeserializeCapabilities(run.CapabilitiesJson),
            DeserializeAdvisory(run.AdvisoryJson), run.AdvisoryFromAi);
    }

    private IReadOnlyList<KnightCapabilityStatus> DeserializeCapabilities(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<KnightCapabilityStatus>();
        try
        {
            return JsonSerializer.Deserialize<List<KnightCapabilityStatus>>(json, Json) ?? new();
        }
        catch (JsonException)
        {
            return Array.Empty<KnightCapabilityStatus>();
        }
    }

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
