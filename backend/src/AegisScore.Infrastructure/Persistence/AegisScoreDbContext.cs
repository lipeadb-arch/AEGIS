using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Persistence;

/// <summary>
/// The Aegis Score database context (PostgreSQL via Npgsql).
/// - List-typed columns are stored as <c>jsonb</c>.
/// - Computed domain properties (Gap, IsOverdue) are not persisted.
/// - Operational (ITenantOwned) entities carry a global query filter for tenant isolation
///   AND are stamped with the ambient TenantId on insert (fail-closed).
/// Reference/framework data (NIST catalog) is shared across tenants and is not filtered.
/// </summary>
public class AegisScoreDbContext : DbContext
{
    private readonly ITenantContext _tenant;

    public AegisScoreDbContext(DbContextOptions<AegisScoreDbContext> options, ITenantContext tenant)
        : base(options) => _tenant = tenant;

    // Framework (shared reference data)
    public DbSet<FrameworkVersion> FrameworkVersions => Set<FrameworkVersion>();
    public DbSet<NistFunction> Functions => Set<NistFunction>();
    public DbSet<NistCategory> Categories => Set<NistCategory>();
    public DbSet<NistSubcategory> Subcategories => Set<NistSubcategory>();
    public DbSet<MaturityLevel> MaturityLevels => Set<MaturityLevel>();
    public DbSet<SignalMapping> SignalMappings => Set<SignalMapping>();

    // Tenancy
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<BusinessUnit> BusinessUnits => Set<BusinessUnit>();
    public DbSet<BusinessProcess> Processes => Set<BusinessProcess>();
    public DbSet<Asset> Assets => Set<Asset>();

    // Auth / Identity
    public DbSet<User> Users => Set<User>();
    public DbSet<UserRefreshToken> UserRefreshTokens => Set<UserRefreshToken>();

    // Assessments
    public DbSet<Assessment> Assessments => Set<Assessment>();
    public DbSet<AssessmentScope> Scopes => Set<AssessmentScope>();
    public DbSet<AssessmentTask> Tasks => Set<AssessmentTask>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Answer> Answers => Set<Answer>();
    public DbSet<Evidence> Evidence => Set<Evidence>();
    public DbSet<SubcategoryEvaluation> Evaluations => Set<SubcategoryEvaluation>();

    // Aegis Score — estado de conformidade por tenant (desacoplado de campanha de assessment)
    public DbSet<TenantControlState> TenantControlStates => Set<TenantControlState>();
    // Aegis Score — inteligência temporal: foto agregada diária p/ o gráfico de tendência de postura
    public DbSet<TenantScoreSnapshot> TenantScoreSnapshots => Set<TenantScoreSnapshot>();

    // Connectors
    public DbSet<ConnectorConfig> Connectors => Set<ConnectorConfig>();
    public DbSet<EvidenceSignal> Signals => Set<EvidenceSignal>();

    // Risks & scoring
    public DbSet<Risk> Risks => Set<Risk>();
    public DbSet<RiskEvaluation> RiskEvaluations => Set<RiskEvaluation>();
    public DbSet<ActionPlan> ActionPlans => Set<ActionPlan>();
    public DbSet<RiskAppetite> RiskAppetites => Set<RiskAppetite>();
    public DbSet<MaturitySnapshot> MaturitySnapshots => Set<MaturitySnapshot>();
    public DbSet<IcrScore> IcrScores => Set<IcrScore>();
    public DbSet<IcrWeightProfile> IcrWeightProfiles => Set<IcrWeightProfile>();

    // Govern (GV) — Document Hub + Auditor Virtual (GRC)
    public DbSet<GovernanceDocument> GovernanceDocuments => Set<GovernanceDocument>();
    public DbSet<DocumentControlMapping> DocumentControlMappings => Set<DocumentControlMapping>();
    public DbSet<SubcategoryCoverage> SubcategoryCoverages => Set<SubcategoryCoverage>();
    public DbSet<GrcInterviewSession> GrcInterviewSessions => Set<GrcInterviewSession>();
    public DbSet<GrcInterviewMessage> GrcInterviewMessages => Set<GrcInterviewMessage>();
    public DbSet<IdentifiedRisk> IdentifiedRisks => Set<IdentifiedRisk>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        var stringList = JsonbConverter<List<string>>();
        var stringListCmp = new ValueComparer<List<string>>(
            (x, y) => (x ?? new()).SequenceEqual(y ?? new()),
            v => v == null ? 0 : v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
            v => v == null ? new() : v.ToList());

        var guidList = JsonbConverter<List<Guid>>();
        var guidListCmp = new ValueComparer<List<Guid>>(
            (x, y) => (x ?? new()).SequenceEqual(y ?? new()),
            v => v == null ? 0 : v.Aggregate(0, (h, g) => HashCode.Combine(h, g.GetHashCode())),
            v => v == null ? new() : v.ToList());

