using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Scoring;

namespace AegisScore.Application.Queries;

/// <summary>
/// [AEGIS-AUD-021/027/032] Projeção ÚNICA de leitura do workspace — a MESMA autoridade de postura para o
/// Dashboard executivo e as seis Funções NIST, de modo que nenhuma tela recalcule score, cobertura ou
/// contagens de forma concorrente. TUDO deriva da fórmula oficial <see cref="AegisScoreFormulaV1"/>
/// (aegis-score-v1) sobre o catálogo do framework ATIVO e o <c>TenantControlState</c> do tenant ambiente.
/// Métricas de OUTRA natureza (maturidade CMMI, ICR) NÃO entram aqui — não são o AEGIS Score.
/// </summary>
public sealed record WorkspacePostureDto(
    WorkspaceOverallDto Overall,
    IReadOnlyList<FunctionPostureDto> Functions,
    ConnectorHealthSummaryDto Connectors);

/// <summary>Postura consolidada do tenant (todas as Funções) pela autoridade aegis-score-v1.</summary>
/// <param name="EvaluationState">"Evaluated" ou "NotEvaluated" — separa "sem score" de "0%".</param>
/// <param name="Percentage">Score de postura — <c>null</c> quando nada foi avaliado (NUNCA 0% por ausência).</param>
/// <param name="CoveragePercentage">Peso avaliado / peso elegível do catálogo ativo (eixo distinto do score).</param>
/// <param name="LatestEvidenceAt">Instante da avaliação mais recente (a "recência" da evidência).</param>
public sealed record WorkspaceOverallDto(
    string FormulaVersion,
    string EvaluationState,
    double? Percentage,
    double CoveragePercentage,
    int AchievedScore,
    int EvaluatedMaxScore,
    int EligibleMaxScore,
    int EligibleControls,
    int EvaluatedControls,
    int CompliantControls,
    int NonCompliantControls,
    int MitigatedControls,
    int NotEvaluatedControls,
    IReadOnlyList<SeverityCountDto> Severities,
    DateTimeOffset? LatestEvidenceAt);

/// <summary>Resumo de UMA Função NIST (GV/ID/PR/DE/RS/RC) — mesmo contrato de postura da visão geral.</summary>
public sealed record FunctionPostureDto(
    string Code,
    string Name,
    string EvaluationState,
    double? Percentage,
    double CoveragePercentage,
    int EligibleControls,
    int EvaluatedControls,
    int CompliantControls,
    int NonCompliantControls,
    int MitigatedControls,
    int NotEvaluatedControls);

/// <summary>Contagem de controles por severidade-proxy (derivada do status, ver <c>SeverityLevels.FromStatus</c>).</summary>
public sealed record SeverityCountDto(string Severity, int Count);

/// <summary>
/// Saúde e recência dos conectores do tenant. Saúde é OPERACIONAL: <see cref="Healthy"/>, <see cref="Degraded"/>,
/// <see cref="Failed"/> e <see cref="NeverSynced"/> contam SOMENTE conectores HABILITADOS e particionam
/// <see cref="Enabled"/>. Um conector nunca sincronizado (<c>EverSynced=false</c>) é <see cref="NeverSynced"/> —
/// jamais saudável, ainda que o <c>LastStatus</c> esteja incoerentemente como Healthy. Conector DESABILITADO
/// fica de fora do denominador operacional (não é falha) — aparece só em <see cref="Disabled"/>.
/// </summary>
public sealed record ConnectorHealthSummaryDto(
    int Configured,
    int Enabled,
    int Disabled,
    int Healthy,
    int Degraded,
    int Failed,
    int NeverSynced,
    DateTimeOffset? LastSyncAt,
    IReadOnlyList<ConnectorHealthItemDto> Items);

public sealed record ConnectorHealthItemDto(
    Guid Id,
    string DisplayName,
    string Provider,
    string Capability,
    string Status,
    DateTimeOffset? LastSyncAt,
    bool EverSynced,
    bool Enabled);

/// <summary>
/// Consulta de leitura da projeção única do workspace. O tenant é IMPLÍCITO (fail-closed via ITenantContext
/// + Global Query Filter) — nunca por parâmetro; sem tenant ambiente, a projeção é vazia.
/// </summary>
public interface IWorkspacePostureQuery
{
    Task<WorkspacePostureDto> GetAsync(CancellationToken ct = default);
}
