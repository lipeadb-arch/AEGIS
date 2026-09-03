using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Identity;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Identity;

/// <summary>
/// [AEGIS-MVP-EVIDENCE-FABRIC-01] Implementação da Evidence Fabric de identidade: o ÚNICO ponto que faz a
/// aquisição real do Microsoft Entra ID (reusando o coletor do KNIGHT e o transporte/credencial existentes) e
/// persiste o snapshot NORMALIZADO, tenant-safe, com proveniência e completude. Tanto o assessment do AEGIS
/// KNIGHT quanto a rota de postura NIST convergem para cá — UMA aquisição por operação lógica, sem um segundo
/// cliente Graph, credencial ou consulta duplicada.
///
/// Degradação segura: uma coleta que FALHE não apaga nem falsifica o último snapshot válido — só atualiza o
/// desfecho da última tentativa e a saúde do conector. Isolamento por tenant garantido pelo Global Query
/// Filter (leitura) + stamping no SaveChanges (escrita) + FK composta (Id, TenantId) no banco. NUNCA persiste
/// nome, e-mail, ID de usuário, aplicação, token, segredo ou payload — só agregados tipados.
/// </summary>
public sealed class IdentityEvidenceService : IIdentityEvidenceService
{
    /// <summary>Versão do schema do snapshot de evidência de identidade — rastreabilidade do contrato persistido.</summary>
    public const string SchemaVersion = "aegis-identity-evidence-v1";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly AegisScoreDbContext _db;
    private readonly IKnightCollectorRegistry _registry;
    private readonly IKnightSourceConfigurationProvider _config;
    private readonly ITenantContext _tenant;
    private readonly ILogger<IdentityEvidenceService>? _log;

    public IdentityEvidenceService(
        AegisScoreDbContext db,
        IKnightCollectorRegistry registry,
        IKnightSourceConfigurationProvider config,
        ITenantContext tenant,
        ILogger<IdentityEvidenceService>? log = null)
    {
        _db = db;
        _registry = registry;
        _config = config;
        _tenant = tenant;
        _log = log;
    }

    public async Task<IdentityEvidenceAcquisition> CollectAsync(CancellationToken ct = default)
    {
        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException("Aquisição de evidência de identidade sem tenant resolvido no contexto (fail-closed).");

        // O conector é a autoridade da fonte + o alvo da FK tenant-safe + onde a saúde/última sync é registrada.
        var connector = await _db.Connectors
            .FirstOrDefaultAsync(c => c.Provider == ConnectorProvider.Microsoft && c.Capability == ConnectorCapability.IdentityPosture, ct);

        if (connector is null)
            return new IdentityEvidenceAcquisition(IdentityEvidenceConnectorState.NotConfigured, null, null);

        // Recusa conector desabilitado/desconectado — não coleta e NÃO destrói a última evidência preservada.
        if (!connector.Enabled)
            return new IdentityEvidenceAcquisition(IdentityEvidenceConnectorState.Disabled, null, await LoadViewAsync(connector.Id, ct));

        // Valida a presença de material de autenticação (segredo decifrável e completo) sem devolvê-lo nunca.
        var configuration = await _config.ResolveAsync(tenantId, KnightSourceType.MicrosoftEntraId, ct);
        if (configuration is not KnightEntraIdConfiguration)
            return new IdentityEvidenceAcquisition(IdentityEvidenceConnectorState.MissingCredential, null, await LoadViewAsync(connector.Id, ct));

        // UMA aquisição real (o coletor do KNIGHT normaliza em fatos tipados; NUNCA cai para dados sintéticos).
        var collector = _registry.Resolve(KnightSourceType.MicrosoftEntraId);
        var result = await collector.CollectAsync(new KnightCollectionContext(tenantId, configuration), ct);

        var view = await PersistAsync(connector, result, ct);
        return new IdentityEvidenceAcquisition(IdentityEvidenceConnectorState.Configured, result, view);
    }

    public async Task<IdentityEvidenceProjection> GetLatestProjectionAsync(CancellationToken ct = default)
    {
        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException("Leitura de evidência de identidade sem tenant resolvido no contexto (fail-closed).");

        var connector = await _db.Connectors.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Provider == ConnectorProvider.Microsoft && c.Capability == ConnectorCapability.IdentityPosture, ct);

        if (connector is null)
            return IdentityEvidenceProjection.Build(IdentityEvidenceConnectorState.NotConfigured, null);

        var view = await LoadViewAsync(connector.Id, ct);

        IdentityEvidenceConnectorState state;
        if (!connector.Enabled)
            state = IdentityEvidenceConnectorState.Disabled;
        else
        {
            var configuration = await _config.ResolveAsync(tenantId, KnightSourceType.MicrosoftEntraId, ct);
            state = configuration is KnightEntraIdConfiguration
                ? IdentityEvidenceConnectorState.Configured
                : IdentityEvidenceConnectorState.MissingCredential;
        }

