using System.Security.Claims;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Auth;

/// <summary>
/// [AEGIS-AUD-007] Autoridade ÚNICA de leitura/validação das claims do token Entra na troca federada. A
/// policy do endpoint e o controller chamam ESTE mesmo validador, para não divergirem de regra. Assume um
/// principal cujo token JÁ foi validado criptograficamente pelo esquema <c>EntraId</c> (assinatura/issuer/
/// audience/lifetime/algoritmo); aqui checamos a AUTORIZAÇÃO específica da troca:
///  - <c>scp</c> contém EXATAMENTE o scope delegado configurado (comparação por item na lista de espaços);
///  - ausência de <c>scp</c> recusa tokens app-only (que trazem <c>roles</c>) — <c>roles</c> NÃO substitui scope;
///  - <c>azp</c> (v2) ou <c>appid</c> (v1) é o <c>SpaClientId</c> configurado — só o SPA pode trocar;
///  - <c>tid</c> e <c>oid</c> são GUIDs válidos e <c>tid</c> é o tenant configurado.
/// Em sucesso devolve a identidade já CANONICALIZADA (tid/oid no formato "D").
/// </summary>
public static class FederatedPrincipalValidator
{
    public static bool TryValidate(ClaimsPrincipal principal, FederationOptions options, out FederatedIdentity identity)
    {
        identity = new FederatedIdentity(null, null, null);

        if (principal.Identity?.IsAuthenticated != true || !options.FederationEnabled)
            return false;

        // Scope delegado OBRIGATÓRIO. Ausência de `scp` = token app-only (client credentials) → recusado;
        // `roles` (app permissions) nunca é aceito como substituto.
        var required = options.DelegatedScope;
        if (string.IsNullOrWhiteSpace(required))
            return false;
        var scp = principal.FindFirst("scp")?.Value;
        if (string.IsNullOrWhiteSpace(scp))
            return false;
        var scopes = scp.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!scopes.Contains(required, StringComparer.Ordinal))
            return false;

        // Cliente chamador deve ser o SPA configurado (azp em v2, appid em v1).
        var callerAppId = principal.FindFirst("azp")?.Value ?? principal.FindFirst("appid")?.Value;
        if (!GuidEquals(callerAppId, options.SpaClientId))
            return false;

        // tid/oid GUIDs válidos; tid é o tenant configurado. Canonicaliza para "D".
        if (!Guid.TryParse(principal.FindFirst("tid")?.Value, out var tid))
            return false;
        if (!Guid.TryParse(principal.FindFirst("oid")?.Value, out var oid))
            return false;
        if (!Guid.TryParse(options.TenantId, out var allowed) || tid != allowed)
            return false;

        var email = principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst("email")?.Value
            ?? principal.FindFirst("upn")?.Value;

        identity = new FederatedIdentity(tid.ToString("D"), oid.ToString("D"), email);
        return true;
    }

    private static bool GuidEquals(string? a, string? b) =>
        Guid.TryParse(a, out var ga) && Guid.TryParse(b, out var gb) && ga == gb;
}
