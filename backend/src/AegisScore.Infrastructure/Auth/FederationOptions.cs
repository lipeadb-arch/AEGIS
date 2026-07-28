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
    /// Fail-fast: em Federated/Hybrid a configuração necessária é OBRIGATÓRIA — falhamos ANTES de servir,
    /// não numa request de login. Em Local não há nada a exigir (é o modo sem federação).
    /// </summary>
    public void Validate()
    {
        if (!FederationEnabled) return;

        var faltando = new List<string>();
        if (string.IsNullOrWhiteSpace(TenantId)) faltando.Add(nameof(TenantId));
        if (string.IsNullOrWhiteSpace(ApiClientId)) faltando.Add(nameof(ApiClientId));
        if (string.IsNullOrWhiteSpace(ApiScope)) faltando.Add(nameof(ApiScope));
        if (string.IsNullOrWhiteSpace(SpaClientId)) faltando.Add(nameof(SpaClientId));
        if (string.IsNullOrWhiteSpace(Instance)) faltando.Add(nameof(Instance));

        if (faltando.Count > 0)
            throw new InvalidOperationException(
                $"Auth:Federation em modo {Mode} exige configuração completa. Ausente(s): " +
                $"{string.Join(", ", faltando)}. Defina por user-secrets/variável de ambiente " +
                "(valores são identificadores públicos, mas ficam fora do repositório).");
    }

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
