using System.Diagnostics.CodeAnalysis;

namespace AegisScore.Infrastructure.Auth;

/// <summary>
/// [AEGIS-AUD-007] Modo de autenticação da plataforma.
///  - <see cref="Local"/>: só credenciais locais do AEGIS (padrão; é o que dev/demonstração usam).
///  - <see cref="Federated"/>: só identidade corporativa (Entra ID). O login por senha fica DESABILITADO.
///  - <see cref="Hybrid"/>: aceita os dois. Precisa ser ligado EXPLICITAMENTE.
/// </summary>
public enum FederationMode { Local, Federated, Hybrid }

/// <summary>
/// [AEGIS-AUD-007] Configuração da federação com o Microsoft Entra ID. Todos os valores aqui são
/// IDENTIFICADORES PÚBLICOS (tenant, client ids, scope, authority) — não são segredos —, mas os valores
/// reais ficam FORA do repositório (user-secrets/env var), como o resto da configuração sensível.
///
/// O Entra apenas AUTENTICA a identidade corporativa; após validar o token externo, o AEGIS emite o seu
/// próprio par (JWT local + refresh HttpOnly) usando o membership interno já existente. Nada aqui
/// substitui o esquema JWT local nem cria conta/membership/tenant (provisionamento é o AUD-010).
/// </summary>
public sealed class FederationOptions
{
    public const string SectionName = "Auth:Federation";

    /// <summary>Modo de autenticação. Padrão <see cref="FederationMode.Local"/> — dev/demonstração intactos.</summary>
    public FederationMode Mode { get; set; } = FederationMode.Local;

    /// <summary>Tenant (diretório) do Entra permitido. O <c>tid</c> do token DEVE coincidir com este.</summary>
    public string? TenantId { get; set; }

    /// <summary>Application (client) ID da API no Entra — é a <c>audience</c> esperada no token externo.</summary>
    public string? ApiClientId { get; set; }

    /// <summary>Scope que a API expõe (ex.: <c>api://&lt;api-client-id&gt;/access_as_user</c>). O SPA o solicita.</summary>
    public string? ApiScope { get; set; }

    /// <summary>Client ID PÚBLICO do SPA (public client, sem secret). Vai ao frontend na config sanitizada.</summary>
    public string? SpaClientId { get; set; }

    /// <summary>Base da authority. Padrão: nuvem pública. A authority final deriva do tenant.</summary>
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>Federação ligada? (Federated ou Hybrid). Em Local nenhum token externo é aceito.</summary>
    public bool FederationEnabled => Mode is FederationMode.Federated or FederationMode.Hybrid;

    /// <summary>Login por senha permitido? Falso apenas em <see cref="FederationMode.Federated"/>.</summary>
    public bool PasswordLoginEnabled => Mode is FederationMode.Local or FederationMode.Hybrid;

    /// <summary>Authority OIDC do tenant (usada pelo esquema JWT Bearer do Entra para buscar o JWKS).</summary>
    public string Authority => $"{Instance.TrimEnd('/')}/{TenantId}/v2.0";

    /// <summary>Issuers aceitos: v2.0 e v1.0 (STS) do MESMO tenant — cobre variação de versão do endpoint.</summary>
    public string[] ValidIssuers => new[]
    {
        $"{Instance.TrimEnd('/')}/{TenantId}/v2.0",
        $"https://sts.windows.net/{TenantId}/",
    };

    /// <summary>Audiences aceitas: o client id cru e a forma <c>api://&lt;client-id&gt;</c>.</summary>
    public string[] ValidAudiences => new[] { ApiClientId!, $"api://{ApiClientId}" };

    /// <summary>
    /// Prefixo esperado do scope da API — <c>api://&lt;ApiClientId&gt;/</c>. Extração CENTRALIZADA: tanto a
    /// validação quanto a policy do endpoint derivam o nome do scope delegado daqui, sem repetir a regra.
    /// </summary>
    public string ScopePrefix => $"api://{ApiClientId}/";

