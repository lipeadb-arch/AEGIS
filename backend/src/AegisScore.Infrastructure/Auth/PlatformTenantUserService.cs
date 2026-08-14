using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Auth;

/// <summary>
/// Implementação do onboarding de usuário no tenant ambiente (ver <see cref="IPlatformTenantUserService"/>
/// para o contrato). Combina, num único ato, a criação da identidade GLOBAL (quando nova) e a concessão de
/// acesso ao tenant atual — as duas autoridades que vivem separadas em <see cref="IdentityProvisioningService"/>
/// e <see cref="UserManagementService"/>.
///
/// <b>Atomicidade sem transação explícita.</b> Para uma identidade NOVA, a <c>IdentityAccount</c> e o
/// <c>User</c> (membership) são adicionados juntos e persistidos num ÚNICO <c>SaveChanges</c> — que já é uma
/// transação. Assim, ou nascem os dois, ou nenhum: nunca sobra uma identidade órfã sem acesso. Para uma
/// identidade EXISTENTE, delega a concessão ao <see cref="IUserManagementService"/> (autoridade tenant-scoped),
/// que jamais toca credencial, papel global ou vínculo Entra.
///
/// <b>Isolamento.</b> O tenant é o AMBIENTE (claim do JWT, via <see cref="ITenantContext"/>): o membership é
/// carimbado pelo <c>StampTenant</c> fail-closed. A senha informada só é usada quando a identidade é criada —
/// nunca redefine uma credencial existente.
/// </summary>
public sealed class PlatformTenantUserService : IPlatformTenantUserService
{
    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IUserManagementService _users;
    private readonly IPasswordHasher _hasher;
    private readonly FederationOptions _federation;
    private readonly ILogger<PlatformTenantUserService> _log;

    public PlatformTenantUserService(
        AegisScoreDbContext db,
        ITenantContext tenant,
        IUserManagementService users,
        IPasswordHasher hasher,
        IOptions<FederationOptions> federation,
        ILogger<PlatformTenantUserService> log)
    {
        _db = db;
        _tenant = tenant;
        _users = users;
        _hasher = hasher;
        _federation = federation.Value;
        _log = log;
    }

    public async Task<TenantUserOnboardingResult> OnboardAsync(
        OnboardTenantUserCommand command, CancellationToken ct = default)
    {
        RequireAmbientTenant();   // fail-closed ANTES de qualquer escrita

        var email = EmailPolicy.Normalize(command.Email);
        if (!EmailPolicy.IsValid(email))
            return TenantUserOnboardingResult.Rejected(
                TenantUserOnboardingStatus.InvalidEmail,
                $"E-mail obrigatório, em formato válido e com até {EmailPolicy.MaxLength} caracteres.");

        if (!TenantAccessPolicy.IsValidDisplayName(command.DisplayName))
            return TenantUserOnboardingResult.Rejected(
                TenantUserOnboardingStatus.InvalidDisplayName,
                $"Nome de exibição obrigatório, com até {TenantAccessPolicy.MaxDisplayNameLength} caracteres.");

        if (!TenantAccessPolicy.IsAssignableTenantRole(command.Role))
            return TenantUserOnboardingResult.Rejected(
                TenantUserOnboardingStatus.RoleNotAssignable,
                "Papel inválido: use Analista, Gestor ou Administrador do tenant.");

        // ≤2 iterações: se a criação perder a corrida do e-mail, a identidade passa a existir e a próxima
        // volta toma o caminho de concessão a identidade existente (converge sempre).
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var identity = await _db.IdentityAccounts.FirstOrDefaultAsync(a => a.Email == email, ct);
            if (identity is not null)
                return await GrantToExistingAsync(identity, command, ct);

            // Identidade NOVA: resolve a senha por modo ANTES de escrever (recusa não deixa rastro).
            var mode = PasswordPolicy.ResolveForNewIdentity(
                _federation, command.InitialPassword, _hasher, out var hash, out var detail);
            if (mode != PasswordPolicy.ModeOutcome.Ok)
                return MapPasswordRejection(mode, detail);

            var created = await TryCreateIdentityAndGrantAsync(email, hash, command, ct);
            if (created is not null)
                return created;
            // else: corrida do e-mail — refaz o loop como concessão a identidade existente.
        }

