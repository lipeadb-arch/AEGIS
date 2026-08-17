using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Autoridade de CONFIGURAÇÃO do Free Tier: decide, sem tocar banco, se a IA externa pode ser chamada para
/// um tenant. É a FRONTEIRA DE DADOS do modo gratuito — nenhum consumidor chama o cliente Gemini sem passar
/// por aqui. Um tenant só é liberado quando o modo é <see cref="AiMode.GeminiFreeDemo"/>, há chave e o slug
/// está na allowlist (laboratório sandbox, dados sintéticos). Nada é hardcoded: slugs vêm da configuração.
/// </summary>
public interface IAiFreeTierGate
{
    /// <summary>Modo operacional configurado.</summary>
    AiMode Mode { get; }

    /// <summary>True quando o provedor externo está APTO a ser chamado (modo demonstrativo + chave presente).</summary>
    bool ProviderConfigured { get; }

    /// <summary>True quando o tenant do slug informado pode ter dados enviados ao provedor externo gratuito.</summary>
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

    public bool ProviderConfigured =>
        _opt.Mode == AiMode.GeminiFreeDemo && !string.IsNullOrWhiteSpace(_opt.ApiKey);

    public bool IsExternalAllowedForSlug(string? tenantSlug) =>
        ProviderConfigured
        && !string.IsNullOrWhiteSpace(tenantSlug)
        && _allowed.Contains(tenantSlug!.Trim());
}