        b.Entity<NistSubcategory>().Property(x => x.InformativeReferences)
            .HasConversion(stringList, stringListCmp).HasColumnType("jsonb");
        b.Entity<EvidenceSignal>().Property(x => x.MappedSubcategoryCodes)
            .HasConversion(stringList, stringListCmp).HasColumnType("jsonb");
        b.Entity<SignalMapping>().Property(x => x.SubcategoryCodes)
            .HasConversion(stringList, stringListCmp).HasColumnType("jsonb");
        b.Entity<SubcategoryEvaluation>().Property(x => x.EvidenceRefs)
            .HasConversion(guidList, guidListCmp).HasColumnType("jsonb");
        b.Entity<GrcInterviewSession>().Property(x => x.TargetSubcategoryCodes)
            .HasConversion(stringList, stringListCmp).HasColumnType("jsonb");

        // Computed properties — never persisted.
        b.Entity<SubcategoryEvaluation>().Ignore(x => x.Gap);
        b.Entity<ActionPlan>().Ignore(x => x.IsOverdue);

        // Useful uniqueness / lookups.
        b.Entity<Tenant>().HasIndex(x => x.Slug).IsUnique();
        // Catálogo NIST — tamanho fixo dos códigos (cabe nos dados do seeder: "GV", "GV.OC",
        // "GV.OC-01") + unicidade no escopo do pai. O catálogo é versionado por FrameworkVersion,
        // então um índice único global só em Code colidiria entre versões do framework.
        b.Entity<NistFunction>().Property(x => x.Code).HasMaxLength(5).IsRequired();
        b.Entity<NistFunction>().HasIndex(x => new { x.FrameworkVersionId, x.Code }).IsUnique();
        b.Entity<NistCategory>().Property(x => x.Code).HasMaxLength(10).IsRequired();
        b.Entity<NistCategory>().HasIndex(x => new { x.FunctionId, x.Code }).IsUnique();
        b.Entity<NistSubcategory>().Property(x => x.Code).HasMaxLength(15).IsRequired();
        b.Entity<NistSubcategory>().HasIndex(x => new { x.CategoryId, x.Code }).IsUnique();
        b.Entity<Risk>().HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.Entity<EvidenceSignal>().HasIndex(x => new { x.TenantId, x.SignalKey, x.CollectedAt });

        // Tenant-leading indexes for operational entities that don't get one from an FK
        // convention, so the multi-tenant query filter uses an index instead of a full scan.
        b.Entity<Asset>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(200).IsRequired();
            e.Property(a => a.SubType).HasMaxLength(100);
            e.Property(a => a.ExternalRef).HasMaxLength(200);
            // Category e RiskLevel são persistidos como integer (default Npgsql) — sem config extra.

            // Grid tática: índices tenant-leading para os filtros NIST combinados.
            e.HasIndex(a => new { a.TenantId, a.Category });
            e.HasIndex(a => new { a.TenantId, a.RiskLevel });
            e.HasIndex(a => new { a.TenantId, a.Criticality });