        // Inalcançável: uma corrida de e-mail garante que a identidade exista na 2ª volta. Defensivo.
        throw new InvalidOperationException("Onboarding não convergiu após corrida de e-mail global.");
    }

    /// <summary>
    /// Cria a identidade global e o membership do tenant ambiente num ÚNICO <c>SaveChanges</c> (atômico).
    /// Devolve <c>null</c> quando o índice único de e-mail rejeita a criação (corrida) — o chamador refaz
    /// como concessão a identidade existente.
    /// </summary>
    private async Task<TenantUserOnboardingResult?> TryCreateIdentityAndGrantAsync(
        string email, string? passwordHash, OnboardTenantUserCommand command, CancellationToken ct)
    {
        var identity = new IdentityAccount
        {
            Email = email,
            PasswordHash = passwordHash,   // null quando federated-only — nunca "" nem hash fictício
        };
        var membership = new User
        {
            Account = identity,
            DisplayName = TenantAccessPolicy.NormalizeDisplayName(command.DisplayName),
            Role = command.Role,
            IsActive = true,
            // TenantId é carimbado no SaveChanges (fail-closed) — nunca atribuído aqui.
        };
        _db.IdentityAccounts.Add(identity);
        _db.Users.Add(membership);

        try
        {
            await _db.SaveChangesAsync(ct);   // ATÔMICO: identidade + acesso, ou nenhum dos dois
        }
        catch (DbUpdateException ex)
        {
            // Corrida do e-mail GLOBAL: outra sessão provisionou a MESMA pessoa entre o find e este insert.
            // O índice único rejeitou; nada foi persistido (uma transação só). Desanexa os dois e sinaliza
            // retry. O log NÃO inclui o e-mail (sem PII).
            _db.Entry(membership).State = EntityState.Detached;
            _db.Entry(identity).State = EntityState.Detached;
            _log.LogWarning(ex,
                "Onboarding concorrente rejeitado pelo índice único de e-mail — refazendo como concessão a identidade existente.");
            return null;
        }

        _log.LogInformation(
            "Onboarding: identidade global {AccountId} criada ({Credencial}) e acesso {UserId} concedido no tenant {TenantId} como {Role}.",
            identity.Id, passwordHash is null ? "federated-only" : "com credencial local",
            membership.Id, membership.TenantId, membership.Role);

        return TenantUserOnboardingResult.Ok(
            TenantUserOnboardingStatus.IdentityCreatedAndGranted, Project(membership, identity), identityExisted: false);
    }

    /// <summary>
    /// Concede/atualiza/reativa o acesso ao tenant atual para uma identidade JÁ existente, reusando a
    /// autoridade tenant-scoped. PRESERVA senha, papel global e vínculo Entra — a senha informada é ignorada.
    /// </summary>
    private async Task<TenantUserOnboardingResult> GrantToExistingAsync(
        IdentityAccount identity, OnboardTenantUserCommand command, CancellationToken ct)
    {
        var grant = await _users.GrantAccessAsync(
            new GrantTenantAccessCommand(identity.Id, command.DisplayName, command.Role), ct);

        return grant.Status switch
        {
            AccessGrantStatus.Granted => TenantUserOnboardingResult.Ok(
                TenantUserOnboardingStatus.ExistingIdentityGranted, grant.User!, identityExisted: true),
            AccessGrantStatus.AccessUpdated => TenantUserOnboardingResult.Ok(
                TenantUserOnboardingStatus.ExistingIdentityAccessUpdated, grant.User!, identityExisted: true),
            AccessGrantStatus.RoleNotAssignable => TenantUserOnboardingResult.Rejected(
                TenantUserOnboardingStatus.RoleNotAssignable, grant.Detail),
            AccessGrantStatus.InvalidDisplayName => TenantUserOnboardingResult.Rejected(
                TenantUserOnboardingStatus.InvalidDisplayName, grant.Detail),
            // IdentityNotFound: corrida improvável (o modelo não faz exclusão física de identidade).
            _ => TenantUserOnboardingResult.Rejected(
                TenantUserOnboardingStatus.InvalidEmail, "Não foi possível localizar a identidade. Tente novamente."),
        };
    }

    private static TenantUserOnboardingResult MapPasswordRejection(
        PasswordPolicy.ModeOutcome outcome, string? detail) => outcome switch
    {
        PasswordPolicy.ModeOutcome.PasswordRequired =>
            TenantUserOnboardingResult.Rejected(TenantUserOnboardingStatus.PasswordRequired, detail),
        PasswordPolicy.ModeOutcome.PasswordNotAllowed =>
            TenantUserOnboardingResult.Rejected(TenantUserOnboardingStatus.PasswordNotAllowed, detail),
        _ => TenantUserOnboardingResult.Rejected(TenantUserOnboardingStatus.WeakPassword, detail),
    };

    private void RequireAmbientTenant()
    {
        if (_tenant.TenantId is null || _tenant.TenantId == Guid.Empty)
            throw new TenantSecurityException("Onboarding sem tenant resolvido no contexto (fail-closed).");
    }

    private static UserSummary Project(User u, IdentityAccount account) => new(
        u.Id, u.TenantId, account.Email, u.DisplayName, u.Role, u.IsActive, u.CreatedAt, u.LastLoginAt,
        HasLocalCredential: account.PasswordHash is not null);
}
