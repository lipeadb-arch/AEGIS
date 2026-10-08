using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Application.Nist;

// [AEGIS-AUDITOR-CONTEXT-01] Evidências de UMA avaliação · rodada · escopo do AEGIS NIST, calculadas pelos registros (sem IA): a
// relação KNIGHT → NIST (vinculada × disponível para revisão × não avaliada), as lacunas de evidência, os tratamentos ligados e os
// vínculos de documentos e do inventário. Leitura apenas — vincular continua sendo a gravação da subcategoria (LinkEvidenceAsync),
// com a confirmação do assessor. Uma relação não aprova o requisito; a contagem é sobre o universo informado, sem duplicar
// um mesmo controle técnico vinculado a várias subcategorias.

/// <summary>Uma execução do KNIGHT considerada (a concluída mais recente de cada fonte e modo).</summary>
public sealed record NistKnightRunView(
    Guid RunId,
    string Source,
    string SourceLabel,
    string Mode,
    string SourceState,
    string CatalogVersion,
    DateTimeOffset? CompletedAt,
    bool IsDemo,
    bool Partial);

/// <summary>
/// Um resultado técnico relacionado a uma subcategoria. <paramref name="EvidenceId"/> preenchido = VINCULADO pelo assessor nesta rodada;
/// nulo = disponível para revisão (mapeamento explícito do catálogo do KNIGHT). <paramref name="NewerStatus"/>: há execução mais recente
/// da mesma fonte com este controle, e o resultado dela (o vínculo guarda a execução de origem).
/// </summary>
public sealed record NistTechnicalEvidenceView(
    Guid? EvidenceId,
    Guid KnightRunId,
    string KnightIndicatorId,
    string Title,
    string Status,
    string StatusLabel,
    string Severity,
    string SourceLabel,
    DateTimeOffset? CollectedAt,
    bool IsDemo,
    bool PartialCollection,
    string? NewerStatus,
    Guid? NewerRunId,
    string Limitation);

/// <summary>Tratamento ligado à subcategoria: plano de achado NIST da rodada ou plano de um achado do KNIGHT relacionado.</summary>
public sealed record NistLinkedPlanView(
    Guid PlanId,
    string Origin,
    string Title,
    string Status,
    string StatusLabel,
    bool IsOverdue,
    bool IsActive,
    Guid? NistFindingId,
    string? KnightIndicatorId);

public sealed record NistCorrelationRowView(
    string Code,
    string Title,
    string FunctionCode,
    string State,
    int? CurrentLevel,
    int? TargetLevel,
    bool HumanConfirmed,
    IReadOnlyList<NistTechnicalEvidenceView> LinkedTechnical,
    IReadOnlyList<NistTechnicalEvidenceView> Candidates,
    /// <summary>Controles do KNIGHT mapeados para a subcategoria sem resultado avaliado (não avaliado ou erro de leitura).</summary>
    int TechnicalNotEvaluated,
    /// <summary>Evidências vigentes de qualquer origem (técnica, documento, inventário, registro do analista).</summary>
    int LinkedEvidence,
    /// <summary>Nível atual confirmado sem nenhuma evidência vinculada na rodada.</summary>
    bool EvaluatedWithoutEvidence,
    int OpenFindings,
    IReadOnlyList<NistLinkedPlanView> Plans);

/// <summary>Contagens sobre TODAS as subcategorias do escopo (nunca sobre uma lista truncada).</summary>
public sealed record NistCorrelationSummaryView(
    int SubcategoriesInScope,
    int SubcategoriesWithKnightMapping,
    int LinkedTechnicalLinks,
    int LinkedTechnicalControls,
    int SubcategoriesWithLinkedTechnical,
    int CandidateLinks,
    int CandidateControls,
    int SubcategoriesWithCandidates,
    int SubcategoriesWithTechnicalNotEvaluated,
    int SubcategoriesEvaluatedWithoutEvidence,
    int Plans,
    int PlansOverdue);

/// <summary>Documento da biblioteca vinculado como evidência nesta rodada e escopo.</summary>
public sealed record NistDocumentLinkView(Guid EvidenceId, string Code, Guid DocumentId, string Title, DateTimeOffset LinkedAt, string? RecordedByName);

/// <summary>Retrato do inventário vinculado a uma subcategoria de gestão de ativos.</summary>
public sealed record NistInventoryLinkView(Guid EvidenceId, string Title, DateTimeOffset CollectedAt, string? OriginScope, string? RecordedByName);

/// <summary>Subcategoria de gestão de ativos (ID.AM) e os retratos do inventário vinculados a ela nesta rodada.</summary>
public sealed record NistInventoryTargetView(
    string Code, string Title, string State, int? CurrentLevel, bool HumanConfirmed, IReadOnlyList<NistInventoryLinkView> Linked);

public sealed record NistEvidenceOverviewView(
    Guid AssessmentId,
    Guid CycleId,
    Guid ScopeId,
    string AssessmentName,
    string CycleName,
    string CycleStatus,
    string ScopeName,
    DateTimeOffset AsOf,
    IReadOnlyList<NistKnightRunView> KnightRuns,
    NistCorrelationSummaryView Summary,
    IReadOnlyList<NistCorrelationRowView> Rows,
    IReadOnlyList<NistDocumentLinkView> DocumentLinks,
    IReadOnlyList<NistInventoryTargetView> Inventory,
    int ActiveAssets,
    string InventorySnapshot,
    IReadOnlyList<string> Limitations);

public interface INistEvidenceOverviewService
{
    /// <summary>Evidências e correlação da avaliação · rodada · escopo (do tenant; objeto de outro tenant → não encontrado).</summary>
    Task<NistEvidenceOverviewView> GetAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default);
}
