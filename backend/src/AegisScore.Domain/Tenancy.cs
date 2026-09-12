using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

/// <summary>A client of the MSSP/SOC. Root of all operational data isolation.</summary>
public class Tenant : Entity
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public TenantStatus Status { get; set; } = TenantStatus.Onboarding;

    public ICollection<BusinessUnit> BusinessUnits { get; set; } = new List<BusinessUnit>();
    public ICollection<BusinessProcess> Processes { get; set; } = new List<BusinessProcess>();
    public ICollection<ConnectorConfig> Connectors { get; set; } = new List<ConnectorConfig>();
}

/// <summary>Business unit / department / area being assessed (the "BU" in the SAQ).</summary>
public class BusinessUnit : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";          // e.g. "Compliance"
    public string? Code { get; set; }
    public string? ManagerName { get; set; }         // "Joao Neto Silva"
    public string? ManagerEmail { get; set; }
}

/// <summary>An information-security process / domain (Plano Diretor "Atividade Principal").</summary>
public class BusinessProcess : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";           // "Gestão de Identidade e Acesso"
    public string? ProcessCategory { get; set; }     // "Operações de Segurança"
    public ProcessClassification Classification { get; set; } = ProcessClassification.Interno;

    /// <summary>Business value of the process, 1–4 (feeds risk level and ICR business impact).</summary>
    public int ProcessValue { get; set; } = 1;

    public ICollection<Asset> Assets { get; set; } = new List<Asset>();
}

/// <summary>
/// Ativo do inventário contínuo (pilar Identify / ID.AM do NIST CSF 2.0).
/// Ponte Vulnerabilidade → Ativo → Processo, agora categorizado pelas verticais NIST
/// e enriquecido com o score/nível de risco calculado pelo motor de IA.
/// </summary>
public class Asset : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    // ---- Contexto do ativo ----
    public string Name { get; set; } = "";
    public AssetCategory Category { get; set; } = AssetCategory.Hardware;  // vertical NIST (mandatório)
    public string? SubType { get; set; }             // granularidade livre: "server", "saas", "identity"
    public string? Description { get; set; }
    public int Criticality { get; set; } = 1;        // 1–4 (valor cadastrado; ver a proveniência abaixo)

    // ---- [AEGIS-RISK-PRIORITIZATION-01] Proveniência da criticidade DECLARADA ----
    // O resolvedor cria ativos com Criticality = 1 (valor padrão do modelo) e os ativos legados não registram quem
    // definiu o valor: nenhum dos dois é "criticidade baixa confirmada". Só uma declaração com autor e data transforma
    // o valor em informação declarada. Nulo = sem proveniência (nenhum backfill inventa uma).

    /// <summary>Valor declarado (1–4). Só vale como declarado enquanto for igual a <see cref="Criticality"/>.</summary>
    public int? CriticalityDeclaredValue { get; set; }

    /// <summary>Instante da declaração (relógio do servidor).</summary>
    public DateTimeOffset? CriticalityDeclaredAt { get; set; }

    /// <summary>Conta que declarou, quando resolvida do token.</summary>
    public Guid? CriticalityDeclaredByAccountId { get; set; }

    /// <summary>Nome de exibição de quem declarou, como o token o apresentou (vazio quando ausente).</summary>
    public string? CriticalityDeclaredByName { get; set; }

    /// <summary>Justificativa curta e sanitizada informada na declaração (opcional).</summary>
    public string? CriticalityDeclarationNote { get; set; }

    public string? OwnerName { get; set; }
    public string? ExternalRef { get; set; }         // id no CMDB / chave de upsert do conector

    // ---- Ponte com processo de negócio (impacto/risco) ----
    public Guid? BusinessProcessId { get; set; }
    public BusinessProcess? BusinessProcess { get; set; }

    // ---- Inventário contínuo ----
    public AssetDiscoverySource DiscoverySource { get; set; } = AssetDiscoverySource.Manual;
    public DateTimeOffset? LastSeenAt { get; set; }  // heartbeat da descoberta contínua
    public bool IsActive { get; set; } = true;       // desativado ≠ deletado (histórico preservado)

    /// <summary>
    /// [AEGIS-ENTITY-RESOLUTION-01] Origem do <see cref="Name"/>. Só um nome <see cref="AssetNameOrigin.Placeholder"/>
    /// (fonte que não coleta nome) pode ser substituído pelo primeiro nome observado por outra fonte; qualquer outro
    /// (curado, manual, legado) nunca é sobrescrito pela resolução.
    /// </summary>
    public AssetNameOrigin NameOrigin { get; set; } = AssetNameOrigin.Unspecified;

    // ---- Risco calculado pela IA (nulo até o motor rodar) ----
    public double? RiskScore { get; set; }           // 0–100 (mesma escala do IcrScore)
    public RiskLevel? RiskLevel { get; set; }        // banda derivada — reusa o enum existente
    public DateTimeOffset? RiskScoredAt { get; set; }
    public string? RiskRationaleJson { get; set; }   // explicabilidade (padrão IcrScore.FactorsJson)

    // ---- Matriz de impacto de negócio (ID.RA) ----
    /// <summary>
    /// Perfil CIA + dimensões de negócio que REFINA a <see cref="Criticality"/> escalar. Owned Value Object
    /// (1:1, mapeado na própria tabela do Asset — sem Id nem ciclo de vida próprio). Nulo = ainda não avaliado.
    /// </summary>
    public BusinessImpactProfile? BusinessImpact { get; set; }
}

/// <summary>
/// Matriz de impacto de negócio de um ativo (ID.RA-04) — Owned Value Object do <see cref="Asset"/>.
/// Cada eixo é 1–4 (mesma régua de <see cref="Asset.Criticality"/> / <see cref="BusinessProcess.ProcessValue"/>).
/// O motor de raio de explosão usa o MÁXIMO das dimensões (high-water mark, à la FIPS 199) como o impacto
/// intrínseco do ativo. Por ser OWNED não herda de <see cref="Entity"/>: não tem Id próprio.
/// </summary>
public class BusinessImpactProfile
{
    // Tríade CIA
    public int Confidentiality { get; set; } = 1;
    public int Integrity { get; set; } = 1;
    public int Availability { get; set; } = 1;

    // Dimensões de negócio
    public int Financial { get; set; } = 1;
    public int Operational { get; set; } = 1;
    public int Regulatory { get; set; } = 1;
    public int Reputational { get; set; } = 1;

    /// <summary>RTO/RPO em minutos (dimensão de disponibilidade — elo com RC.RP). Nulo = não definido.</summary>
    public int? RecoveryTimeObjectiveMinutes { get; set; }
    public int? RecoveryPointObjectiveMinutes { get; set; }
}
