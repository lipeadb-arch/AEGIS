using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Auth;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.DbMigrator;

/// <summary>
/// [Homologação] Cria de forma SEGURA o primeiro administrador de uma instalação recém-migrada, sem
/// nenhum endpoint anônimo. Roda como etapa final do migrator (depois de migrations+seed+verificação),
/// dentro do MESMO advisory lock — então nunca concorre consigo mesmo.
///
/// Invariantes preservadas (as mesmas do fluxo normal): e-mail global normalizado e único; hash PBKDF2 do
/// <see cref="Pbkdf2PasswordHasher"/>; <see cref="IdentityAccount"/> como pessoa GLOBAL; <see cref="User"/>
/// como membership <see cref="ITenantOwned"/> carimbado pelo <see cref="AegisScoreDbContext"/> (o TenantId
/// nunca é forjado — é validado contra o contexto ambiente). Não cria endpoint, não altera o fluxo de
/// autenticação e não toca senha de mais ninguém.
///
/// Semântica FAIL-CLOSED e IDEMPOTENTE — é o PRIMEIRO administrador, nunca "mais um":
///  - banco SEM nenhuma identidade → cria IdentityAccount (PlatformAdmin) + tenant (localiza ou cria) +
///    membership (TenantAdmin) numa ÚNICA transação (sem estado parcial);
///  - conta configurada já existente E já consistente (PlatformAdmin + TenantAdmin ativo neste tenant) → no-op;
///  - conta configurada existente em QUALQUER outro estado → RECUSA sem mutar (nunca promove em silêncio);
///  - conta configurada AUSENTE, mas já existe OUTRA identidade qualquer → RECUSA: a instalação já foi
///    inicializada e este não é mais o primeiro administrador (provisionamento adicional é pelo fluxo normal).
/// </summary>
public static class AdminBootstrapper
{
    public static async Task<int> RunAsync(
        DbContextOptions<AegisScoreDbContext> options,
        BootstrapOptions bootstrap,
        ILogger log,
        CancellationToken ct = default)
    {
        if (bootstrap.Error is not null)
        {
            log.LogError("Bootstrap do primeiro administrador ABORTADO: {Error}", bootstrap.Error);
            return MigratorExitCode.BootstrapFailure;
        }

        var email = bootstrap.AdminEmail;
        var slug = bootstrap.TenantSlug;

        // Fase de leitura: localiza o tenant pelo slug (Tenant NÃO é ITenantOwned — leitura global) para
        // decidir o TenantId ANTES de abrir o contexto de escrita ligado a ele. Um contexto ligado a null
        // não conseguiria carimbar o membership (stamping fail-closed exige tenant ambiente resolvido).
        Guid tenantId;
        bool tenantExists;
        await using (var probe = new AegisScoreDbContext(options, new SystemTenantContext(null)))
        {
            var existing = await probe.Tenants.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Slug == slug, ct);
            tenantExists = existing is not null;
            tenantId = existing?.Id ?? Guid.NewGuid();
        }

        // Contexto de escrita ligado ao tenant alvo: o membership (ITenantOwned) será carimbado com ESTE
        // tenant, e a leitura de membership abaixo já vem filtrada por ele.
        await using var db = new AegisScoreDbContext(options, new SystemTenantContext(tenantId));

        // IdentityAccount é global (sem query filter): esta leitura enxerga o sistema inteiro.
        var account = await db.IdentityAccounts.FirstOrDefaultAsync(a => a.Email == email, ct);

        if (account is not null)
        {
            var membership = await db.Users.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.IdentityAccountId == account.Id, ct);

            var alreadyAdmin = account.PlatformRole == PlatformRole.PlatformAdmin
                && membership is { IsActive: true, Role: TenantRole.TenantAdmin };

            if (alreadyAdmin)
            {
                log.LogInformation(
                    "Bootstrap idempotente: administrador {AccountId} já existe e está consistente no tenant " +
                    "{Slug} ({TenantId}) — nada a fazer.", account.Id, slug, tenantId);
                return MigratorExitCode.Success;
            }

