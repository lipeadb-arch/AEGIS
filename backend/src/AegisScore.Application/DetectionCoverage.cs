using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Abstractions;

// ---- [AEGIS-MVP-GOOGLE-SECOPS-02] Cobertura de detecção — contrato PROVIDER-NEUTRAL (somente leitura) ----

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Fotografia NORMALIZADA, SEGURA e PROVIDER-NEUTRAL da COBERTURA DE DETECÇÃO de um
/// SIEM: como as regras configuradas se mapeiam ao MITRE ATT&CK. É um FATO OPERACIONAL consultivo — NÃO vira
/// EvidenceSignal, NÃO alimenta o AEGIS Score, NÃO altera conformidade NIST nem os estados determinísticos dos
/// controles (a autoridade continua determinística). Nomes provider-neutral (nada de "Google"/"Chronicle" aqui):
/// o rótulo da fonte vive em <see cref="Source"/>. Só AGREGADOS — NUNCA texto, nome, autor ou conteúdo de regra.
///
/// A existência de uma regra NÃO comprova controle implementado, regra funcional, fonte de logs disponível,
/// ataque detectado nem conformidade: quantidade de regras/técnicas JAMAIS gera pontos.
/// </summary>
public sealed record DetectionCoverageSnapshot(
    string Source,
    string AttackVersion,
    DetectionCoverageCollectionState State,
    DateTimeOffset AttemptedAt,
    int TotalActiveRules,
    int RulesWithMitre,
    int RulesWithoutMitre,
    int RulesInLiveMode,
    // ---- Condição de EXECUÇÃO das regras em live mode (dimensão INDEPENDENTE de live/alerting) ----
    // Particiona EXATAMENTE as regras em live mode: Normal+Limited+Paused+Unknown == RulesInLiveMode. live mode
    // habilitado NÃO implica execução saudável — só DEFAULT (Normal) executa como esperado; LIMITED não garante,
    // PAUSED não executa, e o estado não comprovado (EXECUTION_STATE_UNSPECIFIED/ausente) é Unknown.
    int RulesInNormalExecution,
    int RulesInLimitedExecution,
    int RulesInPausedExecution,
    int RulesInUnknownExecution,
    int RulesWithAlerting,
    IReadOnlyList<DetectionTechniqueCoverage> Techniques)
{
    /// <summary>Só uma coleta COMPLETA (<see cref="DetectionCoverageCollectionState.Available"/>) traz totais confiáveis.</summary>
    public bool IsComplete => State == DetectionCoverageCollectionState.Available;

    /// <summary>Houve algum inventário utilizável (completo ou piso parcial) — distingue de falha total/nunca coletado.</summary>
    public bool HasInventory =>
        State is DetectionCoverageCollectionState.Available or DetectionCoverageCollectionState.Partial;
}

/// <summary>
/// Cobertura AGREGADA de UMA técnica MITRE observada nas regras. <see cref="TechniqueId"/> é o ID canônico
/// (ex.: "T1059.003"); <see cref="Name"/>/<see cref="TacticIds"/>/<see cref="IsSubtechnique"/> vêm do catálogo
/// FIXADO (v17.1) — nunca de aproximação textual, IA ou do nome da regra. Só contagens: configurada, em live mode
/// e com alerting habilitado.
/// </summary>
public sealed record DetectionTechniqueCoverage(
    string TechniqueId,
    string Name,
    bool IsSubtechnique,
    string? ParentTechniqueId,
    IReadOnlyList<string> TacticIds,
    int RuleCount,
    int LiveRuleCount,
    // Condição de execução das regras em live mode desta técnica (particiona LiveRuleCount).
    int NormalExecutionRuleCount,
    int LimitedExecutionRuleCount,
    int PausedExecutionRuleCount,
    int UnknownExecutionRuleCount,
    int AlertingRuleCount);

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Capacidade COMPLEMENTAR a <see cref="IEvidenceConnector"/> e a
/// <see cref="ISiemPostureCollector"/>: um conector de SIEM que produz a COBERTURA DE DETECÇÃO (regras × MITRE)
/// via consulta fixa de configuração de regras. O executor de pull a detecta no MESMO fluxo, como dimensão
/// INDEPENDENTE de casos/alertas — SEM criar EvidenceSignal, SEM mapear NIST e SEM tocar o score.
///
/// Diferente do <see cref="ISiemPostureCollector"/>, esta coleta NÃO lança em falha da fonte: devolve SEMPRE uma
/// fotografia com o <see cref="DetectionCoverageCollectionState"/> classificado (a dimensão degrada, mas nunca
/// derruba a sincronização de casos/alertas). Só o cancelamento SOLICITADO propaga.
/// </summary>
public interface IDetectionCoverageCollector
{
    ConnectorProvider Provider { get; }
    ConnectorCapability Capability { get; }

    /// <summary>Coleta a cobertura de detecção (só leitura). Falha da fonte é SANITIZADA e vira estado, não exceção.</summary>
    Task<DetectionCoverageSnapshot> CollectCoverageAsync(ConnectorConfig config, CancellationToken ct);
}
