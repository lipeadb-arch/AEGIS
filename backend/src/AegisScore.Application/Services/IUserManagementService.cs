using AegisScore.Domain;

namespace AegisScore.Application.Services;

// ---- Comandos de entrada ----------------------------------------------------

/// <summary>
/// Provisionamento de uma identidade no tenant AMBIENTE. Não há <c>TenantId</c> aqui de propósito: o
/// vínculo é derivado do claim <c>tenant_id</c> do JWT e revalidado no carimbo de gravação. O que não
/// trafega não pode ser forjado.
/// </summary>
public record CreateUserCommand(string Email, string DisplayName, string Password, UserRole Role);

/// <summary>
/// Concessão de acesso de um e-mail ao tenant AMBIENTE.
///
/// ⚠️ <paramref name="TenantId"/> é ASSERÇÃO de defesa em profundidade, não parâmetro de roteamento
/// (mesmo desenho de <see cref="IControlStateWriter.ApplyVerdictAsync"/>): precisa casar com o tenant do
/// contexto, senão a operação é recusada. O modelo é Um-para-Muitos — <see cref="User"/> é
/// <see cref="ITenantOwned"/> com UM <c>TenantId</c> —, então "atribuir um usuário a outro tenant" é
/// impossível por construção: o <c>StampTenant</c> fail-closed rejeita a escrita cruzada. A operação
/// correta é o admin DO TENANT DE DESTINO conceder o acesso dentro do próprio ambiente.
/// </summary>
/// <param name="InitialPassword">
/// Obrigatória apenas quando a identidade ainda NÃO existe neste tenant. Identidades de tenants
/// distintos são independentes (senha, papel e refresh tokens próprios) — não há nada a herdar do
/// "mesmo" e-mail noutro ambiente, e tentar herdar exigiria leitura cross-tenant.
/// </param>
public record AssignUserToTenantCommand(
    Guid TenantId, string Email, UserRole Role, string? InitialPassword = null);

// ---- Resultados de saída ----------------------------------------------------

/// <summary>
/// Desfecho do provisionamento. Como na §20, conflito e validação são resultados ESPERADOS do fluxo
/// (→ 409/400 na borda) e viajam como VALOR: o <c>GlobalExceptionHandlingMiddleware</c> traduziria
/// qualquer throw num 500 opaco. Só <see cref="TenantSecurityException"/> sobe.
/// </summary>
public enum UserProvisioningStatus
{
    /// <summary>Identidade criada neste tenant.</summary>
    Created = 0,

    /// <summary>Identidade já existia neste tenant; papel/estado atualizados (só em Assign).</summary>
    AccessUpdated = 1,

    /// <summary>Já existe identidade com este e-mail NESTE tenant (índice único <c>(TenantId, Email)</c>).</summary>
    EmailAlreadyInUse = 2,

    /// <summary>E-mail ausente, malformado ou acima de 256 caracteres.</summary>
    InvalidEmail = 3,

    /// <summary>Senha fora da política (ver <see cref="IUserManagementService"/>).</summary>
    WeakPassword = 4,

    /// <summary>Nome de exibição ausente ou acima de 200 caracteres.</summary>
    InvalidDisplayName = 5,

    /// <summary>Papel não atribuível por esta superfície — ver a nota de escalonamento na interface.</summary>
    RoleNotAssignable = 6,

    /// <summary>Assign de identidade inexistente sem <c>InitialPassword</c>: não há senha a herdar.</summary>
    PasswordRequired = 7,
}

