using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

/// <summary>Um indicador de uma execução KNIGHT, na visão de leitura da aplicação (nunca a entidade crua).</summary>
public sealed record KnightIndicatorView(
    string IndicatorId,
    string Title,
    KnightIndicatorCategory Category,
    SeverityLevel Severity,
    KnightIndicatorStatus Status,
    string Evidence,
    int AffectedObjectCount,
    IReadOnlyList<string> NistCodes,
    IReadOnlyList<string> MitreTechniques,
    string Recommendation,
    DateTimeOffset CollectedAt,
    KnightSourceType SourceType,
    string? NotEvaluatedReason);

/// <summary>
/// Um assessment KNIGHT completo, na visão de leitura da aplicação: a execução, a FONTE e seu estado, os
/// indicadores, o estado por capacidade e o resumo consultivo (com procedência — IA ou fallback). O score é
/// ANULÁVEL e é o score KNIGHT, distinto do AEGIS Score.
/// </summary>
public sealed record KnightAssessment(
    Guid Id,
    KnightAssessmentMode Mode,
    KnightSourceType SourceType,
    KnightSourceState SourceState,
    string Source,
    KnightRunStatus Status,
    string CatalogVersion,
    string ScoreFormulaVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    double? Score,
    double Coverage,
    int PassedCount,
    int ExposedCount,
    int MitigatedCount,
    int NotEvaluatedCount,
    int ErrorCount,
    int NotApplicableCount,
    IReadOnlyList<KnightIndicatorView> Indicators,
    IReadOnlyList<KnightCapabilityStatus> Capabilities,
    KnightAdvisory? Advisory,
    bool AdvisoryFromAi);

/// <summary>Disponibilidade das fontes para o tenant — o que a UI usa para oferecer Demo × execução real.</summary>
public sealed record KnightSourceInfo(KnightSourceType Source, string Label, bool Configured, bool Enabled);

/// <summary>Estado das fontes KNIGHT do tenant: Demo sempre disponível; fontes reais conforme configuração.</summary>
public sealed record KnightSourcesStatus(bool DemoAvailable, IReadOnlyList<KnightSourceInfo> RealSources);

/// <summary>Tentativa de executar uma fonte REAL sem configuração aplicável — o serviço NÃO cai para Demo.</summary>
public sealed class KnightSourceNotConfiguredException : Exception
{
    public KnightSourceType SourceType { get; }
    public KnightSourceNotConfiguredException(KnightSourceType source)
        : base($"Fonte {source} não está configurada para este tenant.") => SourceType = source;
}

/// <summary>
/// Serviço de aplicação DEDICADO do AEGIS KNIGHT — MULTICOLETOR. Orquestra a execução persistida: resolve a
/// configuração da fonte, coleta pelo coletor da fonte, avalia deterministicamente os fatos normalizados,
/// calcula score/cobertura, persiste (com fonte/estado), gera o resumo consultivo (com fallback) e lê por
/// tenant. Todas as operações respeitam o isolamento de tenant (Global Query Filter fail-closed).
/// </summary>
public interface IAegisKnightAssessmentService
{
    /// <summary>Executa um assessment de DEMONSTRAÇÃO (fonte Demo, sintética) ponta a ponta.</summary>
    Task<KnightAssessment> RunDemoAssessmentAsync(CancellationToken ct = default);

    /// <summary>
    /// Executa um assessment da FONTE indicada. Para fontes reais, exige configuração aplicável: se ausente,
    /// lança <see cref="KnightSourceNotConfiguredException"/> (NUNCA cai para Demo). Uma falha real da coleta
    /// (permissão/indisponibilidade) é persistida com o estado real, sem substituição por dados sintéticos.
    /// </summary>
    Task<KnightAssessment> RunAssessmentAsync(KnightSourceType source, CancellationToken ct = default);

    /// <summary>Último assessment do tenant do contexto, ou <c>null</c> se ainda não houver nenhum.</summary>
    Task<KnightAssessment?> GetLatestAsync(CancellationToken ct = default);

    /// <summary>Assessment por Id, restrito ao tenant do contexto (<c>null</c> quando inexistente ou de outro tenant).</summary>
    Task<KnightAssessment?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Disponibilidade das fontes para o tenant (Demo sempre; reais conforme configuração).</summary>
    Task<KnightSourcesStatus> GetSourcesStatusAsync(CancellationToken ct = default);
}
