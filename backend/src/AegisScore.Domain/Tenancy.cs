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

/// <summary>A digital asset (system/server/service) — the bridge Vulnerability → Asset → Process.</summary>
public class Asset : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string? Type { get; set; }                // server, saas, identity, ...
    public int Criticality { get; set; } = 1;        // 1–4
    public string? OwnerName { get; set; }
    public Guid? BusinessProcessId { get; set; }
    public BusinessProcess? BusinessProcess { get; set; }
    public string? ExternalRef { get; set; }         // CMDB id
}
