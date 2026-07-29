using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Auth;

/// <summary>
/// [AEGIS-AUD-010] Implementação da CONCESSÃO DE ACESSO A TENANT (ver <see cref="IUserManagementService"/>
/// para o contrato e o modelo de vínculo). Adapter da Infrastructure — vive ao lado do
/// <see cref="AuthService"/> e do <see cref="IdentityProvisioningService"/> (o dono do provisionamento global).
///
/// Autoridade tenant-scoped e SÓ isso: cria/atualiza o <see cref="User"/> (membership) no tenant ambiente.
/// Não injeta <c>IPasswordHasher</c> — não toca credencial, por construção. Fail-closed: toda leitura de
/// membership passa pelo Global Query Filter do tenant ambiente e toda escrita pelo <c>StampTenant</c>. O
/// <c>TenantId</c> jamais é atribuído à mão — quem carimba é o <see cref="AegisScoreDbContext"/>.
/// </summary>
public sealed class UserManagementService : IUserManagementService
{
    private const int MaxDisplayNameLength = 200;

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<UserManagementService> _log;

    public UserManagementService(
        AegisScoreDbContext db,
        ITenantContext tenant,
        ILogger<UserManagementService> log)
    {
        _db = db;
        _tenant = tenant;
        _log = log;
    }

    public async Task<AccessGrantResult> GrantAccessAsync(
        GrantTenantAccessCommand command, CancellationToken ct = default)
    {
        var tenantId = RequireAmbientTenant();

        if (ValidateDisplayName(command.DisplayName) is { } displayNameRejection)
            return displayNameRejection;
        if (ValidateRole(command.Role) is { } roleRejection)
            return roleRejection;

        // A identidade global deve EXISTIR previamente. IdentityAccount não tem query filter (é global):
        // esta leitura por Id enxerga o sistema inteiro. Ausente → resposta genérica, SEM criar nada — o
        // provisionamento é a autoridade separada do IdentityProvisioningService (PlatformAdmin).
        var account = await _db.IdentityAccounts
            .FirstOrDefaultAsync(a => a.Id == command.IdentityAccountId, ct);
        if (account is null)
            return AccessGrantResult.Rejected(AccessGrantStatus.IdentityNotFound);

        // O membership DESTA pessoa a ESTE tenant (o query filter restringe ao ambiente atual — um
        // membership em OUTRO tenant é invisível aqui e nunca é tocado).
        var existing = await _db.Users
            .FirstOrDefaultAsync(u => u.IdentityAccountId == account.Id, ct);

        // ---- Já tem acesso a este ambiente: gestão de permissão, não de credencial ----
        if (existing is not null)
            return await ApplyUpdateAsync(existing, account, command, tenantId, ct);

        // ---- Sem acesso aqui ainda: cria o membership SOMENTE no tenant ambiente ----
        var granted = new User
        {
            Account = account,
            DisplayName = command.DisplayName.Trim(),
            Role = command.Role,
            IsActive = true,
            // TenantId é carimbado no SaveChanges (fail-closed) — nunca atribuído aqui.
        };
        _db.Users.Add(granted);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Corrida perdida no índice único (TenantId, IdentityAccountId): outra concessão concorrente da
            // MESMA pessoa ao MESMO tenant venceu. Idempotente — reconcilia no membership vencedor.
            _db.Entry(granted).State = EntityState.Detached;
            var winner = await _db.Users
                .FirstOrDefaultAsync(u => u.IdentityAccountId == account.Id, ct);
            if (winner is null) throw;   // não era corrida de unicidade de membership — propaga
            _log.LogWarning(ex,
                "Concessão concorrente no tenant {TenantId} — reconciliada pelo membership vencedor.", tenantId);
            return await ApplyUpdateAsync(winner, account, command, tenantId, ct);
        }

        _log.LogInformation(
            "Acesso {UserId} concedido no tenant {TenantId} à identidade {AccountId} como {Role}.",
            granted.Id, tenantId, account.Id, granted.Role);

        return AccessGrantResult.Ok(AccessGrantStatus.Granted, Project(granted, account));
    }

    /// <summary>
    /// Aplica papel, nome e reativação a um membership existente — o caminho idempotente. A credencial
    /// global e o vínculo Entra NÃO são tocados de propósito: conceder acesso não é resetar senha nem
    /// revincular identidade.
    /// </summary>
    private async Task<AccessGrantResult> ApplyUpdateAsync(
        User membership, IdentityAccount account, GrantTenantAccessCommand command, Guid tenantId, CancellationToken ct)
    {
        var reactivated = !membership.IsActive;
        membership.Role = command.Role;
        membership.DisplayName = command.DisplayName.Trim();
        membership.IsActive = true;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Acesso {UserId} atualizado no tenant {TenantId}: papel {Role}{Reactivated}.",
            membership.Id, tenantId, membership.Role, reactivated ? ", REATIVADO" : "");

        return AccessGrantResult.Ok(AccessGrantStatus.AccessUpdated, Project(membership, account));
    }

    // ---- Validação --------------------------------------------------------------

    private static AccessGrantResult? ValidateDisplayName(string? raw)
    {
        var name = (raw ?? "").Trim();
        if (name.Length is 0 or > MaxDisplayNameLength)
            return AccessGrantResult.Rejected(
                AccessGrantStatus.InvalidDisplayName,
                $"Nome de exibição obrigatório, com até {MaxDisplayNameLength} caracteres.");
        return null;
    }

    /// <summary>
    /// [AEGIS-AUD-011] ALLOWLIST explícita dos papéis tenant-scoped válidos. Com a separação dos eixos, a
    /// autoridade global (<c>PlatformAdmin</c>) já NÃO existe em <see cref="TenantRole"/> — não há o que
    /// comparar aqui, o escalonamento é barrado pelo próprio TIPO. O que resta guardar são valores
    /// INDEFINIDOS do enum: o ASP.NET Core desserializa enum de número, então <c>"role": 999</c> chega como
    /// <c>(TenantRole)999</c>, que uma checagem por desigualdade deixaria passar e corromperia o membership
    /// (papel inválido → claim de papel inválida depois). Só Analyst/Manager/TenantAdmin são aceitos, na
    /// criação e na atualização.
    /// </summary>
    private static AccessGrantResult? ValidateRole(TenantRole role) =>
        role is TenantRole.Analyst or TenantRole.Manager or TenantRole.TenantAdmin
            ? null
            : AccessGrantResult.Rejected(
                AccessGrantStatus.RoleNotAssignable,
                "Papel inválido para concessão de acesso a tenant: use Analyst, Manager ou TenantAdmin. " +
                "Valores indefinidos do enum são recusados.");

    // ---- Helpers ----------------------------------------------------------------

    /// <summary>Tenant ambiente, fail-closed. Falhar aqui dá a mensagem certa e evita montar a entidade à toa.</summary>
    private Guid RequireAmbientTenant() => _tenant.TenantId
        ?? throw new TenantSecurityException(
            "Concessão de acesso sem tenant resolvido no contexto (fail-closed).");

    /// <summary>
    /// Projeção de saída SEM o hash de senha (ver <see cref="UserSummary"/>). Recebe os dois lados porque o
    /// e-mail vive na conta global e o resto no membership.
    /// </summary>
    private static UserSummary Project(User u, IdentityAccount account) => new(
        u.Id, u.TenantId, account.Email, u.DisplayName, u.Role, u.IsActive, u.CreatedAt, u.LastLoginAt);
}
