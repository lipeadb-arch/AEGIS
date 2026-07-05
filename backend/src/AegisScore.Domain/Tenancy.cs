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
    public int Criticality { get; set; } = 1;        // 1–4 (declarado pelo negócio)
    public string? OwnerName { get; set; }
    public string? ExternalRef { get; set; }         // id no CMDB / chave de upsert do conector

    // ---- Ponte com processo de negócio (impacto/risco) ----
    public Guid? BusinessProcessId { get; set; }
    public BusinessProcess? BusinessProcess { get; set; }

    // ---- Inventário contínuo ----
    public AssetDiscoverySource DiscoverySource { get; set; } = AssetDiscoverySource.Manual;
    public DateTimeOffset? LastSeenAt { get; set; }  // heartbeat da descoberta contínua
    public bool IsActive { get; set; } = true;       // desativado ≠ deletado (histórico preservado)

    // ---- Risco calculado pela IA (nulo até o motor rodar) ----
    public double? RiskScore { get; set; }           // 0–100 (mesma escala do IcrScore)
    public RiskLevel? RiskLevel { get; set; }        // banda derivada — reusa o enum existente
    public DateTimeOffset? RiskScoredAt { get; set; }
    public string? RiskRationaleJson { get; set; }   // explicabilidade (padrão IcrScore.FactorsJson)
}
