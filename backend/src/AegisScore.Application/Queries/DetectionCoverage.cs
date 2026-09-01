using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Application.Queries;

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Autoridade ÚNICA de leitura, tenant-scoped e SOMENTE LEITURA, da cobertura de
/// detecção atual (regras do SIEM × MITRE ATT&CK). Tenant IMPLÍCITO (fail-closed via ITenantContext + Global Query
/// Filter). Devolve SÓ agregados seguros — nunca configuração da integração, credencial, nome/texto de regra,
/// payload do fornecedor ou dados de outro tenant.
/// </summary>
public interface IDetectionCoverageQuery
{
    Task<DetectionCoverageViewDto> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Estado GERAL da visão de cobertura de detecção — o frontend escolhe a tela por ele. <c>NotConfigured</c> = sem
/// conector de SIEM; <c>NeverSynced</c> = conector existe mas nunca coletou; <c>Available</c>/<c>Partial</c> =
/// inventário (completo/piso); <c>Unavailable</c> = a última tentativa falhou (com ou sem snapshot anterior
/// preservado).
/// </summary>
public enum DetectionCoverageViewState { NotConfigured = 0, NeverSynced = 1, Available = 2, Partial = 3, Unavailable = 4 }

/// <summary>
/// Contrato de leitura da cobertura de detecção. <see cref="AffectsScore"/> é SEMPRE <c>false</c> e
/// <see cref="ScoreDisclaimer"/> carrega o aviso explícito: configuração de regras NÃO altera o AEGIS Score.
/// </summary>
public sealed record DetectionCoverageViewDto(
    DetectionCoverageViewState State,
    string? Source,
    string AttackVersion,
    string AttackLabel,
    string? StoredCollectionState,      // "Available" | "Partial" | null (nunca coletado)
    string LastAttemptState,            // "Available" | "Partial" | "Unavailable" | "NeverCollected"
    DateTimeOffset? LastCollectionAt,
    DateTimeOffset? LastAttemptAt,
    DetectionCoverageSummaryDto Summary,
    IReadOnlyList<DetectionCoverageTechniqueDto> Techniques,
    bool AffectsScore,
    string ScoreDisclaimer);

/// <summary>Totais AGREGADOS em linguagem clara. Regras arquivadas NUNCA entram; quantidade nunca vira pontos.</summary>
public sealed record DetectionCoverageSummaryDto(
    int ActiveRules,
    int RulesWithMitre,
    int RulesWithoutMitre,
    int RulesInLiveMode,
    int RulesWithAlerting,
    int TechniquesObserved,
    int TechniquesNeedingAttention);

/// <summary>
/// Uma técnica MITRE observada, com o nome CLARO e as táticas em pt-BR (resolvidos determinìsticamente pelo catálogo
/// fixado v17.1 — nunca por IA). Estado legível derivado das contagens; nunca chama a técnica de "protegida"/"eficaz".
/// </summary>
public sealed record DetectionCoverageTechniqueDto(
    string TechniqueId,
    string Name,
    bool IsSubtechnique,
    string? ParentTechniqueId,
    IReadOnlyList<DetectionCoverageTacticDto> Tactics,
    int RuleCount,
    int LiveRuleCount,
    int AlertingRuleCount,
    string StatusLabel,
    bool NeedsAttention);

/// <summary>Uma tática MITRE associada, com o código e o nome pt-BR determinístico do catálogo.</summary>
public sealed record DetectionCoverageTacticDto(string Id, string Name);
