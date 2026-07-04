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

    // Assessments
    public DbSet<Assessment> Assessments => Set<Assessment>();
    public DbSet<AssessmentScope> Scopes => Set<AssessmentScope>();
    public DbSet<AssessmentTask> Tasks => Set<AssessmentTask>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Answer> Answers => Set<Answer>();
    public DbSet<Evidence> Evidence => Set<Evidence>();
    public DbSet<SubcategoryEvaluation> Evaluations => Set<SubcategoryEvaluation>();

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

        // Computed properties — never persisted.
        b.Entity<SubcategoryEvaluation>().Ignore(x => x.Gap);
        b.Entity<ActionPlan>().Ignore(x => x.IsOverdue);

        // Useful uniqueness / lookups.
        b.Entity<Tenant>().HasIndex(x => x.Slug).IsUnique();
        b.Entity<NistFunction>().HasIndex(x => new { x.FrameworkVersionId, x.Code }).IsUnique();
        b.Entity<NistCategory>().HasIndex(x => x.Code);
        b.Entity<NistSubcategory>().HasIndex(x => x.Code);
        b.Entity<Risk>().HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.Entity<EvidenceSignal>().HasIndex(x => new { x.TenantId, x.SignalKey, x.CollectedAt });

        // Tenant-leading indexes for operational entities that don't get one from an FK
        // convention, so the multi-tenant query filter uses an index instead of a full scan.
        b.Entity<Asset>().HasIndex(x => x.TenantId);
        b.Entity<Assessment>().HasIndex(x => x.TenantId);
        b.Entity<AssessmentScope>().HasIndex(x => new { x.TenantId, x.AssessmentId });
        b.Entity<Evidence>().HasIndex(x => x.TenantId);
        b.Entity<RiskAppetite>().HasIndex(x => x.TenantId);
        b.Entity<IcrScore>().HasIndex(x => x.TenantId);
        // Children now carry their own TenantId — index it alongside the parent FK.
        b.Entity<RiskEvaluation>().HasIndex(x => new { x.TenantId, x.RiskId });
        b.Entity<ActionPlan>().HasIndex(x => new { x.TenantId, x.RiskId });

        // Multi-tenant isolation: every operational entity is scoped to the ambient tenant.
        // Fail-CLOSED: when no tenant is resolved (missing/invalid X-Tenant) the filter yields
        // no rows, instead of leaking every tenant's data. Seed/maintenance code that must span
        // tenants uses .IgnoreQueryFilters() explicitly.
        b.Entity<BusinessUnit>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<BusinessProcess>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<Asset>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
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
