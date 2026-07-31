using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

/// <summary>
/// A configured integration to a client tool (Microsoft, Google, AWS, SIEM, EDR, ...).
/// Set up during onboarding. Credentials are stored encrypted.
/// </summary>
public class ConnectorConfig : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public ConnectorProvider Provider { get; set; }
    public ConnectorCapability Capability { get; set; }
    public string DisplayName { get; set; } = "";
    public ConnectorAuthType AuthType { get; set; }

    /// <summary>Encrypted JSON blob with credentials/endpoints. Never stored in clear text.</summary>
    public string EncryptedSettings { get; set; } = "";

    public bool Enabled { get; set; } = true;
    public int SyncIntervalMinutes { get; set; } = 360;
    public DateTimeOffset? LastSyncAt { get; set; }
    public ConnectorStatus LastStatus { get; set; } = ConnectorStatus.Unknown;

    /// <summary>
    /// [AEGIS-AUD-020] Hash SHA-256 (hex, 64 chars) da CHAVE DE INGESTÃO dos conectores genéricos de push
    /// (Generic/Siem, Generic/Edr). Só o hash é persistido — a chave em claro NUNCA fica no banco nem volta
    /// na API. É a credencial própria do endpoint externo de ingestão, distinta do JWT de usuário. Nullable:
    /// conectores pull (OAuth/API-key nas EncryptedSettings) não têm chave de ingestão.
    /// </summary>
    public string? IngestionKeyHash { get; set; }
}

/// <summary>A normalized fact collected from a connector and mapped to NIST subcategories.</summary>
public class EvidenceSignal : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid ConnectorConfigId { get; set; }
    public string SignalKey { get; set; } = "";      // "secureScore.identity"
    public double? NumericValue { get; set; }        // 0.67
    public string? JsonValue { get; set; }           // richer payload (jsonb)
    public string? Unit { get; set; }                // "percent", "count"
    public int? Severity { get; set; }               // 0..4
    public List<string> MappedSubcategoryCodes { get; set; } = new(); // ["PR.AA-03"]
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;

    // ---- [AEGIS-AUD-020/041/043] Ingestão genérica de evidências (push SIEM/EDR) ----
    // Todos NULLABLE de propósito: aditivos e compatíveis com EvidenceSignal legado (coleta pull), que
    // nunca os preencheu. A coleta pull continua gravando só os campos acima; o push preenche estes.

    /// <summary>Versão do contrato de lote que trouxe este evento (ex.: "1").</summary>
    public string? SchemaVersion { get; set; }

    /// <summary>Origem declarada pelo emissor (ex.: "sentinel", "crowdstrike-falcon"). Dado NÃO confiável.</summary>
    public string? Source { get; set; }

    /// <summary>Tipo do evento no vocabulário do emissor (ex.: "alert", "detection"). Dado NÃO confiável.</summary>
    public string? EventType { get; set; }

    /// <summary>Id do evento no sistema de origem, quando fornecido — base da chave idempotente.</summary>
    public string? ExternalEventId { get; set; }

    /// <summary>
    /// Chave idempotente determinística (SHA-256 hex): derivada do <see cref="ExternalEventId"/> quando há,
    /// senão do conteúdo normalizado do evento. Única por (Tenant, ConnectorConfig, DeduplicationKey) —
    /// invariante de banco. NULL nos sinais de coleta pull (snapshots periódicos não são deduplicados).
    /// </summary>
    public string? DeduplicationKey { get; set; }

    /// <summary>Instante em que o AEGIS RECEBEU o evento (distinto de <see cref="CollectedAt"/>, do emissor).</summary>
    public DateTimeOffset? ReceivedAt { get; set; }

    /// <summary>
    /// Payload bruto do evento PROTEGIDO (Data Protection, purpose <c>AegisScore.EvidenceSignal.RawPayload.v1</c>)
    /// — nunca legível no banco e nunca devolvido pela API/tela. Distinto do <see cref="JsonValue"/> (claro).
    /// </summary>
    public string? ProtectedRawPayload { get; set; }
}

/// <summary>
/// Rule that maps a connector signal to one or more subcategories, with a weight and
/// scoring hint. Lets us turn "Secure Score Identity = 31%" into a maturity contribution
/// for PR.AA-* without code changes.
/// </summary>
public class SignalMapping : Entity
{
    public Guid FrameworkVersionId { get; set; }
    public ConnectorCapability Capability { get; set; }
    public string SignalKey { get; set; } = "";
    public List<string> SubcategoryCodes { get; set; } = new();
    public double Weight { get; set; } = 1.0;
    public string? ScoringHint { get; set; }         // e.g. "percent->level" mapping name
}