        return IdentityEvidenceProjection.Build(state, view);
    }

    // ---- Persistência degradation-safe ----------------------------------------------------------

    private async Task<IdentityEvidenceSnapshotView> PersistAsync(
        ConnectorConfig connector, KnightCollectionResult result, CancellationToken ct)
    {
        var producedData = result.State is KnightSourceState.Completed or KnightSourceState.PartialCollection;
        var now = result.CollectedAt == default ? DateTimeOffset.UtcNow : result.CollectedAt;

        var factsJson = JsonSerializer.Serialize(result.Facts.All.OrderBy(o => (int)o.Key).ToList(), Json);
        var capsJson = JsonSerializer.Serialize(result.Capabilities, Json);
        var fingerprint = Fingerprint(factsJson, capsJson, result.State);

        var snapshot = await _db.IdentityEvidenceSnapshots
            .FirstOrDefaultAsync(s => s.ConnectorConfigId == connector.Id, ct);

        var hadPriorData = snapshot?.LastCollectionAt is not null;

        if (snapshot is null)
        {
            snapshot = new IdentityEvidenceSnapshot
            {
                ConnectorConfigId = connector.Id,
                Source = result.SourceLabel,
                SourceType = result.Source,
                SchemaVersion = SchemaVersion,
                LastAttemptState = result.State,
                LastAttemptAt = now,
                LastAttemptDetail = result.Detail,
                DataState = KnightSourceState.NotConfigured,   // placeholder até haver dado
            };
            if (producedData)
            {
                snapshot.DataState = result.State;
                snapshot.LastCollectionAt = now;
                snapshot.FactsJson = factsJson;
                snapshot.CapabilitiesJson = capsJson;
                snapshot.Fingerprint = fingerprint;
            }
            _db.IdentityEvidenceSnapshots.Add(snapshot);
        }
        else
        {
            // A última tentativa SEMPRE é registrada (é onde a degradação aparece).
            snapshot.LastAttemptState = result.State;
            snapshot.LastAttemptAt = now;
            snapshot.LastAttemptDetail = result.Detail;
            snapshot.Source = result.SourceLabel;
            snapshot.SchemaVersion = SchemaVersion;

            if (producedData)
            {
                snapshot.DataState = result.State;
                snapshot.LastCollectionAt = now;
                if (!string.Equals(snapshot.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    snapshot.FactsJson = factsJson;
                    snapshot.CapabilitiesJson = capsJson;
                    snapshot.Fingerprint = fingerprint;
                }
                snapshot.UpdatedAt = now;
            }
            // FALHA: preserva DataState/Facts/Capabilities/LastCollectionAt (última evidência válida) — só a
            // degradação foi registrada acima.
        }

        // Saúde do conector atualizada UMA vez por operação. Falha total com evidência anterior = Degraded
        // (ainda servimos a última evidência válida); falha total sem evidência = Failed.
        connector.LastSyncAt = now;
        connector.LastStatus = result.State switch
        {
            KnightSourceState.Completed => ConnectorStatus.Healthy,
            KnightSourceState.PartialCollection => ConnectorStatus.Degraded,
            _ => hadPriorData ? ConnectorStatus.Degraded : ConnectorStatus.Failed,
        };

        await _db.SaveChangesAsync(ct);
        return ToView(connector.TenantId, snapshot);
    }

    private async Task<IdentityEvidenceSnapshotView?> LoadViewAsync(Guid connectorId, CancellationToken ct)
    {
        var snapshot = await _db.IdentityEvidenceSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ConnectorConfigId == connectorId, ct);
        return snapshot is null ? null : ToView(snapshot.TenantId, snapshot);
    }

    private IdentityEvidenceSnapshotView ToView(Guid tenantId, IdentityEvidenceSnapshot s) => new(
        tenantId,
        s.ConnectorConfigId,
        s.SourceType,
        string.IsNullOrWhiteSpace(s.Source) ? "Microsoft Entra ID" : s.Source,
        s.SchemaVersion,
        s.DataState,
        s.LastAttemptState,
        s.LastCollectionAt,
        s.LastAttemptAt,
        s.LastAttemptDetail,
        DeserializeFacts(s.FactsJson),
        DeserializeCapabilities(s.CapabilitiesJson));

    private IReadOnlyList<KnightObservation> DeserializeFacts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<KnightObservation>();
        try
        {
            return JsonSerializer.Deserialize<List<KnightObservation>>(json, Json) ?? new List<KnightObservation>();
        }
        catch (JsonException ex)
        {
            _log?.LogWarning(ex, "FactsJson do snapshot de identidade ilegível; retornando sem fatos.");
            return Array.Empty<KnightObservation>();
        }
    }

    private IReadOnlyList<KnightCapabilityStatus> DeserializeCapabilities(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<KnightCapabilityStatus>();
        try
        {
            return JsonSerializer.Deserialize<List<KnightCapabilityStatus>>(json, Json) ?? new List<KnightCapabilityStatus>();
        }
        catch (JsonException ex)
        {
            _log?.LogWarning(ex, "CapabilitiesJson do snapshot de identidade ilegível; retornando sem capacidades.");
            return Array.Empty<KnightCapabilityStatus>();
        }
    }

    private static string Fingerprint(string factsJson, string capsJson, KnightSourceState state)
    {
        var bytes = Encoding.UTF8.GetBytes($"{state}|{factsJson}|{capsJson}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
