using System.Runtime.CompilerServices;
using Stars.Application.Abstractions;
using Stars.Domain;

namespace Stars.Connectors.Microsoft;

/// <summary>
/// Reference adapter: Microsoft Secure Score → normalized <see cref="EvidenceSignal"/>s.
/// In production this calls Microsoft Graph (security/secureScores) using the OAuth client
/// credentials stored (encrypted) in <see cref="ConnectorConfig.EncryptedSettings"/>. Here it
/// emits representative values matching the onboarding screenshots, mapped to NIST CSF 2.0
/// subcategories — demonstrating the Adapter/Facade contract end to end.
///
/// To add another tool (Defender Exposure, Purview, Azure Advisor, AWS Security Hub, a SIEM…),
/// implement another <see cref="IEvidenceConnector"/> the same way and register it. Nothing in
/// the core changes — it only ever sees EvidenceSignal.
/// </summary>
public class MicrosoftSecureScoreConnector : IEvidenceConnector
{
    public ConnectorProvider Provider => ConnectorProvider.Microsoft;
    public ConnectorCapability Capability => ConnectorCapability.SecureScore;

    public Task<ConnectorHealth> TestAsync(ConnectorConfig config, CancellationToken ct)
    {
        // Real impl: acquire a token and GET /v1.0/security/secureScores?$top=1.
        var ok = !string.IsNullOrWhiteSpace(config.EncryptedSettings);
        return Task.FromResult(new ConnectorHealth(
            ok ? ConnectorStatus.Healthy : ConnectorStatus.Degraded,
            ok ? "Credentials present." : "Connector not configured."));
    }

    public async IAsyncEnumerable<EvidenceSignal> CollectAsync(
        ConnectorConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield(); // real impl awaits Graph here

        foreach (var (key, value, subcats) in SampleScores())
        {
            ct.ThrowIfCancellationRequested();
            yield return new EvidenceSignal
            {
                TenantId = config.TenantId,
                ConnectorConfigId = config.Id,
                SignalKey = key,
                NumericValue = value,
                Unit = "percent",
                Severity = SeverityFromPercent(value),
                MappedSubcategoryCodes = subcats.ToList(),
                CollectedAt = DateTimeOffset.UtcNow
            };
        }
    }

    private static IEnumerable<(string Key, double Value, string[] Subcats)> SampleScores() => new[]
    {
        ("secureScore.overall",  53.77, new[] { "PR.AA-01", "PR.DS-01", "PR.PS-01" }),
        ("secureScore.identity", 67.84, new[] { "PR.AA-01", "PR.AA-03", "PR.AA-05" }),
        ("secureScore.data",     77.78, new[] { "PR.DS-01", "PR.DS-02", "PR.DS-10" }),
        ("secureScore.device",   52.24, new[] { "PR.PS-01", "PR.PS-05", "DE.CM-01" }),
        ("secureScore.apps",     54.04, new[] { "PR.PS-06", "DE.CM-09" }),
    };

    private static int SeverityFromPercent(double pct) => pct switch
    {
        >= 80 => 0,
        >= 60 => 1,
        >= 40 => 2,
        >= 20 => 3,
        _     => 4
    };
}
