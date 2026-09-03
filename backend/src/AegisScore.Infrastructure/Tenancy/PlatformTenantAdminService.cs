using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Tenancy;

/// <summary>
/// [AEGIS-MVP-ADMIN-LIFECYCLE-01] Implementação do ciclo de vida ADMINISTRATIVO dos tenants (ver
/// <see cref="IPlatformTenantAdminService"/> para o contrato). Adapter da Infrastructure — opera sobre a
/// entidade GLOBAL <see cref="Tenant"/> (que NÃO é <see cref="ITenantOwned"/>, então não tem query filter nem
/// stamping: é exatamente o sujeito que um <c>PlatformAdmin</c> administra cross-tenant). A AUTORIDADE é a
/// policy de plataforma na borda HTTP; aqui só aplicamos regra e traduzimos desfecho.
///
/// Nada aqui exclui fisicamente tenant, altera slug, toca score/ledger/NIST ou concede autoridade nova. A
/// suspensão IMPEDE o uso operacional imediatamente na porta de entrada: login, seleção, troca e RENOVAÇÃO já
/// recusam o tenant suspenso, e os refresh tokens ativos do ambiente são revogados. Um access token JÁ EMITIDO
/// permanece válido até o seu vencimento (o teto de vida é curto — ver <c>JwtOptions.AccessTokenMinutes</c>);
/// não há revogação retroativa de access token aqui, e nós NÃO afirmamos "sessões encerradas imediatamente":
/// as sessões deixam de poder ser renovadas e expiram no prazo normal do token.
/// </summary>
public sealed class PlatformTenantAdminService : IPlatformTenantAdminService
{
    /// <summary>Teto do nome de exibição do tenant (espelha o limite de 200 do formulário de criação).</summary>
    private const int MaxTenantNameLength = 200;

    private readonly AegisScoreDbContext _db;
    private readonly ILogger<PlatformTenantAdminService> _log;

    public PlatformTenantAdminService(AegisScoreDbContext db, ILogger<PlatformTenantAdminService> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<IReadOnlyList<TenantAdminSummary>> ListTenantsAsync(CancellationToken ct = default)
    {
        // Tenant é GLOBAL (sem query filter): a listagem enxerga TODOS os clientes, inclusive suspensos — é o
        // catálogo da plataforma. Ordena em memória (o SQLite dos testes não ordena por DateTimeOffset; o Name
        // é estável e barato).
        var rows = await _db.Tenants.AsNoTracking().ToListAsync(ct);
        return rows
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Id)
            .Select(t => new TenantAdminSummary(t.Id, t.Name, t.Slug, t.Status, t.CreatedAt, t.UpdatedAt))
            .ToList();
    }

    public async Task<TenantAdminMutationResult> RenameTenantAsync(
        RenameTenantCommand command, CancellationToken ct = default)
    {
        var name = (command.Name ?? "").Trim();
        if (name.Length is 0 or > MaxTenantNameLength)
            return TenantAdminMutationResult.Rejected(
                TenantAdminMutationStatus.InvalidName,
                $"Nome obrigatório, com até {MaxTenantNameLength} caracteres.");

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == command.TenantId, ct);
        if (tenant is null)
            return TenantAdminMutationResult.Rejected(TenantAdminMutationStatus.NotFound);

