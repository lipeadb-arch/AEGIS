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
