using AegisScore.Domain;

namespace AegisScore.Application.Services;

// ---- Comando de entrada -----------------------------------------------------

/// <summary>
/// Onboarding de um usuário no tenant AMBIENTE — a operação que o administrador inicial usa para adicionar
/// o gestor e os demais. Combina, de forma ATÔMICA, duas autoridades que existem separadas:
/// provisionar a identidade GLOBAL (quando nova) e conceder acesso ao tenant atual. Por isso a superfície
/// HTTP exige SIMULTANEAMENTE a policy global <c>PlatformAdmin</c> e o papel tenant-scoped <c>TenantAdmin</c>.
/// </summary>
/// <param name="Email">Login global da pessoa. Normalizado e único no sistema inteiro.</param>
/// <param name="DisplayName">Nome exibido NESTE tenant.</param>
/// <param name="Role">Papel TENANT-SCOPED concedido aqui (<see cref="TenantRole"/>). <c>PlatformAdmin</c> não existe neste tipo.</param>
/// <param name="InitialPassword">
/// Senha inicial OPCIONAL, conforme o modo (Local exige · Federated recusa · Hybrid opcional). ⚠️ Só é
/// aplicada quando a identidade é CRIADA. Se a pessoa JÁ existe, a senha informada é IGNORADA — conceder
/// acesso jamais redefine silenciosamente uma credencial existente.
/// </param>
public record OnboardTenantUserCommand(string Email, string DisplayName, TenantRole Role, string? InitialPassword);

// ---- Resultado de saída -----------------------------------------------------

/// <summary>
/// Desfecho do onboarding. Sucesso e recusas viajam como VALOR (não exceção): o boundary global as
/// traduziria num 500 opaco. Os três primeiros são sucesso e discriminam o que aconteceu — a UI precisa
/// disso para explicar, por exemplo, que a pessoa JÁ existia e a senha não foi tocada.
/// </summary>
public enum TenantUserOnboardingStatus
{
    /// <summary>Identidade NOVA criada + acesso concedido ao tenant atual (mesma transação).</summary>
    IdentityCreatedAndGranted = 0,

    /// <summary>Identidade JÁ existia; acesso NOVO concedido ao tenant atual. Senha/papel global/Entra preservados.</summary>
    ExistingIdentityGranted = 1,

    /// <summary>Identidade JÁ existia e JÁ tinha acesso aqui; papel/nome atualizados (reativa se inativo). Senha preservada.</summary>
    ExistingIdentityAccessUpdated = 2,

    /// <summary>E-mail ausente, malformado ou acima do teto (400).</summary>
    InvalidEmail = 3,

    /// <summary>Nome de exibição ausente ou acima do teto (400).</summary>
    InvalidDisplayName = 4,

    /// <summary>Papel não atribuível por esta superfície (403) — nunca <c>PlatformAdmin</c> nem valor indefinido.</summary>
    RoleNotAssignable = 5,

    /// <summary>Modo <c>Local</c>, identidade NOVA, sem senha (400).</summary>
    PasswordRequired = 6,

    /// <summary>Modo <c>Federated</c>, identidade NOVA, com senha (400) — credencial inutilizada.</summary>
    PasswordNotAllowed = 7,

    /// <summary>Senha presente porém fora da política de comprimento (400).</summary>
    WeakPassword = 8,
}

/// <summary>
/// Resultado do onboarding. <paramref name="User"/> só vem no sucesso; <paramref name="IdentityExisted"/>
/// permite à UI explicar que a pessoa já existia (e que a senha não foi alterada).
/// </summary>
public record TenantUserOnboardingResult(
    TenantUserOnboardingStatus Status, UserSummary? User = null, bool IdentityExisted = false, string? Detail = null)
{
    public bool Succeeded => Status is
        TenantUserOnboardingStatus.IdentityCreatedAndGranted or
        TenantUserOnboardingStatus.ExistingIdentityGranted or
        TenantUserOnboardingStatus.ExistingIdentityAccessUpdated;

    public static TenantUserOnboardingResult Ok(
        TenantUserOnboardingStatus status, UserSummary user, bool identityExisted) =>
        new(status, user, identityExisted);

    public static TenantUserOnboardingResult Rejected(TenantUserOnboardingStatus status, string? detail = null) =>
        new(status, null, false, detail);
}

// ---- Porta ------------------------------------------------------------------

/// <summary>
/// Onboarding de usuário no tenant ambiente. É a ÚNICA superfície que pode criar uma identidade global E
/// conceder acesso na mesma operação — e por isso exige as DUAS autoridades (PlatformAdmin global +
/// TenantAdmin do ambiente) na borda HTTP. Preserva rigorosamente a separação: um <c>TenantAdmin</c> sem
/// autoridade global NÃO alcança esta rota (403) e administra apenas os usuários já vinculados.
///
/// <b>Semântica.</b> Identidade inexistente → cria (respeitando a política de senha por modo) + concede
/// acesso, ATOMICAMENTE (sem estado parcial). Identidade existente → concede/atualiza/reativa o acesso ao
/// tenant atual, PRESERVANDO senha, papel global e vínculo Entra; a senha informada é ignorada. Nunca faz
/// listagem/busca global ampla de identidades — só a descoberta pontual por e-mail exato.
/// </summary>
public interface IPlatformTenantUserService
{
    Task<TenantUserOnboardingResult> OnboardAsync(OnboardTenantUserCommand command, CancellationToken ct = default);
}