        // ⚠️ SOMENTE o nome de exibição. O Slug é imutável: nem sequer é parâmetro do comando.
        tenant.Name = name;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Tenant {TenantId} renomeado (nome de exibição).", tenant.Id);
        return TenantAdminMutationResult.Ok(Summarize(tenant));
    }

    public async Task<TenantAdminMutationResult> SetTenantStatusAsync(
        SetTenantStatusCommand command, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == command.TenantId, ct);
        if (tenant is null)
            return TenantAdminMutationResult.Rejected(TenantAdminMutationStatus.NotFound);

        return command.Suspend
            ? await SuspendAsync(tenant, command.ActorAccountId, ct)
            : await ReactivateAsync(tenant, ct);
    }

    /// <summary>
    /// Suspende o tenant SE isso não trancar o próprio ator para fora da plataforma. A correção SOB
    /// CONCORRÊNCIA vem de uma trava de linha no PostgreSQL sobre TODAS as linhas de acesso ATIVO do ator:
    /// duas suspensões concorrentes dos dois últimos ambientes do ator serializam, fechando o write-skew que
    /// sob READ COMMITTED deixaria as duas passarem e o trancaria para fora. No SQLite a cláusula é omitida
    /// (as escritas já são serializadas); a garantia real é validada em PostgreSQL descartável.
    /// </summary>
    private async Task<TenantAdminMutationResult> SuspendAsync(Tenant tenant, Guid actorAccountId, CancellationToken ct)
    {
        // Idempotente: já suspenso não muda nada e não pode "trancar" ninguém (já estava fora de uso).
        if (tenant.Status == TenantStatus.Suspended)
            return TenantAdminMutationResult.Ok(Summarize(tenant));

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockActorActiveMembershipsAsync(actorAccountId, ct);

        // O ator ainda terá ao menos UM ambiente ativo (não suspenso) DIFERENTE deste após a suspensão? Se
        // não, a plataforma ficaria sem forma administrativa válida de recuperação por ele — barra fechado.
        var otherActiveEnvironments = await (
            from u in _db.Users.IgnoreQueryFilters()
            join t in _db.Tenants on u.TenantId equals t.Id
            where u.IdentityAccountId == actorAccountId && u.IsActive
                  && u.TenantId != tenant.Id && t.Status != TenantStatus.Suspended
            select u.Id).CountAsync(ct);

        if (otherActiveEnvironments == 0)
        {
            await tx.RollbackAsync(ct);
            return TenantAdminMutationResult.Rejected(
                TenantAdminMutationStatus.SelfLockoutForbidden,
                "Você não pode suspender o último ambiente ativo em que tem acesso — perderia a forma de " +
                "reentrar e reverter. Garanta outro ambiente ativo antes.");
        }

        tenant.Status = TenantStatus.Suspended;
        await _db.SaveChangesAsync(ct);

        // Revoga os refresh tokens ATIVOS do ambiente (ExecuteUpdate — atômico, fora do change tracker,
        // participante da transação). IgnoreQueryFilters porque o ambiente suspenso pode NÃO ser o tenant
        // ambiente do ator. Efeito honesto: novas autenticações e RENOVAÇÕES ficam bloqueadas na porta de
        // entrada (login/seletor/troca já barram suspenso); um access token JÁ EMITIDO segue válido até vencer
        // (teto curto de JwtOptions.AccessTokenMinutes) — não há revogação retroativa de access token aqui.
        var revokedAt = DateTimeOffset.UtcNow;
        await _db.UserRefreshTokens.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenant.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, _ => revokedAt), ct);

        await tx.CommitAsync(ct);

        _log.LogInformation(
            "Tenant {TenantId} SUSPENSO por {ActorAccountId}; refresh tokens do ambiente revogados " +
            "(novas autenticações e renovações bloqueadas; access tokens já emitidos expiram no TTL normal).",
            tenant.Id, actorAccountId);
        return TenantAdminMutationResult.Ok(Summarize(tenant));
    }

    /// <summary>Reativa um tenant suspenso (→ Active). Idempotente; NÃO restaura sessões antigas.</summary>
    private async Task<TenantAdminMutationResult> ReactivateAsync(Tenant tenant, CancellationToken ct)
    {
        if (tenant.Status == TenantStatus.Suspended)
        {
            tenant.Status = TenantStatus.Active;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Tenant {TenantId} REATIVADO (Active).", tenant.Id);
        }
        // Não suspenso (Onboarding/Active): reativar é no-op idempotente — não rebaixa Onboarding para Active
        // sem necessidade nem inventa transição.
        return TenantAdminMutationResult.Ok(Summarize(tenant));
    }

    /// <summary>
    /// Tranca as linhas de acesso ATIVO do ator até o fim da transação (só no PostgreSQL). Comando cru, como o
    /// <c>UserManagementService.LockActiveAdminsAsync</c> — <c>FOR UPDATE</c> não existe no SQLite, e o provedor
    /// é detectado pelo NOME da conexão para não acoplar o pacote do Npgsql aqui.
    /// </summary>
    private async Task LockActorActiveMembershipsAsync(Guid actorAccountId, CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        if (!conn.GetType().Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            return;

        await _db.Database.ExecuteSqlRawAsync(
            "SELECT \"Id\" FROM \"Users\" WHERE \"IdentityAccountId\" = {0} AND \"IsActive\" = TRUE FOR UPDATE",
            new object[] { actorAccountId }, ct);
    }

    private static TenantAdminSummary Summarize(Tenant t) =>
        new(t.Id, t.Name, t.Slug, t.Status, t.CreatedAt, t.UpdatedAt);
}
