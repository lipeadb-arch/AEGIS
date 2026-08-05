using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Knight;

/// <summary>
/// Resolve a CONFIGURAÇÃO de uma fonte KNIGHT para o tenant do contexto: lê o <see cref="ConnectorConfig"/>
/// (Provider=Microsoft, Capability=IdentityPosture) e DECIFRA os segredos pela mesma proteção já existente
/// (<see cref="IConnectorSecretProtector"/>) — sem criar uma segunda infraestrutura de segredos. O segredo
/// decifrado só existe em memória, é passado ao coletor pelo contexto e NUNCA é persistido/logado. Demo é
/// sempre disponível; Google Workspace existe como fonte, porém sem configuração real nesta entrega.
///
/// Isolamento de tenant: o <see cref="AegisScoreDbContext"/> aplica o Global Query Filter (fail-closed), então
/// a busca de conectores já é restrita ao tenant do contexto.
/// </summary>
public sealed class KnightSourceConfigurationProvider : IKnightSourceConfigurationProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AegisScoreDbContext _db;
    private readonly IConnectorSecretProtector _protector;
    private readonly ILogger<KnightSourceConfigurationProvider>? _log;

    public KnightSourceConfigurationProvider(
        AegisScoreDbContext db, IConnectorSecretProtector protector, ILogger<KnightSourceConfigurationProvider>? log = null)
    {
        _db = db;
        _protector = protector;
        _log = log;
    }

    public async Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default)
    {
        switch (source)
        {
            case KnightSourceType.Demo:
                return new KnightDemoConfiguration();

            case KnightSourceType.MicrosoftEntraId:
                var cfg = await FindEntraConnectorAsync(ct);
                if (cfg is null || !cfg.Enabled)
                    return new KnightSourceNotConfigured(source);
                var settings = TryDecrypt(cfg);
                return settings is null
                    ? new KnightSourceNotConfigured(source)   // segredo ilegível/incompleto = não configurado (fail-closed)
                    : new KnightEntraIdConfiguration(settings.TenantIdValue!, settings.ClientId!, settings.ClientSecret!);

            case KnightSourceType.GoogleWorkspace:
                var gcfg = await FindGoogleConnectorAsync(ct);
                if (gcfg is null || !gcfg.Enabled)
                    return new KnightSourceNotConfigured(source);
                var gsettings = TryDecryptGoogle(gcfg);
                return gsettings is null
                    ? new KnightSourceNotConfigured(source)   // segredo ilegível/incompleto = não configurado (fail-closed)
                    : new KnightGoogleWorkspaceConfiguration(gsettings.CustomerId!, gsettings.DelegatedAdminEmail!, gsettings.ServiceAccountJson!);

            default:
                // Fontes futuras: capacidade arquitetural, sem configuração real ainda.
                return new KnightSourceNotConfigured(source);
        }
    }

    public async Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default)
    {
        var entra = await FindEntraConnectorAsync(ct);
        var entraConfigured = entra is not null && TryDecrypt(entra) is not null;
        var google = await FindGoogleConnectorAsync(ct);
        var googleConfigured = google is not null && TryDecryptGoogle(google) is not null;
        return new[]
        {
            new KnightSourceAvailability(KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID", entraConfigured, entra?.Enabled ?? false),
            new KnightSourceAvailability(KnightSourceType.GoogleWorkspace, "Google Workspace", googleConfigured, google?.Enabled ?? false),
        };
    }

    private Task<ConnectorConfig?> FindEntraConnectorAsync(CancellationToken ct) =>
        _db.Connectors.AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.Provider == ConnectorProvider.Microsoft && c.Capability == ConnectorCapability.IdentityPosture, ct);

    private Task<ConnectorConfig?> FindGoogleConnectorAsync(CancellationToken ct) =>
        _db.Connectors.AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.Provider == ConnectorProvider.Google && c.Capability == ConnectorCapability.IdentityPosture, ct);

    private EntraSettings? TryDecrypt(ConnectorConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.EncryptedSettings)) return null;
        try
        {
            var json = _protector.Unprotect(cfg.EncryptedSettings);
            var s = JsonSerializer.Deserialize<EntraSettings>(json, JsonOpts);
            if (s is null
                || string.IsNullOrWhiteSpace(s.TenantIdValue)
                || string.IsNullOrWhiteSpace(s.ClientId)
                || string.IsNullOrWhiteSpace(s.ClientSecret))
                return null;
            return s;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Configuração do conector Entra do KNIGHT ilegível; tratada como não configurada.");
            return null;
        }
    }

    /// <summary>
    /// Forma esperada do JSON de configuração (em claro no <c>settings</c> de criação; cifrado em repouso).
    /// A interface envia <c>tenantId</c>; <c>azureTenantId</c> é aceito por compatibilidade interna. NÃO há
    /// base URL de Graph/login no JSON — o destino é constante oficial no cliente HTTP (não vem do tenant).
    /// </summary>
    private sealed record EntraSettings(
        string? TenantId = null,
        string? AzureTenantId = null,
        string? ClientId = null,
        string? ClientSecret = null)
    {
        /// <summary>Tenant do Entra: prioriza <c>tenantId</c> (o que a interface envia); cai para <c>azureTenantId</c>.</summary>
        public string? TenantIdValue => !string.IsNullOrWhiteSpace(TenantId) ? TenantId : AzureTenantId;
    }

    private GoogleSettings? TryDecryptGoogle(ConnectorConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.EncryptedSettings)) return null;
        try
        {
            var json = _protector.Unprotect(cfg.EncryptedSettings);
            var s = JsonSerializer.Deserialize<GoogleSettings>(json, JsonOpts);
            if (s is null
                || string.IsNullOrWhiteSpace(s.CustomerId)
                || string.IsNullOrWhiteSpace(s.DelegatedAdminEmail)
                || string.IsNullOrWhiteSpace(s.ServiceAccountJson))
                return null;
            return s;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Configuração do conector Google do KNIGHT ilegível; tratada como não configurada.");
            return null;
        }
    }

    /// <summary>
    /// Forma esperada do JSON de configuração do Google (em claro no <c>settings</c> de criação; cifrado em
    /// repouso). O <c>serviceAccountJson</c> contém a chave privada — NÃO há URL de destino no JSON (o host é
    /// constante oficial no cliente).
    /// </summary>
    private sealed record GoogleSettings(
        string? CustomerId = null,
        string? DelegatedAdminEmail = null,
        string? ServiceAccountJson = null);
}
