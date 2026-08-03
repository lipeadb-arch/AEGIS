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
    DateTimeOffset CollectedAt);

/// <summary>
/// Um assessment KNIGHT completo, na visão de leitura da aplicação: a execução, os indicadores e o resumo
/// consultivo (com a procedência — IA ou fallback). O score é ANULÁVEL e é o score KNIGHT, distinto do AEGIS Score.
/// </summary>
public sealed record KnightAssessment(
    Guid Id,
    KnightAssessmentMode Mode,
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
    KnightAdvisory? Advisory,
    bool AdvisoryFromAi);

/// <summary>
/// Serviço de aplicação DEDICADO do AEGIS KNIGHT. Orquestra a execução persistida do assessment: coleta do
/// snapshot (provedor), avaliação determinística dos indicadores, cálculo de score/cobertura, persistência,
/// resumo consultivo pela IA (com fallback) e leitura por tenant. Todas as operações respeitam o isolamento
/// de tenant (Global Query Filter fail-closed).
/// </summary>
public interface IAegisKnightAssessmentService
{
    /// <summary>
    /// Executa um assessment de DEMONSTRAÇÃO ponta a ponta: cria a execução, coleta o snapshot sintético,
    /// avalia deterministicamente, calcula score/cobertura, persiste, tenta gerar a narrativa consultiva e
    /// conclui MESMO SE a IA estiver indisponível. Devolve o assessment completo.
    /// </summary>
    Task<KnightAssessment> RunDemoAssessmentAsync(CancellationToken ct = default);

    /// <summary>Último assessment do tenant do contexto, ou <c>null</c> se ainda não houver nenhum.</summary>
    Task<KnightAssessment?> GetLatestAsync(CancellationToken ct = default);

    /// <summary>Assessment por Id, restrito ao tenant do contexto (<c>null</c> quando inexistente ou de outro tenant).</summary>
    Task<KnightAssessment?> GetByIdAsync(Guid id, CancellationToken ct = default);
}
