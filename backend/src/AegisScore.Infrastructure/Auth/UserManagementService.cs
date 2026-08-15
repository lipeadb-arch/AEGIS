using System.Data;
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
            DisplayName = TenantAccessPolicy.NormalizeDisplayName(command.DisplayName),
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
    /// Aplica papel, nome e reativação a um membership existente — o caminho idempotente da concessão. Passa
    /// pela MESMA autoridade guardada das operações administrativas (<see cref="ApplyGuardedMembershipChangeAsync"/>):
    /// conceder acesso a uma identidade existente NÃO pode virar um bypass do auto-rebaixamento, da proteção
    /// do último administrador ou da concorrência. A credencial global, o <c>PlatformRole</c> e o vínculo
    /// Entra NÃO são tocados de propósito.
    /// </summary>
    private async Task<AccessGrantResult> ApplyUpdateAsync(
        User membership, IdentityAccount account, GrantTenantAccessCommand command, Guid tenantId, CancellationToken ct)
    {
        var rejection = await ApplyGuardedMembershipChangeAsync(
            membership, command.ActorAccountId, command.DisplayName, command.Role, reactivate: true, tenantId, ct);
        if (rejection is { } r)
            return MapGrantGuardRejection(r);

        _log.LogInformation(
            "Acesso {UserId} atualizado no tenant {TenantId}: papel {Role}.",
            membership.Id, tenantId, membership.Role);

        return AccessGrantResult.Ok(AccessGrantStatus.AccessUpdated, Project(membership, account));
    }

    /// <summary>Traduz uma recusa de guarda (compartilhada) para o desfecho da CONCESSÃO. Só ocorre no
    /// caminho de membership existente; papel/nome já foram pré-validados por <see cref="GrantAccessAsync"/>.</summary>
    private static AccessGrantResult MapGrantGuardRejection(MembershipAdminStatus status) => status switch
    {
        MembershipAdminStatus.SelfDemotionForbidden => AccessGrantResult.Rejected(
            AccessGrantStatus.SelfDemotionForbidden,
            "Você não pode rebaixar o próprio papel de administrador ao conceder acesso. Peça a outro administrador."),
        MembershipAdminStatus.LastAdminProtected => AccessGrantResult.Rejected(
            AccessGrantStatus.LastAdminProtected,
            "Esta concessão rebaixaria o último administrador ativo do ambiente. Promova outra pessoa antes."),
        _ => AccessGrantResult.Rejected(AccessGrantStatus.RoleNotAssignable),   // inalcançável (pré-validado)
    };

    // ---- Listagem tenant-scoped -------------------------------------------------

    public async Task<IReadOnlyList<TenantUserListItem>> ListUsersAsync(CancellationToken ct = default)
    {
        RequireAmbientTenant();   // fail-closed ANTES de qualquer leitura — sem tenant não há listagem

        // O Global Query Filter de User já restringe ao tenant ambiente; o join com IdentityAccount (global)
        // traz o e-mail e o "tem credencial local?". Projeta em tipo ANÔNIMO no SQL e monta o record em
        // memória — projetar direto num record dentro do Join é o que o EF falha em traduzir (idioma do
        // TenantManagementService/AuthService).
        var rows = await (
            from u in _db.Users
            join a in _db.IdentityAccounts on u.IdentityAccountId equals a.Id
            select new
            {
                u.Id, a.Email, u.DisplayName, u.Role, u.IsActive,
                HasLocalCredential = a.PasswordHash != null,
                u.CreatedAt, u.LastLoginAt,
            }).ToListAsync(ct);

        // Ordenação em MEMÓRIA: o SQLite (provider dos testes) não ordena por DateTimeOffset — um ORDER BY no
        // servidor deixaria a listagem sem cobertura. Custo desprezível (poucos acessos por tenant).
        return rows
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new TenantUserListItem(
                r.Id, r.Email, r.DisplayName, r.Role, r.IsActive,
                r.HasLocalCredential, r.CreatedAt, r.LastLoginAt))
            .ToList();
    }

    // ---- Administração de membership (editar / desativar / reativar) ------------

    public async Task<MembershipAdminResult> UpdateMembershipAsync(
        UpdateMembershipCommand command, CancellationToken ct = default)
    {
        var tenantId = RequireAmbientTenant();

        // Valida entradas ANTES de tocar o alvo (mensagem certa, sem efeito parcial).
        if (command.Role is { } wantedRole && !TenantAccessPolicy.IsAssignableTenantRole(wantedRole))
            return MembershipAdminResult.Rejected(
                MembershipAdminStatus.RoleNotAssignable,
                "Papel inválido: use Analista, Gestor ou Administrador do tenant.");
        if (command.DisplayName is { } wantedName && !TenantAccessPolicy.IsValidDisplayName(wantedName))
            return MembershipAdminResult.Rejected(
                MembershipAdminStatus.InvalidDisplayName,
                $"Nome de exibição obrigatório, com até {TenantAccessPolicy.MaxDisplayNameLength} caracteres.");

        var (target, account) = await LoadMembershipAsync(command.MembershipId, ct);
        if (target is null || account is null)
            return MembershipAdminResult.Rejected(MembershipAdminStatus.NotFound);

        // MESMA autoridade guardada da concessão: auto-rebaixamento, último admin (sob concorrência) e
        // revogação de sessões ao reduzir privilégio. Edição não reativa (reactivate: false).
        var rejection = await ApplyGuardedMembershipChangeAsync(
            target, command.ActorAccountId, command.DisplayName, command.Role, reactivate: false, tenantId, ct);
        if (rejection is { } r)
            return MembershipAdminResult.Rejected(r, MessageForGuardRejection(r));

        _log.LogInformation(
            "Acesso {UserId} editado no tenant {TenantId}: papel {Role}.", target.Id, tenantId, target.Role);
        return MembershipAdminResult.Ok(Project(target, account));
    }

    /// <summary>Mensagem orientada à operação para as recusas de guarda compartilhadas.</summary>
    private static string MessageForGuardRejection(MembershipAdminStatus status) => status switch
    {
        MembershipAdminStatus.SelfDemotionForbidden =>
            "Você não pode rebaixar o próprio papel de administrador. Peça a outro administrador.",
        MembershipAdminStatus.LastAdminProtected =>
            "Este é o último administrador ativo do ambiente. Promova outra pessoa a Administrador antes de rebaixá-lo.",
        _ => "Operação não permitida.",
    };

    /// <summary>
    /// AUTORIDADE ÚNICA da mutação de um membership EXISTENTE (nome/papel/reativação) com TODAS as
    /// invariantes: auto-rebaixamento recusado, proteção do último administrador ativo (correta sob
    /// concorrência real, via <c>FOR UPDATE</c>) e revogação dos refresh tokens quando o privilégio é
    /// reduzido. Compartilhada por <see cref="ApplyUpdateAsync"/> (concessão/reativação) e
    /// <see cref="UpdateMembershipAsync"/> (edição), para que os DOIS caminhos apliquem EXATAMENTE as mesmas
    /// guardas — sem invariantes divergentes. Retorna <c>null</c> em sucesso, ou o status de recusa. A
    /// credencial global, o <c>PlatformRole</c> e o vínculo Entra NÃO são tocados por construção.
    /// </summary>
    private async Task<MembershipAdminStatus?> ApplyGuardedMembershipChangeAsync(
        User membership, Guid actorAccountId, string? newDisplayName, TenantRole? newRole, bool reactivate,
        Guid tenantId, CancellationToken ct)
    {
        var isSelf = membership.IdentityAccountId == actorAccountId;
        var targetRole = newRole ?? membership.Role;
        var demotesAdmin = membership.Role == TenantRole.TenantAdmin && targetRole != TenantRole.TenantAdmin;
        var reducesPrivilege = targetRole < membership.Role;   // menor ordinal = menos privilégio

        // Auto-rebaixamento administrativo: a pessoa não pode tirar o próprio papel de administrador.
        if (isSelf && demotesAdmin)
            return MembershipAdminStatus.SelfDemotionForbidden;

        async Task<MembershipAdminStatus?> ApplyAsync()
        {
            if (newDisplayName is { } dn)
                membership.DisplayName = TenantAccessPolicy.NormalizeDisplayName(dn);
            if (newRole is { } r)
                membership.Role = r;
            if (reactivate)
                membership.IsActive = true;
            await _db.SaveChangesAsync(ct);

            // Reduzir privilégio derruba as sessões daquele membership (o papel antigo não sobrevive no token).
            if (reducesPrivilege)
                await RevokeMembershipSessionsAsync(membership.Id, ct);
            return null;   // sucesso
        }

        // Guarda do último administrador só quando esta mudança rebaixa um admin ATIVO.
        return membership.IsActive && demotesAdmin
            ? await GuardLastActiveAdminAsync(
                tenantId, membership.Id, ApplyAsync, () => (MembershipAdminStatus?)MembershipAdminStatus.LastAdminProtected, ct)
            : await ApplyAsync();
    }

    public async Task<MembershipAdminResult> SetMembershipStatusAsync(
        SetMembershipStatusCommand command, CancellationToken ct = default)
    {
        var tenantId = RequireAmbientTenant();

        var (target, account) = await LoadMembershipAsync(command.MembershipId, ct);
        if (target is null || account is null)
            return MembershipAdminResult.Rejected(MembershipAdminStatus.NotFound);

        var isSelf = account.Id == command.ActorAccountId;

        // ---- Reativação: só adiciona acesso, sem guardas. NÃO restaura sessões (nunca des-revogamos). ----
        if (command.Active)
        {
            if (!target.IsActive)
            {
                target.IsActive = true;
                await _db.SaveChangesAsync(ct);
                _log.LogInformation("Acesso {UserId} REATIVADO no tenant {TenantId}.", target.Id, tenantId);
            }
            return MembershipAdminResult.Ok(Project(target, account));
        }

        // ---- Desativação ----
        // Auto-desativação: a pessoa não pode se trancar para fora.
        if (isSelf)
            return MembershipAdminResult.Rejected(
                MembershipAdminStatus.SelfDeactivationForbidden,
                "Você não pode desativar o próprio acesso. Peça a outro administrador.");

        if (!target.IsActive)
            return MembershipAdminResult.Ok(Project(target, account));   // já inativo: idempotente

        async Task<MembershipAdminResult> ApplyAsync()
        {
            target.IsActive = false;
            await _db.SaveChangesAsync(ct);
            // Desativar REVOGA os refresh tokens ativos do membership (as sessões não sobrevivem à desativação).
            await RevokeMembershipSessionsAsync(target.Id, ct);
            _log.LogInformation("Acesso {UserId} DESATIVADO no tenant {TenantId}.", target.Id, tenantId);
            return MembershipAdminResult.Ok(Project(target, account));
        }

        // Guarda do último administrador quando o alvo é um admin ATIVO (target.IsActive já é true aqui).
        return target.Role == TenantRole.TenantAdmin
            ? await GuardLastActiveAdminAsync(
                tenantId, target.Id, ApplyAsync,
                () => MembershipAdminResult.Rejected(
                    MembershipAdminStatus.LastAdminProtected,
                    "Este é o último administrador ativo do ambiente. Promova outra pessoa a Administrador antes " +
                    "de removê-lo."),
                ct)
            : await ApplyAsync();
    }

    /// <summary>
    /// Executa <paramref name="apply"/> só se, após remover/rebaixar o alvo, AINDA restar ao menos um
    /// <see cref="TenantRole.TenantAdmin"/> ATIVO no tenant. A correção SOB CONCORRÊNCIA vem de uma trava de
    /// linha no PostgreSQL: dentro da transação, <c>FOR UPDATE</c> sobre TODAS as linhas de admin ativo
    /// serializa duas remoções concorrentes dos últimos administradores — fechando o write-skew que, sob
    /// READ COMMITTED, deixaria as duas passarem e zeraria os administradores. No SQLite (testes de máquina
    /// de estados) a cláusula é omitida: as escritas já são serializadas, e a garantia real é validada em
    /// PostgreSQL descartável.
    /// </summary>
    private async Task<T> GuardLastActiveAdminAsync<T>(
        Guid tenantId, Guid targetId, Func<Task<T>> apply, Func<T> onLastAdmin, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockActiveAdminsAsync(tenantId, ct);

        var otherActiveAdmins = await _db.Users
            .CountAsync(u => u.Id != targetId && u.Role == TenantRole.TenantAdmin && u.IsActive, ct);
        if (otherActiveAdmins == 0)
        {
            await tx.RollbackAsync(ct);
            return onLastAdmin();
        }

        var result = await apply();
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// Tranca as linhas de administrador ATIVO do tenant até o fim da transação (só no PostgreSQL). Executado
    /// como comando cru para acompanhar o padrão do <c>DurableClaim</c> — <c>FOR UPDATE</c> não existe no SQLite,
    /// e o provedor é detectado pelo NOME da conexão, sem acoplar o pacote do Npgsql aqui.
    /// </summary>
    private async Task LockActiveAdminsAsync(Guid tenantId, CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        if (!conn.GetType().Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            return;   // SQLite: escritas serializadas; FOR UPDATE indisponível

        await _db.Database.ExecuteSqlRawAsync(
            "SELECT \"Id\" FROM \"Users\" WHERE \"TenantId\" = {0} AND \"Role\" = 2 AND \"IsActive\" = TRUE FOR UPDATE",
            new object[] { tenantId }, ct);
    }

    /// <summary>
    /// Revoga (RevokedAt = agora) os refresh tokens ATIVOS de um membership do tenant ambiente. Via
    /// <c>ExecuteUpdate</c> — atômico, fora do change tracker e participante da transação ativa (se houver).
    /// O teto de 10 min do access token JÁ emitido é preservado (não o encurtamos).
    /// </summary>
    private async Task RevokeMembershipSessionsAsync(Guid membershipId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await _db.UserRefreshTokens
            .Where(t => t.UserId == membershipId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, _ => now), ct);
    }

    /// <summary>
    /// Carrega o membership do tenant ambiente (Global Query Filter → outro tenant é o MESMO <c>null</c> de
    /// inexistente) junto da identidade global dona (para e-mail/projeção). <c>(null, null)</c> = não encontrado.
    /// </summary>
    private async Task<(User? Target, IdentityAccount? Account)> LoadMembershipAsync(
        Guid membershipId, CancellationToken ct)
    {
        var target = await _db.Users.FirstOrDefaultAsync(u => u.Id == membershipId, ct);
        if (target is null)
            return (null, null);
        var account = await _db.IdentityAccounts.FirstOrDefaultAsync(a => a.Id == target.IdentityAccountId, ct);
        return (target, account);
    }

    // ---- Validação --------------------------------------------------------------

    private static AccessGrantResult? ValidateDisplayName(string? raw) =>
        TenantAccessPolicy.IsValidDisplayName(raw)
            ? null
            : AccessGrantResult.Rejected(
                AccessGrantStatus.InvalidDisplayName,
                $"Nome de exibição obrigatório, com até {TenantAccessPolicy.MaxDisplayNameLength} caracteres.");

    /// <summary>
    /// [AEGIS-AUD-011] ALLOWLIST explícita dos papéis tenant-scoped válidos (autoridade em
    /// <see cref="TenantAccessPolicy"/>). O escalonamento a <c>PlatformAdmin</c> é barrado pelo próprio TIPO
    /// (não existe em <see cref="TenantRole"/>); o que resta guardar são valores INDEFINIDOS do enum, que o
    /// ASP.NET Core desserializa de número (<c>"role": 999</c> → <c>(TenantRole)999</c>).
    /// </summary>
    private static AccessGrantResult? ValidateRole(TenantRole role) =>
        TenantAccessPolicy.IsAssignableTenantRole(role)
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
        u.Id, u.TenantId, account.Email, u.DisplayName, u.Role, u.IsActive, u.CreatedAt, u.LastLoginAt,
        HasLocalCredential: account.PasswordHash is not null);
}
