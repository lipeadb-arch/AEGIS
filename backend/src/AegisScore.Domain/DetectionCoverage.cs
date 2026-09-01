using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-MVP-GOOGLE-SECOPS-02] Cobertura de detecção (regras do SIEM × MITRE ATT&CK)
// ============================================================================
// Fotografia CONSULTIVA e PROVIDER-NEUTRAL de como as regras de um SIEM estão mapeadas
// ao MITRE ATT&CK. É um FATO OPERACIONAL: NÃO vira EvidenceSignal, NÃO alimenta o AEGIS
// Score, NÃO altera conformidade NIST nem os estados determinísticos dos controles. A
// existência de uma regra não comprova eficácia, fonte de logs, detecção real ou
// conformidade — por isso "quantidade de regras" JAMAIS gera pontos.
//
// Persistência LIMITADA: UM snapshot atual por (tenant, conector), com filhos AGREGADOS
// por técnica. NUNCA se persiste o texto, o nome, o autor ou o conteúdo de uma regra —
// apenas contagens por técnica MITRE (validada contra o catálogo fixado v17.1).

/// <summary>
/// Estado EXPLÍCITO da coleta/completude de uma fotografia de cobertura de detecção. Distingue o que uma
/// contagem sozinha confundiria: uma coleta COMPLETA com zero regras (<see cref="Available"/>) NÃO é o
/// mesmo que uma coleta truncada, uma falha total ou "nunca coletado". Só <see cref="Available"/> permite
/// ler os totais como verdade completa.
/// </summary>
public enum DetectionCoverageCollectionState
{
    /// <summary>Nunca coletado — não há fotografia (linha placeholder ou ausência de snapshot).</summary>
    NeverCollected = 0,
    /// <summary>Coleta COMPLETA — os totais são a verdade (zero ou mais regras).</summary>
    Available = 1,
    /// <summary>Coletado, mas truncado (teto defensivo/limite da fonte): os agregados são um PISO, não o total.</summary>
    Partial = 2,
    /// <summary>Coleta indisponível/falhou (permissão, throttle, timeout, transporte) — não comprovada, nunca "zero".</summary>
    Unavailable = 3,
}

/// <summary>
/// Snapshot ATUAL de cobertura de detecção de UM conector de SIEM, isolado por tenant. Chave natural
/// (TenantId, ConnectorConfigId) — índice único que torna o upsert idempotente uma invariante de banco. Guarda
/// os TOTAIS agregados do último inventário e o desfecho da última tentativa, para sobreviver a reload/restart e
/// preservar honestamente o último inventário completo mesmo quando uma coleta posterior falha. NUNCA guarda
/// nome/texto/conteúdo de regra.
/// </summary>
public class DetectionCoverageSnapshot : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Conector de SIEM que produziu a fotografia (a fonte concreta desta cobertura).</summary>
    public Guid ConnectorConfigId { get; set; }

    /// <summary>Rótulo estável da fonte (ex.: "Google SecOps"). Nunca endpoint/credencial.</summary>
    public string Source { get; set; } = "";

    /// <summary>Versão do MITRE ATT&CK contra a qual as técnicas foram validadas (ex.: "17.1").</summary>
    public string AttackVersion { get; set; } = "";

    /// <summary>Estado dos DADOS ARMAZENADOS (técnicas/totais): Available (completo) ou Partial (piso). Nunca Unavailable.</summary>
    public DetectionCoverageCollectionState CollectionState { get; set; } = DetectionCoverageCollectionState.NeverCollected;

    /// <summary>Desfecho da tentativa MAIS RECENTE (pode ser Unavailable/Partial mesmo com dados completos preservados).</summary>
    public DetectionCoverageCollectionState LastAttemptState { get; set; } = DetectionCoverageCollectionState.NeverCollected;

    /// <summary>Instante da última TENTATIVA de coleta (sucesso, parcial ou falha).</summary>
    public DateTimeOffset LastAttemptAt { get; set; }

    /// <summary>Instante da última coleta que PRODUZIU os dados armazenados (Available/Partial). Null enquanto nunca houve dados.</summary>
    public DateTimeOffset? LastCollectionAt { get; set; }

    // ---- Totais AGREGADOS do inventário armazenado (regras ARQUIVADAS não entram) ----
    public int TotalActiveRules { get; set; }
    public int RulesWithMitre { get; set; }
    public int RulesWithoutMitre { get; set; }
    public int RulesInLiveMode { get; set; }
    public int RulesWithAlerting { get; set; }
    public int TechniquesObserved { get; set; }

    /// <summary>
    /// Fingerprint determinístico dos DADOS armazenados (totais + técnicas). Impede writes desnecessários: uma
    /// coleta idêntica não reescreve os filhos.
    /// </summary>
    public string Fingerprint { get; set; } = "";

    public List<DetectionCoverageTechnique> Techniques { get; set; } = new();
}

/// <summary>
/// Cobertura AGREGADA de UMA técnica MITRE (validada contra o catálogo fixado) num snapshot. Guarda apenas o ID
/// da técnica e as CONTAGENS — nome/táticas/hierarquia são resolvidos na leitura pelo catálogo (nunca persistidos,
/// nunca desatualizam). Persistir por técnica (e não por regra) mantém as linhas minúsculas e limitadas.
/// </summary>
public class DetectionCoverageTechnique : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid DetectionCoverageSnapshotId { get; set; }

    /// <summary>ID MITRE canônico da técnica/subtécnica (ex.: "T1059" ou "T1059.003"). Único no snapshot.</summary>
    public string TechniqueId { get; set; } = "";

    /// <summary>Regras (não arquivadas) configuradas com esta técnica.</summary>
    public int RuleCount { get; set; }

    /// <summary>Dessas, quantas estão em live mode (execução ativa).</summary>
    public int LiveRuleCount { get; set; }

    /// <summary>Dessas, quantas têm alerting habilitado.</summary>
    public int AlertingRuleCount { get; set; }

    public DetectionCoverageSnapshot Snapshot { get; set; } = null!;
}
