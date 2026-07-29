namespace AegisScore.Application.Services;

// ---- Comando de entrada -----------------------------------------------------

/// <summary>
/// [AEGIS-AUD-010] Provisionamento de uma IDENTIDADE GLOBAL (a PESSOA — <c>IdentityAccount</c>). É a
/// autoridade de PLATAFORMA, distinta da concessão de acesso a tenant (ver <see cref="IUserManagementService"/>).
///
/// Só trafega o e-mail global e, conforme o modo de autenticação, uma senha local OPCIONAL. NÃO existe
/// aqui — de propósito — <c>TenantId</c>, membership, papel, DisplayName de tenant nem lista de tenants:
/// esta operação não concede acesso a nenhum ambiente, apenas cria a identidade dona da credencial.
/// </summary>
/// <param name="Email">Login global. Normalizado (minúsculas) e único no sistema inteiro.</param>
/// <param name="Password">
/// Senha local OPCIONAL. A obrigatoriedade depende do modo de federação (ver
/// <see cref="IIdentityProvisioningService"/>): <c>Local</c> exige; <c>Federated</c> recusa (não se guarda
/// credencial inutilizada); <c>Hybrid</c> aceita ausente ou presente. Ausência é <c>null</c> — jamais
/// vira string vazia nem hash fictício.
/// </param>
public record ProvisionIdentityCommand(string Email, string? Password = null);

// ---- Resultado de saída -----------------------------------------------------

/// <summary>
/// Desfecho do provisionamento global. Conflito e validação são resultados ESPERADOS do fluxo (→ 409/400
/// na borda) e viajam como VALOR — o <c>GlobalExceptionHandlingMiddleware</c> traduziria qualquer throw
/// num 500 opaco.
/// </summary>
public enum IdentityProvisioningStatus
{
    /// <summary>Identidade global criada.</summary>
    Created = 0,

    /// <summary>Já existe uma identidade com este e-mail GLOBAL (índice único <c>Email</c>) → conflito.</summary>
    EmailAlreadyInUse = 1,

    /// <summary>E-mail ausente, malformado ou acima de 256 caracteres.</summary>
    InvalidEmail = 2,

    /// <summary>Senha presente, porém fora da política de comprimento (ver <see cref="IIdentityProvisioningService"/>).</summary>
    WeakPassword = 3,

    /// <summary>Modo <c>Local</c> sem senha: a conta precisa de credencial local para autenticar.</summary>
    PasswordRequired = 4,

    /// <summary>
    /// Modo <c>Federated</c> com senha: a conta autentica só pelo Entra, então uma senha seria uma
    /// credencial inutilizada. Recusada em vez de armazenada.
    /// </summary>
    PasswordNotAllowed = 5,
}

/// <summary>
/// Projeção SEGURA da identidade global. NUNCA carrega <c>PasswordHash</c> — nem o hash sai daqui.
/// <paramref name="HasLocalCredential"/> distingue uma conta local/híbrida (com senha) de uma
/// federated-only (sem senha) sem revelar nada da credencial.
/// </summary>
public record IdentitySummary(Guid Id, string Email, bool HasLocalCredential, DateTimeOffset CreatedAt);

/// <summary>
/// Resultado do provisionamento. <paramref name="Identity"/> só vem preenchido no sucesso;
/// <paramref name="Detail"/> explica a recusa de validação (a política vive no serviço, não na borda).
/// </summary>
public record IdentityProvisioningResult(
    IdentityProvisioningStatus Status, IdentitySummary? Identity = null, string? Detail = null)
{
    public bool Succeeded => Status is IdentityProvisioningStatus.Created;

    public static IdentityProvisioningResult Ok(IdentitySummary identity) =>
        new(IdentityProvisioningStatus.Created, identity);

    public static IdentityProvisioningResult Rejected(IdentityProvisioningStatus status, string? detail = null) =>
        new(status, null, detail);
}

// ---- Porta ------------------------------------------------------------------

/// <summary>
/// [AEGIS-AUD-010] Serviço de aplicação de PROVISIONAMENTO GLOBAL DE IDENTIDADE. Cria a
/// <c>IdentityAccount</c> — entidade GLOBAL, que ATRAVESSA tenants — e NADA MAIS: nunca cria
/// <c>User</c> (membership), nunca concede acesso a tenant, nunca atribui papel. A concessão de acesso é
/// uma autoridade SEPARADA (<see cref="IUserManagementService"/>), com papel e superfície próprios.
///
/// <b>Por que separado da concessão de acesso.</b> Antes, uma rota de <c>TenantAdmin</c> criava a
/// identidade global E o acesso ao tenant no mesmo POST — um admin de cliente conseguia materializar uma
/// entidade global e uma credencial, extrapolando a própria autoridade. Aqui a criação da identidade é
/// exclusiva da PLATAFORMA (a superfície HTTP exige <c>PlatformAdmin</c>).
///
/// <b>Política de senha por modo de autenticação (<c>Auth:Federation:Mode</c>).</b>
/// <list type="bullet">
/// <item><c>Local</c>: senha local OBRIGATÓRIA (é o único fator).</item>
/// <item><c>Federated</c>: a conta é provisionada SEM senha; uma senha enviada é RECUSADA (não se guarda
/// credencial que nunca será usada) → <see cref="IdentityProvisioningStatus.PasswordNotAllowed"/>.</item>
/// <item><c>Hybrid</c>: senha OPCIONAL — a conta pode ter credencial local e também federar.</item>
/// </list>
/// Quando fornecida, a senha segue a MESMA política do console (NIST SP 800-63B: 12–128 caracteres, sem
/// regra de composição) e o MESMO hash PBKDF2-HMAC-SHA256 com 210k iterações. Senha ausente é gravada como
/// <c>null</c> — nunca string vazia nem hash fictício.
///
/// <b>Isolamento.</b> A identidade global não é <c>ITenantOwned</c> e não tem query filter; a unicidade do
/// e-mail é invariante de BANCO (índice único), então a corrida entre dois provisionamentos do mesmo e-mail
/// resolve no MESMO conflito da checagem prévia. A implementação vive na Infrastructure (toca o DbContext);
/// a porta, aqui — mesmo desenho de <see cref="IUserManagementService"/> e <see cref="ITenantManagementService"/>.
/// </summary>
public interface IIdentityProvisioningService
{
    /// <summary>
    /// Cria uma identidade global com o e-mail informado e, conforme o modo, a senha local. Estritamente
    /// CRIAÇÃO de <c>IdentityAccount</c>: e-mail já usado GLOBALMENTE devolve
    /// <see cref="IdentityProvisioningStatus.EmailAlreadyInUse"/> (409 na borda). Nunca cria membership
    /// nem concede acesso a tenant.
    /// </summary>
    Task<IdentityProvisioningResult> ProvisionAsync(
        ProvisionIdentityCommand command, CancellationToken ct = default);
}