            // Upsert idempotente vindo de conectores (só ativos com ref externa).
            e.HasIndex(a => new { a.TenantId, a.ExternalRef })
                .IsUnique()
                .HasFilter("\"ExternalRef\" IS NOT NULL");
        });
        // Auth: usuário (e-mail único por tenant) e refresh tokens (RTR).
        b.Entity<User>(e =>
        {
            e.Property(u => u.Email).HasMaxLength(256).IsRequired();
            e.Property(u => u.DisplayName).HasMaxLength(200);
            e.Property(u => u.PasswordHash).IsRequired();
            e.HasIndex(u => new { u.TenantId, u.Email }).IsUnique();   // login único no escopo do tenant
        });
        b.Entity<UserRefreshToken>(e =>
        {
            e.Property(t => t.Token).HasMaxLength(512).IsRequired();
            e.Property(t => t.ReplacedByToken).HasMaxLength(512);
            e.HasIndex(t => new { t.TenantId, t.Token }).IsUnique();   // lookup do refresh, tenant-leading
            e.HasIndex(t => new { t.TenantId, t.UserId });             // revogação em massa por usuário (breach)
            e.HasOne(t => t.User).WithMany(u => u.RefreshTokens)
                .HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);

            // Estado derivado — nunca persistido.
            e.Ignore(t => t.IsExpired);
            e.Ignore(t => t.IsRevoked);
            e.Ignore(t => t.IsActive);
        });

        b.Entity<Assessment>().HasIndex(x => x.TenantId);
        b.Entity<AssessmentScope>().HasIndex(x => new { x.TenantId, x.AssessmentId });
        b.Entity<Evidence>().HasIndex(x => x.TenantId);
        b.Entity<RiskAppetite>().HasIndex(x => x.TenantId);
        b.Entity<IcrScore>().HasIndex(x => x.TenantId);
        b.Entity<GovernanceDocument>().HasIndex(x => x.TenantId);
        // Dedupe por hash NO NÍVEL DO BANCO: o índice ÚNICO (TenantId, Sha256) REJEITA fisicamente um
        // segundo documento com o mesmo conteúdo no tenant. É ele que torna idempotente a corrida
        // read-then-write dos dois caminhos de ingestão (Upload e PolicyIngestionWorker.SyncTenantAsync),
        // que antes dependia de um SemaphoreSlim como paliativo. Índice PARCIAL (mesmo padrão de
        // Asset.ExternalRef): Sha256 é nullable — a integração registra o documento antes de anexar o
        // binário — então a unicidade só incide quando há hash; vários registros sem hash convivem.
        b.Entity<GovernanceDocument>().HasIndex(x => new { x.TenantId, x.Sha256 })
            .IsUnique()
            .HasFilter("\"Sha256\" IS NOT NULL");
        b.Entity<DocumentControlMapping>().HasIndex(x => new { x.TenantId, x.GovernanceDocumentId });
        b.Entity<DocumentControlMapping>().HasIndex(x => new { x.TenantId, x.SubcategoryCode });
        b.Entity<SubcategoryCoverage>().HasIndex(x => new { x.TenantId, x.SubcategoryCode }).IsUnique();
        b.Entity<GrcInterviewSession>().HasIndex(x => x.TenantId);
        b.Entity<GrcInterviewMessage>().HasIndex(x => new { x.TenantId, x.SessionId });
        b.Entity<IdentifiedRisk>().HasIndex(x => new { x.TenantId, x.SubcategoryCode });
        // Children now carry their own TenantId — index it alongside the parent FK.
        b.Entity<RiskEvaluation>().HasIndex(x => new { x.TenantId, x.RiskId });
        b.Entity<ActionPlan>().HasIndex(x => new { x.TenantId, x.RiskId });

        // Aegis Score — um ÚNICO estado por tenant × subcategoria (o índice único garante que o
        // "Group By de soma" nunca conte linhas duplicadas). FK para o catálogo global SEM coleção
        // inversa (o catálogo imutável não referencia dados de tenant); Restrict impede que um
        // delete no catálogo cascateie sobre o estado do tenant.
        b.Entity<TenantControlState>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.SubcategoryId }).IsUnique();
            e.HasOne(x => x.Subcategory).WithMany()
                .HasForeignKey(x => x.SubcategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Aegis Score — série temporal (Snapshot Agregado Diário). O índice único composto
        // (TenantId, SnapshotDate) é tenant-leading e faz DUPLO papel: idempotência — o banco
        // REJEITA fisicamente duas fotos do mesmo tenant no mesmo dia — e performance da consulta
        // de tendência (seek por tenant + range ordenado por data). DateOnly → coluna `date` nativa
        // do Npgsql, sem ValueConverter.
        b.Entity<TenantScoreSnapshot>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.SnapshotDate }).IsUnique();
        });

        // Multi-tenant isolation: every operational entity is scoped to the ambient tenant.
        // Fail-CLOSED: when no tenant is resolved (missing/invalid X-Tenant) the filter yields
        // no rows, instead of leaking every tenant's data. Seed/maintenance code that must span
        // tenants uses .IgnoreQueryFilters() explicitly.
        b.Entity<BusinessUnit>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<BusinessProcess>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<Asset>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<User>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<UserRefreshToken>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<Assessment>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<AssessmentScope>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<Evidence>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<ConnectorConfig>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<EvidenceSignal>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<Risk>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<RiskAppetite>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<IcrScore>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        // Defense in depth: child entities no longer rely solely on the parent route.
        // They now filter on their own denormalized TenantId, independent of the Risk filter.
        b.Entity<RiskEvaluation>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<ActionPlan>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<GovernanceDocument>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<DocumentControlMapping>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<SubcategoryCoverage>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<GrcInterviewSession>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<GrcInterviewMessage>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<IdentifiedRisk>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<TenantControlState>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<TenantScoreSnapshot>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
    }

    /// <summary>Stamp tenant (fail-closed) + audit timestamps automatically on save.</summary>
    public override int SaveChanges()
    {
        StampTenant();
        StampAudit();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        StampTenant();
        StampAudit();
        return base.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Secure-by-design write stamping: every ITenantOwned entity being inserted receives the
    /// ambient TenantId. Fail-CLOSED — if no tenant is resolved, or a caller tried to smuggle a
    /// TenantId that disagrees with the context, we throw instead of persisting a cross-tenant row.
    /// </summary>
    private void StampTenant()
    {
        var added = ChangeTracker.Entries<ITenantOwned>()
            .Where(e => e.State == EntityState.Added)
            .ToList();
        if (added.Count == 0) return;

        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException(
                "Gravação de entidade multi-tenant sem tenant resolvido no contexto (fail-closed).");

        if (tenantId == Guid.Empty)
            throw new TenantSecurityException("TenantId do contexto é inválido (Guid.Empty).");

        foreach (var entry in added)
        {
            var supplied = entry.Entity.TenantId;

            // Never trust a client-supplied TenantId that diverges from the ambient tenant.
            if (supplied != Guid.Empty && supplied != tenantId)
                throw new TenantSecurityException(
                    $"TenantId da entidade '{entry.Entity.GetType().Name}' ({supplied}) " +
                    $"diverge do tenant do contexto ({tenantId}).");

            entry.Entity.TenantId = tenantId;
        }
    }

    private void StampAudit()
    {
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static ValueConverter<T, string> JsonbConverter<T>() => new(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => JsonSerializer.Deserialize<T>(v, (JsonSerializerOptions?)null)!);
}