    /// <summary>
    /// Nome do scope DELEGADO exigido (ex.: <c>access_as_user</c>), derivado do <see cref="ApiScope"/> —
    /// a parte após <see cref="ScopePrefix"/>. <c>null</c> quando o scope não está no formato esperado da
    /// API configurada (recurso errado, sem nome, ou só o identificador do recurso).
    /// </summary>
    public string? DelegatedScope
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ApiScope) || string.IsNullOrWhiteSpace(ApiClientId))
                return null;
            return ApiScope.StartsWith(ScopePrefix, StringComparison.OrdinalIgnoreCase)
                   && ApiScope.Length > ScopePrefix.Length
                ? ApiScope[ScopePrefix.Length..]
                : null;
        }
    }

    /// <summary>
    /// Fail-fast: a configuração é validada ANTES de servir, não numa request de login. Um <see cref="Mode"/>
    /// desconhecido (config numérica inválida) é recusado em qualquer modo; em Federated/Hybrid, TenantId,
    /// ApiClientId e SpaClientId precisam ser GUIDs válidos, <see cref="Instance"/> uma URI HTTPS absoluta e
    /// <see cref="ApiScope"/> o formato <c>api://&lt;ApiClientId&gt;/&lt;scope&gt;</c> (não só o recurso).
    /// Em Local não há nada a exigir (é o modo sem federação).
    /// </summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Mode))
            throw new InvalidOperationException(
                $"Auth:Federation:Mode inválido ('{(int)Mode}'). Use Local, Federated ou Hybrid.");

        if (!FederationEnabled) return;

        var problemas = new List<string>();
        if (!Guid.TryParse(TenantId, out _)) problemas.Add($"{nameof(TenantId)} deve ser um GUID válido");
        if (!Guid.TryParse(ApiClientId, out _)) problemas.Add($"{nameof(ApiClientId)} deve ser um GUID válido");
        if (!Guid.TryParse(SpaClientId, out _)) problemas.Add($"{nameof(SpaClientId)} deve ser um GUID válido");
        if (!IsHttpsAbsolute(Instance)) problemas.Add($"{nameof(Instance)} deve ser uma URI HTTPS absoluta");
        if (string.IsNullOrWhiteSpace(DelegatedScope))
            problemas.Add(
                $"{nameof(ApiScope)} deve estar no formato 'api://<ApiClientId>/<scope>' " +
                "(ex.: api://<ApiClientId>/access_as_user) — não vazio e não apenas o identificador do recurso");

        if (problemas.Count > 0)
            throw new InvalidOperationException(
                $"Auth:Federation em modo {Mode} exige configuração válida. Problema(s): " +
                $"{string.Join("; ", problemas)}. Defina por user-secrets/variável de ambiente " +
                "(identificadores públicos, mas fora do repositório).");
    }

    private static bool IsHttpsAbsolute(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Projeção PÚBLICA e sanitizada para o SPA. NUNCA carrega segredo (não há segredo aqui).</summary>
    public FederationPublicConfig ToPublicConfig() => new(
        Enabled: FederationEnabled,
        Mode: Mode.ToString(),
        PasswordLoginEnabled: PasswordLoginEnabled,
        Authority: FederationEnabled ? Authority : null,
        SpaClientId: FederationEnabled ? SpaClientId : null,
        Scope: FederationEnabled ? ApiScope : null);
}

/// <summary>
/// Configuração pública que o SPA consome para inicializar o MSAL e decidir a UI. Só identificadores
/// públicos — enabled, authority, client id do SPA e scope. Nunca inclui client secret ou qualquer segredo.
/// </summary>
public sealed record FederationPublicConfig(
    bool Enabled,
    string Mode,
    bool PasswordLoginEnabled,
    string? Authority,
    string? SpaClientId,
    string? Scope);

/// <summary>Constantes do esquema de autenticação do Entra (separado do Bearer local do AEGIS).</summary>
[SuppressMessage("Design", "CA1052", Justification = "Agrupa constantes de esquema, padrão do projeto.")]
public static class FederatedAuthDefaults
{
    /// <summary>Nome do esquema JWT Bearer que valida os tokens do Entra — só ele protege a troca.</summary>
    public const string Scheme = "EntraId";
}