/// <summary>Projeção SEGURA de uma identidade. NUNCA carrega <c>PasswordHash</c>.</summary>
public record UserSummary(
    Guid Id,
    Guid TenantId,
    string Email,
    string DisplayName,
    UserRole Role,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

/// <summary>
/// Resultado do provisionamento. <paramref name="User"/> só vem preenchido no sucesso;
/// <paramref name="Detail"/> explica a recusa de validação (a política vive no serviço, não na borda —
/// duplicá-la no controller garantiria divergência).
/// </summary>
public record UserProvisioningResult(
    UserProvisioningStatus Status, UserSummary? User = null, string? Detail = null)
{
    public bool Succeeded =>
        Status is UserProvisioningStatus.Created or UserProvisioningStatus.AccessUpdated;

    public static UserProvisioningResult Ok(UserProvisioningStatus status, UserSummary user) =>
        new(status, user);

    public static UserProvisioningResult Rejected(UserProvisioningStatus status, string? detail = null) =>
        new(status, null, detail);
}

// ---- Porta ------------------------------------------------------------------

/// <summary>
/// Serviço de aplicação de IDENTIDADES. Cria usuários e concede acesso, sempre DENTRO do tenant
/// ambiente.
///
/// <b>Modelo de vínculo — Um-para-Muitos (decisão firmada).</b> <see cref="User"/> é
/// <see cref="ITenantOwned"/> com UM <c>TenantId</c>. Um mesmo e-mail em dois tenants são DUAS
/// identidades independentes — senha, papel, <c>IsActive</c> e refresh tokens próprios —, e o índice
/// único é <c>(TenantId, Email)</c>, não <c>Email</c>. É o que permite que nenhum token atravesse a
/// fronteira: não existe sujeito capaz de "trocar de tenant".
///
/// <b>Nada aqui fura o isolamento.</b> Sem leitura cross-tenant, sem <c>IgnoreQueryFilters</c>, sem
/// bypass de <c>TenantId</c>. O serviço não sabe — e não pode saber — se um e-mail existe noutro
/// ambiente: essa consulta é justamente o que o Global Query Filter fail-closed impede.
///
/// <b>⚠️ Escalonamento de privilégio.</b> <see cref="UserRole.PlatformAdmin"/> NÃO é atribuível por
/// esta superfície. Ele autoriza operações de PLATAFORMA (criar tenants — ver a §20), então deixar um
/// <c>TenantAdmin</c> emiti-lo transformaria admin de cliente em admin da plataforma com um POST. É
/// provisionado fora do onboarding self-service, como o próprio <see cref="UserRole"/> documenta.
///
/// <b>Política de senha (NIST SP 800-63B).</b> Comprimento mínimo de 12 e máximo de 128 caracteres,
/// <b>sem regras de composição</b> — o 800-63B desaconselha exigir maiúscula/dígito/símbolo, porque
/// empurra o usuário para padrões previsíveis ("Senha@123") sem ganho real de entropia. O hash é
/// PBKDF2-HMAC-SHA256 com 210k iterações (<c>Pbkdf2PasswordHasher</c>); a senha em claro nunca é
/// persistida nem registrada em log.
///
/// A implementação vive na Infrastructure (toca o DbContext); a porta, aqui — mesmo desenho de
/// <see cref="ITenantManagementService"/> e <see cref="IControlStateWriter"/>.
/// </summary>
public interface IUserManagementService
{
    /// <summary>
    /// Cria uma identidade no tenant ambiente com o papel informado. Estritamente CRIAÇÃO: e-mail já
    /// usado NESTE tenant devolve <see cref="UserProvisioningStatus.EmailAlreadyInUse"/> (409 na borda).
    ///
    /// A unicidade é invariante de BANCO (índice único <c>(TenantId, Email)</c>): a checagem prévia é
    /// fast-path e a corrida perdida no INSERT resolve no MESMO conflito.
    /// </summary>
    /// <exception cref="TenantSecurityException">Sem tenant resolvido no contexto (fail-closed).</exception>
    Task<UserProvisioningResult> CreateUserAsync(CreateUserCommand command, CancellationToken ct = default);

    /// <summary>
    /// Concede acesso ao tenant ambiente de forma IDEMPOTENTE — o caminho de gestão de permissões:
    /// <list type="bullet">
    /// <item>identidade ausente → cria (exige <c>InitialPassword</c>) e devolve <c>Created</c>;</item>
    /// <item>identidade presente → aplica o papel, REATIVA se estava inativa, e devolve
    /// <c>AccessUpdated</c>. A senha vigente é preservada: conceder permissão não é resetar credencial.</item>
    /// </list>
    /// </summary>
    /// <exception cref="TenantSecurityException">
    /// Sem tenant no contexto, ou <c>command.TenantId</c> divergente do tenant ambiente.
    /// </exception>
    Task<UserProvisioningResult> AssignUserToTenantAsync(
        AssignUserToTenantCommand command, CancellationToken ct = default);
}
