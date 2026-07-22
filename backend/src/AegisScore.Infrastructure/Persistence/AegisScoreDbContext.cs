using System.Text.Json;
using System.Text.Json.Serialization;
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
    // Motor GLOBAL de avaliação: regras técnicas por subcategoria (extraídas do 800-53 5.2.0). Reference
    // data, sem tenant — como o resto do catálogo NIST.
    public DbSet<AegisAssessmentRule> AssessmentRules => Set<AegisAssessmentRule>();

    // Tenancy
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<BusinessUnit> BusinessUnits => Set<BusinessUnit>();
    public DbSet<BusinessProcess> Processes => Set<BusinessProcess>();
    public DbSet<Asset> Assets => Set<Asset>();

    // Auth / Identity
    // A pessoa (global, sem query filter) e o membership por tenant (isolado, ITenantOwned).
    public DbSet<IdentityAccount> IdentityAccounts => Set<IdentityAccount>();
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
    // Aegis Score — motor consultivo: recomendações de remediação (advisories) por controle NIST
    public DbSet<RemediationAdvisory> RemediationAdvisories => Set<RemediationAdvisory>();

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

    // Identify (ID.RA) — Raio de Explosão: topologia, ameaças estruturadas e snapshots do raio
    public DbSet<AssetDependency> AssetDependencies => Set<AssetDependency>();
    public DbSet<Threat> Threats => Set<Threat>();
    public DbSet<AssetThreatExposure> AssetThreatExposures => Set<AssetThreatExposure>();
    public DbSet<BlastRadiusAssessment> BlastRadiusAssessments => Set<BlastRadiusAssessment>();
    public DbSet<BlastRadiusImpactNode> BlastRadiusImpactNodes => Set<BlastRadiusImpactNode>();

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

        // Lacunas de evidência do ledger (TenantControlState). MissingRequirement é record: SequenceEqual
        // e GetHashCode já usam igualdade ESTRUTURAL, então o change tracker detecta a edição de um item
        // sem que o comparer precise saber dos campos. A cópia é rasa DE PROPÓSITO — o record é imutável,
        // então clonar a lista basta para o snapshot do tracker.
        var missingRequirements = JsonbEnumAwareConverter<List<MissingRequirement>>();
        var missingRequirementsCmp = new ValueComparer<List<MissingRequirement>>(
            (x, y) => (x ?? new()).SequenceEqual(y ?? new()),
            v => v == null ? 0 : v.Aggregate(0, (h, m) => HashCode.Combine(h, m.GetHashCode())),
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

        // [AEGIS-AUD-052] Idempotência do catálogo vira INVARIANTE DE BANCO (mesmo idioma do dedupe de
        // GovernanceDocument.Sha256 e da chave natural de ConnectorConfig). O FrameworkSeeder decidia
        // "já semeado?" por um AnyAsync(Name) — read-then-write. Duas execuções concorrentes passavam
        // juntas pelo guard e inseriam DOIS catálogos completos: os índices únicos de Functions,
        // Categories e Subcategories são compostos com o Id do PAI, então uma segunda FrameworkVersion
        // não violava nada. O estrago não era só duplicar linhas — com códigos de subcategoria
        // repetidos, o ToDictionaryAsync(s => s.Code) do seed de regras passa a lançar, e o boot falha
        // para sempre, em toda réplica. Aqui o banco recusa fisicamente o segundo catálogo.
        b.Entity<FrameworkVersion>().HasIndex(x => x.Name).IsUnique();
        // Catálogo NIST — tamanho fixo dos códigos (cabe nos dados do seeder: "GV", "GV.OC",
        // "GV.OC-01") + unicidade no escopo do pai. O catálogo é versionado por FrameworkVersion,
        // então um índice único global só em Code colidiria entre versões do framework.
        b.Entity<NistFunction>().Property(x => x.Code).HasMaxLength(5).IsRequired();
        b.Entity<NistFunction>().HasIndex(x => new { x.FrameworkVersionId, x.Code }).IsUnique();
        b.Entity<NistCategory>().Property(x => x.Code).HasMaxLength(10).IsRequired();
        b.Entity<NistCategory>().HasIndex(x => new { x.FunctionId, x.Code }).IsUnique();
        b.Entity<NistSubcategory>().Property(x => x.Code).HasMaxLength(15).IsRequired();
        b.Entity<NistSubcategory>().HasIndex(x => new { x.CategoryId, x.Code }).IsUnique();

        // Aegis Assessment Rules — motor GLOBAL de avaliação (reference data, SEM query filter/stamp de
        // tenant). Uma regra por subcategoria: o índice único em SubcategoryCode reflete que as regras são
        // únicas por controle. Listas → jsonb (mesmo converter das demais), sem tabelas 1-N. FK RÍGIDA ao
        // catálogo por Id (não por Code — Code só é único no escopo (CategoryId, Code)), WithMany() sem
        // coleção inversa e Restrict, como no TenantControlState: apagar uma subcategoria não cascateia
        // sobre as regras. (Rules globais numa única FrameworkVersion; multi-versão seria migration futura.)
        b.Entity<AegisAssessmentRule>(e =>
        {
            e.Property(x => x.SubcategoryCode).HasMaxLength(15).IsRequired();
            e.HasIndex(x => x.SubcategoryCode).IsUnique();
            e.Property(x => x.EvaluationMetrics)
                .HasConversion(stringList, stringListCmp).HasColumnType("jsonb");
            e.Property(x => x.EvidenceRequirements)
                .HasConversion(stringList, stringListCmp).HasColumnType("jsonb");
            e.HasOne(x => x.Subcategory).WithMany()
                .HasForeignKey(x => x.SubcategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Risk>().HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.Entity<EvidenceSignal>().HasIndex(x => new { x.TenantId, x.SignalKey, x.CollectedAt });

        // Conector: UM registro por (tenant, provedor, capacidade) — a chave NATURAL da configuração.
        // O índice único torna o upsert do TenantManagementService.ConfigureConnectorAsync uma invariante
        // de BANCO, e não uma promessa do read-then-write: duas configurações simultâneas do mesmo
        // provedor+capacidade não podem mais gerar duas linhas. Duplicatas quebravam dois consumidores —
        // o IConnectorRegistry, que resolve UM adaptador por par, e o PolicyIngestionWorker, que projeta
        // (TenantId, Provider) e sincronizaria a MESMA integração N vezes por ciclo.
        //
        // ⚠️ Consequência de modelagem: um tenant do Aegis não pode ter duas contas do MESMO provedor na
        // mesma capacidade (ex.: dois M365 distintos sob um só cliente). Suportar isso exigiria uma chave
        // com discriminador de instância (o domínio do locatário externo), não este índice.
        b.Entity<ConnectorConfig>()
            .HasIndex(x => new { x.TenantId, x.Provider, x.Capability })
            .IsUnique();

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

            // ID.RA — matriz de impacto de negócio como Owned Value Object (colunas BusinessImpact_* na
            // própria tabela do Asset). É a ÚNICA config EF que a adição do VO ao Domain torna OBRIGATÓRIA:
            // sem ela o EF trata o tipo como entidade sem PK e invalida o modelo inteiro. As tabelas e
            // relações das demais entidades ID.RA (AssetDependency, Threat, exposições, raio) ficam para a
            // fase de infraestrutura (DbSets, índices, migration), conforme combinado.
            e.OwnsOne(a => a.BusinessImpact);
        });
        // Auth — a PESSOA (referência global): e-mail único no sistema inteiro. Sem query filter e sem
        // stamping: IdentityAccount NÃO é ITenantOwned de propósito, é o sujeito que ATRAVESSA tenants.
        // É a única entidade de identidade com essa natureza; o membership (User) segue isolado.
        b.Entity<IdentityAccount>(e =>
        {
            e.Property(a => a.Email).HasMaxLength(256).IsRequired();
            e.Property(a => a.PasswordHash).IsRequired();
            e.HasIndex(a => a.Email).IsUnique();   // login único GLOBAL (era por tenant)
        });

        // Auth — o MEMBERSHIP: um acesso por (tenant, pessoa). O índice único mudou de
        // (TenantId, Email) para (TenantId, IdentityAccountId): o e-mail saiu da tabela, e é a FK que
        // impede duas linhas de acesso da mesma pessoa ao mesmo cliente.
        b.Entity<User>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(200);
            e.HasIndex(u => new { u.TenantId, u.IdentityAccountId }).IsUnique();
            // Restrict: apagar a pessoa não cascateia sobre os acessos (e o histórico deles). A remoção
            // de um membership é ato explícito, como no resto do modelo.
            e.HasOne(u => u.Account).WithMany(a => a.Memberships)
                .HasForeignKey(u => u.IdentityAccountId)
                .OnDelete(DeleteBehavior.Restrict);
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

            // Lacunas de evidência tipadas → jsonb, mesmo idioma das listas do catálogo NIST (converter +
            // comparer), e NÃO o idioma string-blob de ChecksJson/IntelligenceJson: esta lista é
            // percorrida e agregada por Type, não repassada opaca à UI. Converter enum-aware para que o
            // JSON grave "Documentation" e não 1. NOT NULL com default de lista VAZIA — "sem lacuna
            // registrada" é [], nunca NULL; assim nenhum consumidor precisa de checagem de nulo.
            //
            // ⚠️ HasDefaultValue (tipado, atravessa o ValueConverter → literal '[]') e NÃO
            // HasDefaultValueSql("'[]'::jsonb"): o cast ::jsonb é sintaxe exclusiva do PostgreSQL e
            // quebraria o EnsureCreated dos testes, que rodam sobre SQLite. O literal serve aos dois.
            e.Property(x => x.MissingRequirements)
                .HasConversion(missingRequirements, missingRequirementsCmp)
                .HasColumnType("jsonb")
                .HasDefaultValue(new List<MissingRequirement>())
                .IsRequired();
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

        // Aegis Score — advisories (motor consultivo). Índice tenant-leading por código de controle: o
        // caso de uso natural é listar as recomendações de UMA subcategoria do tenant. NÃO é único —
        // podem coexistir várias versões/revisões de advisory para o mesmo controle (histórico consultivo).
        b.Entity<RemediationAdvisory>(e =>
        {
            e.Property(x => x.SubcategoryCode).HasMaxLength(15).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.SubcategoryCode });
        });

        // ============================================================
        //  Identify (ID.RA) — Raio de Explosão
        // ============================================================

        // Grafo de topologia: aresta direcionada Source→Target com payload. DUAS FKs para Asset — AMBAS
        // Restrict, senão o PostgreSQL rejeita "multiple cascade paths" ao deletar um Asset. Índice único
        // tenant-leading (idempotência por par + tipo); check barra o auto-laço (A depende de A).
        b.Entity<AssetDependency>(e =>
        {
            e.HasOne(d => d.SourceAsset).WithMany()
                .HasForeignKey(d => d.SourceAssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.TargetAsset).WithMany()
                .HasForeignKey(d => d.TargetAssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(d => new { d.TenantId, d.SourceAssetId, d.TargetAssetId, d.Type }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint(
                "CK_AssetDependency_NoSelfLoop", "\"SourceAssetId\" <> \"TargetAssetId\""));
        });

        // Catálogo de ameaças (reference data, idioma do IcrWeightProfile): TenantId nulo = global.
        // Unicidade composta (TenantId, Code, Source). ⚠️ No PostgreSQL NULLs são distintos — dois threats
        // GLOBAIS de mesmo Code/Source ainda passariam; a ingestão do catálogo público dedupe na origem.
        b.Entity<Threat>(e =>
        {
            e.Property(t => t.Code).HasMaxLength(64).IsRequired();
            e.HasIndex(t => new { t.TenantId, t.Code, t.Source }).IsUnique();
        });

        // Exposição ativo↔ameaça: uma linha por par no tenant. FKs Restrict (a exposição é registro de
        // auditoria — apagar ativo/ameaça não a cascateia).
        b.Entity<AssetThreatExposure>(e =>
        {
            e.HasOne(x => x.Asset).WithMany()
                .HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Threat).WithMany()
                .HasForeignKey(x => x.ThreatId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.AssetId, x.ThreatId }).IsUnique();
        });

        // Snapshot do raio + nós materializados (1:N). O nó NÃO existe sem o assessment → Cascade PERMITIDO
        // aqui. As FKs para Asset (root e nó impactado) e para o Threat de cenário são Restrict: o snapshot é
        // histórico — apagar um ativo/ameaça não apaga avaliações passadas nem cascateia por múltiplos caminhos.
        b.Entity<BlastRadiusAssessment>(e =>
        {
            e.HasOne(a => a.RootAsset).WithMany()
                .HasForeignKey(a => a.RootAssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.ScenarioThreat).WithMany()
                .HasForeignKey(a => a.ScenarioThreatId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.ImpactedNodes).WithOne(n => n.Assessment)
                .HasForeignKey(n => n.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.TenantId, a.RootAssetId });
        });
        b.Entity<BlastRadiusImpactNode>(e =>
        {
            e.HasOne(n => n.ImpactedAsset).WithMany()
                .HasForeignKey(n => n.ImpactedAssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(n => new { n.TenantId, n.AssessmentId });
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
        b.Entity<RemediationAdvisory>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        // Identify (ID.RA) — grafo, exposições e snapshots são ITenantOwned (Threat é reference data, sem filtro).
        b.Entity<AssetDependency>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<AssetThreatExposure>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<BlastRadiusAssessment>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<BlastRadiusImpactNode>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
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

    /// <summary>
    /// Opções do jsonb para payloads que carregam ENUM. O enum vai como TEXTO ("Documentation"), nunca
    /// como ordinal: o ledger de conformidade é auditado direto no SQL — <c>{"type": 1}</c> é ilegível
    /// para quem consulta — e, pior, reordenar o enum reinterpretaria em silêncio todo o histórico
    /// gravado. Um dado de auditoria não pode mudar de significado por causa de um refactor.
    /// </summary>
    private static readonly JsonSerializerOptions JsonbWithEnumNames = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static ValueConverter<T, string> JsonbEnumAwareConverter<T>() => new(
        v => JsonSerializer.Serialize(v, JsonbWithEnumNames),
        v => JsonSerializer.Deserialize<T>(v, JsonbWithEnumNames)!);
}