            // Estado parcial ou conflitante. NÃO mutamos: promover em silêncio uma conta que já estava no
            // banco (talvez de outro fluxo, talvez de um bootstrap anterior interrompido) é exatamente o que
            // esta rotina deve recusar. O operador resolve manualmente ou desliga o bootstrap.
            log.LogError(
                "Bootstrap ABORTADO (fail-closed): já existe uma conta com este e-mail em estado que NÃO " +
                "corresponde ao administrador pedido (PlatformAdmin + TenantAdmin ativo no tenant {Slug}). " +
                "Recusando para não promover silenciosamente uma conta preexistente. Ajuste manualmente ou " +
                "desative o bootstrap (Bootstrap__Enabled=false).", slug);
            return MigratorExitCode.BootstrapFailure;
        }

        // "Primeiro administrador" no sentido estrito: só criamos se o banco NÃO contiver NENHUMA outra
        // identidade. Se a conta configurada não existe mas JÁ existe outra IdentityAccount, esta instalação
        // já foi inicializada por outro caminho — criar um segundo PlatformAdmin por bootstrap abriria uma
        // porta de provisionamento fora do self-service. Fail-closed, sem mutar. (Leitura global: IdentityAccount
        // não é ITenantOwned, então enxerga o sistema inteiro independentemente do tenant do contexto.)
        if (await db.IdentityAccounts.AnyAsync(ct))
        {
            log.LogError(
                "Bootstrap ABORTADO (fail-closed): a conta configurada não existe, mas o banco já contém outra " +
                "identidade — a instalação já foi inicializada e este não é mais o PRIMEIRO administrador. " +
                "Provisione acessos adicionais pelo fluxo normal (PlatformAdmin → identidade → membership) ou " +
                "desative o bootstrap (Bootstrap__Enabled=false).");
            return MigratorExitCode.BootstrapFailure;
        }

        // Conta inexistente e banco sem outras identidades: criação limpa numa única transação (tudo ou nada).
        var hasher = new Pbkdf2PasswordHasher();

        if (!tenantExists)
        {
            db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = bootstrap.TenantName,
                Slug = slug,
                Status = TenantStatus.Active,   // tenant de homologação já utilizável pelo admin
            });
        }

        var newAccount = new IdentityAccount
        {
            Email = email,
            PasswordHash = hasher.Hash(bootstrap.AdminPassword),   // PBKDF2 210k — o mesmo do fluxo normal
            PlatformRole = PlatformRole.PlatformAdmin,
        };
        db.IdentityAccounts.Add(newAccount);

        db.Users.Add(new User
        {
            TenantId = tenantId,   // carimbo validado contra o contexto ambiente (== tenantId): sem divergência
            IdentityAccountId = newAccount.Id,
            DisplayName = bootstrap.AdminDisplayName,
            Role = TenantRole.TenantAdmin,
            IsActive = true,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Corrida perdida em índice único (Email global / Tenant.Slug) — improvável sob o advisory lock,
            // mas fail-closed mesmo assim. A mensagem NÃO inclui e-mail, senha nem connection string.
            log.LogError(ex,
                "Bootstrap ABORTADO: conflito de unicidade ao gravar o primeiro administrador (e-mail ou slug " +
                "já em uso). Nenhuma promoção silenciosa foi feita.");
            return MigratorExitCode.BootstrapFailure;
        }

        log.LogInformation(
            "Bootstrap concluído: administrador {AccountId} (PlatformAdmin) criado com membership TenantAdmin " +
            "no tenant {Slug} ({TenantId}, {TenantAction}). Desative o bootstrap após o primeiro deploy " +
            "(Bootstrap__Enabled=false).",
            newAccount.Id, slug, tenantId, tenantExists ? "existente" : "criado");

        return MigratorExitCode.Success;
    }
}
