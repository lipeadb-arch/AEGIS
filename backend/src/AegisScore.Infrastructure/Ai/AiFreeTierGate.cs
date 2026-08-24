using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Autoridade de configuração que decide, sem tocar banco, se a IA externa pode ser chamada para um tenant.
/// A fronteira continua fail-closed: é necessário modo externo, chave presente e slug explicitamente allowlisted.
/// </summary>
public interface IAiFreeTierGate
{
    AiMode Mode { get; }
    bool ProviderConfigured { get; }
    bool IsExternalAllowedForSlug(string? tenantSlug);
}

/// <summary>Implementação pura de configuração (sem banco, sem rede) — registrada como singleton.</summary>
public sealed class AiFreeTierGate : IAiFreeTierGate
{
    private readonly AiOptions _opt;
    private readonly HashSet<string> _allowed;

    public AiFreeTierGate(IOptions<AiOptions> opt)
    {
        _opt = opt.Value;
        _allowed = new HashSet<string>(
            (_opt.FreeTier.AllowedTenantSlugs ?? new List<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    public AiMode Mode => _opt.Mode;

    private bool IsExternalMode =>
        _opt.Mode is AiMode.ExternalDemo or AiMode.ExternalEnterprise;

    public bool ProviderConfigured =>
        IsExternalMode && !string.IsNullOrWhiteSpace(_opt.ApiKey);

    public bool IsExternalAllowedForSlug(string? tenantSlug) =>
        ProviderConfigured
        && !string.IsNullOrWhiteSpace(tenantSlug)
        && _allowed.Contains(tenantSlug!.Trim());
}
