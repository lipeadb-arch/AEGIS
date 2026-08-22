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
    public DbSet<ReferenceDatasetProvenance> ReferenceDatasetProvenances => Set<ReferenceDatasetProvenance>();
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
    // [AEGIS-MVP-POSTURE-02] Exposições de configuração (postura) — tenant-owned, provider-neutral.
    public DbSet<PostureExposureFinding> PostureExposureFindings => Set<PostureExposureFinding>();

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
    // [AEGIS-AUD-050] Fila operacional durável de sincronização de políticas (substitui o canal em memória).
    public DbSet<PolicySyncRequest> PolicySyncRequests => Set<PolicySyncRequest>();

    // Identify (ID.RA) — Raio de Explosão: topologia, ameaças estruturadas e snapshots do raio
    public DbSet<AssetDependency> AssetDependencies => Set<AssetDependency>();
    public DbSet<Threat> Threats => Set<Threat>();
    public DbSet<AssetThreatExposure> AssetThreatExposures => Set<AssetThreatExposure>();
    // [AEGIS-MVP-VULN-01] Fundação multicloud: vínculo Asset↔fonte e observação por fonte da exposição consolidada.
    public DbSet<AssetSourceBinding> AssetSourceBindings => Set<AssetSourceBinding>();
    public DbSet<AssetThreatObservation> AssetThreatObservations => Set<AssetThreatObservation>();
    public DbSet<BlastRadiusAssessment> BlastRadiusAssessments => Set<BlastRadiusAssessment>();
    public DbSet<BlastRadiusImpactNode> BlastRadiusImpactNodes => Set<BlastRadiusImpactNode>();

    // AEGIS KNIGHT — assessment de postura de identidade/exposição (estrutura DEDICADA, desacoplada do
    // Assessment de processos e do ledger do AEGIS Score geral).
    public DbSet<KnightAssessmentRun> KnightAssessmentRuns => Set<KnightAssessmentRun>();
    public DbSet<KnightIndicatorResult> KnightIndicatorResults => Set<KnightIndicatorResult>();

    // [AEGIS-AUD-035/036/037] Fotografia AUDITÁVEL e IMUTÁVEL de postura (histórico compartilhado AEGIS
    // Score/NIST e KNIGHT). Append-only: sem update/delete (reforçado por gatilho no PostgreSQL — ver migration).
    public DbSet<PostureSnapshot> PostureSnapshots => Set<PostureSnapshot>();
    public DbSet<PostureSnapshotControl> PostureSnapshotControls => Set<PostureSnapshotControl>();
    public DbSet<PostureSnapshotIndicator> PostureSnapshotIndicators => Set<PostureSnapshotIndicator>();

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

        // [AEGIS-AUD-035] Referências de evidência de um controle CONGELADO na fotografia → jsonb. PostureEvidenceRef
        // é record (igualdade estrutural): SequenceEqual/GetHashCode bastam ao change tracker. Sem enum → converter
        // padrão (não o enum-aware). Cópia rasa: o record é imutável, clonar a lista basta para o snapshot do tracker.
        var evidenceRefs = JsonbConverter<List<PostureEvidenceRef>>();
        var evidenceRefsCmp = new ValueComparer<List<PostureEvidenceRef>>(
            (x, y) => (x ?? new()).SequenceEqual(y ?? new()),
            v => v == null ? 0 : v.Aggregate(0, (h, e) => HashCode.Combine(h, e.GetHashCode())),
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
        // AEGIS KNIGHT — listas de mapeamento por indicador → jsonb (mesmo idioma das listas do catálogo NIST).
        b.Entity<KnightIndicatorResult>().Property(x => x.NistCodes)
            .HasConversion(stringList, stringListCmp).HasColumnType("jsonb");
        b.Entity<KnightIndicatorResult>().Property(x => x.MitreTechniques)
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
        // [AEGIS-MVP-POSTURE-01] No MÁXIMO uma FrameworkVersion ATIVA — invariante de banco que torna todo
        // FirstOrDefault(IsActive) inequívoco (score, snapshot, signal mapper, seed operam sobre a MESMA
        // versão). Índice PARCIAL: só as ativas concorrem pela unicidade; as inativas convivem.
        b.Entity<FrameworkVersion>().HasIndex(x => x.IsActive)
            .IsUnique()
            .HasDatabaseName("UX_FrameworkVersion_SingleActive")
            .HasFilter("\"IsActive\"");
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
            // [AEGIS-MVP-POSTURE-01] Natureza da evidência TIPADA (persistida como int); default Telemetry
            // preenche as linhas legadas na migration — o seed reconcilia o valor correto por regra.
            e.Property(x => x.EvidenceType).HasConversion<int>();
            e.HasOne(x => x.Subcategory).WithMany()
                .HasForeignKey(x => x.SubcategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // [AEGIS-MVP-POSTURE-01] Proveniência auditável dos conjuntos de dados de referência. Reference data
        // GLOBAL (sem query filter/stamp). HISTÓRICO PRESERVADO: uma linha por (FrameworkVersion, Kind,
        // Revision) — o índice único impede colisão de revisão — e um índice PARCIAL garante EXATAMENTE uma
        // revisão vigente (IsCurrent) por conjunto. Um hash antigo nunca é apagado; a versão cascateia.
        b.Entity<ReferenceDatasetProvenance>(e =>
        {
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Classification).HasConversion<int>();
            e.Property(x => x.Identifier).HasMaxLength(100).IsRequired();
            e.Property(x => x.SchemaVersion).HasMaxLength(50).IsRequired();
            e.Property(x => x.Origin).HasMaxLength(300).IsRequired();
            e.Property(x => x.OfficialReference).HasMaxLength(100);
            e.Property(x => x.Release).HasMaxLength(100);
            e.Property(x => x.OfficialUrl).HasMaxLength(500);
            e.Property(x => x.ObtainedOn).HasMaxLength(50);
            e.Property(x => x.AppliesToCatalog).HasMaxLength(100);
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();   // SHA-256 hex
            e.Property(x => x.MethodologyVersion).HasMaxLength(50);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasIndex(x => new { x.FrameworkVersionId, x.Kind, x.Revision }).IsUnique();
            e.HasIndex(x => new { x.FrameworkVersionId, x.Kind })
                .IsUnique()
                .HasDatabaseName("UX_ReferenceDatasetProvenance_Current")
                .HasFilter("\"IsCurrent\"");
            e.HasOne<FrameworkVersion>().WithMany(f => f.Provenance)
                .HasForeignKey(x => x.FrameworkVersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Risk>().HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        b.Entity<EvidenceSignal>().HasIndex(x => new { x.TenantId, x.SignalKey, x.CollectedAt });

        // [AEGIS-AUD-020/041] Ingestão genérica: campos aditivos + IDEMPOTÊNCIA como invariante de banco.
        b.Entity<EvidenceSignal>(e =>
        {
            e.Property(x => x.SchemaVersion).HasMaxLength(32);
            e.Property(x => x.Source).HasMaxLength(200);
            e.Property(x => x.EventType).HasMaxLength(200);
            e.Property(x => x.ExternalEventId).HasMaxLength(200);
            e.Property(x => x.DeduplicationKey).HasMaxLength(64);   // SHA-256 hex
            // O banco REJEITA fisicamente um segundo evento com a MESMA chave idempotente no par
            // (tenant, conector) — duas requisições concorrentes com o mesmo evento produzem UMA evidência.
            // Índice PARCIAL (mesmo idioma de GovernanceDocument.Sha256): a coleta pull grava
            // DeduplicationKey NULL (snapshots periódicos não são deduplicados) e essas linhas convivem.
            e.HasIndex(x => new { x.TenantId, x.ConnectorConfigId, x.DeduplicationKey })
                .IsUnique()
                .HasDatabaseName("UX_EvidenceSignal_Idempotency")   // nome estável: o executor reconhece SÓ esta violação
                .HasFilter("\"DeduplicationKey\" IS NOT NULL");
        });

        // [AEGIS-AUD-043] SignalMapping é a ÚNICA autoridade de (Capability, SignalKey) → subcategorias NIST
        // no framework ativo. A unicidade por (FrameworkVersionId, Capability, SignalKey) torna o seed
        // incremental uma invariante de banco e impede duas regras conflitantes para o mesmo sinal.
        b.Entity<SignalMapping>(e =>
        {
            e.Property(x => x.SignalKey).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.FrameworkVersionId, x.Capability, x.SignalKey }).IsUnique();
        });

        // [AEGIS-AUD-020] Hash SHA-256 (hex) da chave de ingestão — comprimento fixo é a invariante de banco.
        b.Entity<ConnectorConfig>().Property(x => x.IngestionKeyHash).HasMaxLength(64);

        // [AEGIS-MVP-POSTURE-02] Exposição de configuração (postura). Chave natural (Tenant, ConnectorConfig,
        // ExternalId) como ÍNDICE ÚNICO NOMEADO — torna a reconciliação (upsert + resolução) uma invariante de
        // banco (o reconciliador reconhece SÓ esta violação como corrida de inserção). Índices tenant-leading
        // por estado de ciclo de vida e por conector cobrem a listagem e a reconciliação sem full scan. Ameaças
        // → jsonb (mesmo idioma das listas do catálogo). Tamanhos fixos = invariante de banco; sem actionUrl/PII.
        b.Entity<PostureExposureFinding>(e =>
        {
            e.Property(x => x.ExternalId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.Service).HasMaxLength(200);
            e.Property(x => x.ActionType).HasMaxLength(100);
            e.Property(x => x.Tier).HasMaxLength(100);
            e.Property(x => x.ImplementationCost).HasMaxLength(100);
            e.Property(x => x.UserImpact).HasMaxLength(100);
            e.Property(x => x.Remediation).HasMaxLength(4000);
            e.Property(x => x.RemediationImpact).HasMaxLength(2000);
            e.Property(x => x.SourceState).HasMaxLength(100);
            e.Property(x => x.Threats)
                .HasConversion(stringList, stringListCmp).HasColumnType("jsonb");
            e.HasIndex(x => new { x.TenantId, x.ConnectorConfigId, x.ExternalId })
                .IsUnique()
                .HasDatabaseName("UX_PostureExposureFinding_Natural");
            e.HasIndex(x => new { x.TenantId, x.LifecycleState });
            e.HasIndex(x => new { x.TenantId, x.ConnectorConfigId });
        });

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
            // [AEGIS-AUD-010] PasswordHash é NULLABLE (deixou de ser IsRequired): uma conta federated-only,
            // provisionada por PlatformAdmin sem senha local, existe sem credencial. O login Local nunca
            // autentica conta sem hash (AuthService.LoginAsync) e o dummy hash preserva o guard de timing.
            e.HasIndex(a => a.Email).IsUnique();   // login único GLOBAL (era por tenant)

            // [AEGIS-AUD-007] Vínculo Entra: tid/oid nullable (contas locais não têm vínculo). Índice único
            // PARCIAL (só linhas vinculadas, WHERE oid IS NOT NULL — mesmo idioma do dedupe de
            // Asset.ExternalRef): impede que a MESMA identidade externa (tid,oid) caia em duas contas e
            // torna a corrida do primeiro vínculo uma invariante de BANCO. Contas sem vínculo (oid NULL)
            // convivem sem restrição.
            e.Property(a => a.ExternalTenantId).HasMaxLength(64);
            e.Property(a => a.ExternalObjectId).HasMaxLength(64);
            e.HasIndex(a => new { a.ExternalTenantId, a.ExternalObjectId })
                .IsUnique()
                .HasFilter("\"ExternalObjectId\" IS NOT NULL");

            // [AEGIS-AUD-011] Autoridade GLOBAL na identidade (não no membership). Default de BANCO None (0):
            // uma linha nova sem papel global explícito nasce sem autoridade — invariante segura e que torna
            // o schema robusto a inserts que não citam a coluna. A constraint fecha a porta a um int fora da
            // faixa (0=None, 1=PlatformAdmin) gravado por fora do domínio.
            e.Property(a => a.PlatformRole).HasDefaultValue(PlatformRole.None);
            e.ToTable(t => t.HasCheckConstraint("CK_IdentityAccounts_PlatformRole", "\"PlatformRole\" IN (0, 1)"));
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

            // [AEGIS-AUD-011] Papel TENANT-SCOPED: só 0/1/2 (Analyst/Manager/TenantAdmin). O antigo
            // PlatformAdmin (3) não pode mais ser gravado num membership — a constraint é a invariante de
            // banco do eixo tenant, complementar à allowlist do UserManagementService.
            e.ToTable(t => t.HasCheckConstraint("CK_Users_Role", "\"Role\" IN (0, 1, 2)"));
        });
        b.Entity<UserRefreshToken>(e =>
        {
            // [AEGIS-AUD-009] Só o hash SHA-256 (hex, 64 chars) é persistido — nunca o token bruto. O
            // comprimento fixo de 64 é a invariante de banco: um valor bruto (base64url ~43 chars ou mais)
            // ainda caberia, mas o índice único + lookup por hash e o backfill da migration garantem que
            // apenas hashes cheguem aqui.
            e.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(t => t.ReplacedByTokenHash).HasMaxLength(64);
            e.HasIndex(t => new { t.TenantId, t.TokenHash }).IsUnique();   // lookup do refresh por hash, tenant-leading
            e.HasIndex(t => new { t.TenantId, t.UserId });                 // revogação em massa por usuário (breach)
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
        // [AEGIS-AUD-050] Índices da fila operacional durável. A aquisição varre por AnalysisStatus ordenando
        // por AnalysisQueuedAt (trabalho disponível) e reclama Processing por AnalysisLeaseExpiresAt (lease
        // vencido) — um índice para cada caminho evita full scan na varredura cross-tenant do worker.
        b.Entity<GovernanceDocument>().HasIndex(x => new { x.AnalysisStatus, x.AnalysisQueuedAt });
        b.Entity<GovernanceDocument>().HasIndex(x => new { x.AnalysisStatus, x.AnalysisLeaseExpiresAt });
        b.Entity<DocumentControlMapping>().HasIndex(x => new { x.TenantId, x.GovernanceDocumentId });
        b.Entity<DocumentControlMapping>().HasIndex(x => new { x.TenantId, x.SubcategoryCode });
        b.Entity<SubcategoryCoverage>().HasIndex(x => new { x.TenantId, x.SubcategoryCode }).IsUnique();
        b.Entity<GrcInterviewSession>().HasIndex(x => x.TenantId);
        b.Entity<GrcInterviewMessage>().HasIndex(x => new { x.TenantId, x.SessionId });
        b.Entity<IdentifiedRisk>().HasIndex(x => new { x.TenantId, x.SubcategoryCode });

        // [AEGIS-AUD-050] Fila operacional durável de sincronização de políticas. Três índices:
        //  - claim: (Status, AvailableAt) cobre a varredura por pedido disponível ordenado por disponibilidade;
        //  - lease: (Status, LeaseExpiresAt) cobre a reclamação de Processing com lease vencido;
        //  - dedupe: ÚNICO PARCIAL só em TenantId WHERE ativo (Pending=0/Processing=1) — no máximo um pedido
        //    ativo por tenant, a invariante que torna EnqueueAsync idempotente (mesmo idioma do dedupe de
        //    GovernanceDocument.Sha256). O filtro usa os ordinais do enum, que o Npgsql persiste como int.
        b.Entity<PolicySyncRequest>(e =>
        {
            e.Property(x => x.ErrorCategory).HasMaxLength(200);
            e.HasIndex(x => new { x.Status, x.AvailableAt });
            e.HasIndex(x => new { x.Status, x.LeaseExpiresAt });
            e.HasIndex(x => x.TenantId)
                .IsUnique()
                .HasFilter("\"Status\" IN (0, 1)");
        });
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

            // A ORIGEM documental vigente nunca pode apontar silenciosamente para um documento inexistente:
            // FK (sem navegação) para GovernanceDocument + índice de apoio. Restrict (não cascateia): a
            // exclusão de documento RECONCILIA (retrai/repointa o estado) ANTES de remover a linha do
            // documento, então a FK nunca bloqueia uma exclusão legítima e um estado órfão é impossível.
            e.HasOne<GovernanceDocument>().WithMany()
                .HasForeignKey(x => x.OriginDocumentId)
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
        // Unicidade composta (TenantId, Code, Source) para ameaças TENANT-SPECIFIC.
        b.Entity<Threat>(e =>
        {
            e.Property(t => t.Code).HasMaxLength(64).IsRequired();
            // [AEGIS-MVP-VULN-01] Metadados CONSULTÁVEIS de CVE (fatos da fonte). Tamanhos fixos = invariante de banco.
            e.Property(t => t.Severity).HasMaxLength(50);
            e.Property(t => t.CvssVector).HasMaxLength(200);
            e.HasIndex(t => new { t.TenantId, t.Code, t.Source }).IsUnique();
            // [AEGIS-MVP-VULN-01] ⚠️ No PostgreSQL NULLs são DISTINTOS, então o índice acima NÃO dedupe o catálogo
            // GLOBAL (TenantId nulo): dois CVEs globais de mesmo Code+Source passariam. Um índice único PARCIAL
            // sobre (Code, Source) WHERE TenantId IS NULL torna a unicidade do catálogo público uma invariante de
            // banco (mesmo idioma do dedupe parcial de Asset.ExternalRef) — e o reconciliador reconhece SÓ esta
            // violação como corrida de inserção. A unicidade tenant-specific acima é preservada.
            e.HasIndex(t => new { t.Code, t.Source })
                .IsUnique()
                .HasDatabaseName("UX_Threat_GlobalNaturalKey")
                .HasFilter("\"TenantId\" IS NULL");
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

        // [AEGIS-MVP-VULN-01] Vínculo Asset ↔ FONTE (provider-neutral). Chave natural (Tenant, Conector, ExternalId)
        // como ÍNDICE ÚNICO NOMEADO — o upsert idempotente por fonte é invariante de banco (o reconciliador reconhece
        // SÓ esta violação como corrida). FKs Restrict (o binding é histórico; apagar Asset/Conector não cascateia).
        // Tenant-leading por Asset (recompute agregado) e por Conector (reconciliação por fonte). Sem IP/PII.
        b.Entity<AssetSourceBinding>(e =>
        {
            e.Property(x => x.ExternalId).HasMaxLength(200).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.SubType).HasMaxLength(100);
            e.HasOne(x => x.Asset).WithMany()
                .HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ConnectorConfig).WithMany()
                .HasForeignKey(x => x.ConnectorConfigId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.ConnectorConfigId, x.ExternalId })
                .IsUnique()
                .HasDatabaseName("UX_AssetSourceBinding_Natural");
            e.HasIndex(x => new { x.TenantId, x.AssetId });
            e.HasIndex(x => new { x.TenantId, x.ConnectorConfigId });
        });

        // [AEGIS-MVP-VULN-01] Observação de UMA fonte sobre uma exposição CONSOLIDADA ativo×CVE. Chave natural
        // (Tenant, Conector, Exposição) única — cada fonte tem no máximo uma observação por exposição. FKs Restrict.
        // Índices tenant-leading por exposição (consolidação/leitura efetiva) e por conector (reconciliação por fonte).
        b.Entity<AssetThreatObservation>(e =>
        {
            e.HasOne(x => x.AssetThreatExposure).WithMany()
                .HasForeignKey(x => x.AssetThreatExposureId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ConnectorConfig).WithMany()
                .HasForeignKey(x => x.ConnectorConfigId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.ConnectorConfigId, x.AssetThreatExposureId })
                .IsUnique()
                .HasDatabaseName("UX_AssetThreatObservation_Natural");
            e.HasIndex(x => new { x.TenantId, x.AssetThreatExposureId });
            e.HasIndex(x => new { x.TenantId, x.ConnectorConfigId });
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

        // ============================================================
        //  AEGIS KNIGHT — execução + resultados de indicadores
        // ============================================================

        // Execução do assessment: tenant-owned. Índice tenant-leading por StartedAt para a consulta "latest"
        // (seek por tenant + range ordenado por data). Filhos (indicadores) cascateiam com a execução.
        b.Entity<KnightAssessmentRun>(e =>
        {
            e.Property(x => x.Source).HasMaxLength(200);
            e.Property(x => x.CatalogVersion).HasMaxLength(50);
            e.Property(x => x.ScoreFormulaVersion).HasMaxLength(50);
            // Multicoletor (aditivo): defaults tornam a migration segura sobre linhas existentes (Demo/Completed).
            e.Property(x => x.SourceType).HasDefaultValue(KnightSourceType.Demo);
            e.Property(x => x.SourceState).HasDefaultValue(KnightSourceState.Completed);
            e.HasIndex(x => new { x.TenantId, x.StartedAt });

            // INTEGRIDADE MULTI-TENANT NO BANCO: chave alternativa composta (Id, TenantId) que a FK do filho
            // referencia. Assim o próprio banco REJEITA um resultado cujo TenantId não seja o da execução —
            // o query filter esconde a inconsistência, mas só a FK composta impede a corrupção relacional.
            e.HasAlternateKey(x => new { x.Id, x.TenantId });
            e.HasMany(x => x.Indicators).WithOne(i => i.Run)
                .HasForeignKey(i => new { i.RunId, i.TenantId })
                .HasPrincipalKey(x => new { x.Id, x.TenantId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Resultado por indicador: tenant-owned. O indicador NÃO existe sem a execução → Cascade (acima).
        b.Entity<KnightIndicatorResult>(e =>
        {
            e.Property(x => x.IndicatorId).HasMaxLength(40).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Recommendation).HasMaxLength(1000);
            e.Property(x => x.NotEvaluatedReason).HasMaxLength(500);
            e.Property(x => x.SourceType).HasDefaultValue(KnightSourceType.Demo);
            // Uma execução não pode conter duas linhas para o MESMO IndicatorId — invariante de banco. O
            // índice é tenant-leading e cobre o carregamento por (tenant, execução), substituindo o antigo
            // índice não-único (TenantId, RunId).
            e.HasIndex(x => new { x.TenantId, x.RunId, x.IndicatorId }).IsUnique();
        });

        // ============================================================
        //  Fotografia AUDITÁVEL de postura — histórico imutável compartilhado
        // ============================================================

        // [AEGIS-AUD-035/036/037] A fotografia é tenant-owned e APPEND-ONLY. Índices tenant-leading por instante
        // (lista cronológica) e por (tenant, tipo, instante) — a UI filtra por instrumento. A imutabilidade é
        // garantida no serviço (sem update/delete) e REFORÇADA por um gatilho no PostgreSQL (ver a migration);
        // a chave alternativa composta (Id, TenantId) é o alvo da FK dos filhos, para o banco recusar filho de
        // tenant divergente (o mesmo idioma do KnightAssessmentRun).
        b.Entity<PostureSnapshot>(e =>
        {
            e.Property(x => x.SchemaVersion).HasMaxLength(50).IsRequired();
            e.Property(x => x.FormulaVersion).HasMaxLength(50).IsRequired();
            e.Property(x => x.CatalogVersion).HasMaxLength(200).IsRequired();
            e.Property(x => x.SemanticFamily).HasMaxLength(300).IsRequired();
            e.Property(x => x.SourceLabel).HasMaxLength(200);
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.CapturedAt });
            e.HasIndex(x => new { x.TenantId, x.Type, x.CapturedAt });

            e.HasAlternateKey(x => new { x.Id, x.TenantId });
            e.HasMany(x => x.Controls).WithOne(c => c.Snapshot)
                .HasForeignKey(c => new { c.SnapshotId, c.TenantId })
                .HasPrincipalKey(x => new { x.Id, x.TenantId })
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Indicators).WithOne(i => i.Snapshot)
                .HasForeignKey(i => new { i.SnapshotId, i.TenantId })
                .HasPrincipalKey(x => new { x.Id, x.TenantId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Controle NIST congelado: tenant-owned. Referências de evidência sanitizadas → jsonb (idioma das listas
        // do catálogo). NOT NULL com default de lista VAZIA — "sem referência" é [], nunca NULL.
        b.Entity<PostureSnapshotControl>(e =>
        {
            e.Property(x => x.SubcategoryCode).HasMaxLength(15).IsRequired();
            e.Property(x => x.FunctionCode).HasMaxLength(5).IsRequired();
            e.Property(x => x.EvidenceRefs)
                .HasConversion(evidenceRefs, evidenceRefsCmp)
                .HasColumnType("jsonb")
                .HasDefaultValue(new List<PostureEvidenceRef>())
                .IsRequired();
            e.HasIndex(x => new { x.TenantId, x.SnapshotId });
        });

        // Indicador KNIGHT congelado: tenant-owned. Listas de mapeamento → jsonb (mesmo idioma do KnightIndicatorResult).
        b.Entity<PostureSnapshotIndicator>(e =>
        {
            e.Property(x => x.IndicatorId).HasMaxLength(40).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Evidence).HasMaxLength(2000);
            e.Property(x => x.NistCodes)
                .HasConversion(stringList, stringListCmp).HasColumnType("jsonb");
            e.Property(x => x.MitreTechniques)
                .HasConversion(stringList, stringListCmp).HasColumnType("jsonb");
            e.HasIndex(x => new { x.TenantId, x.SnapshotId });
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
        // [AEGIS-MVP-POSTURE-02] Exposições de postura são ITenantOwned (fail-closed): um tenant jamais lê as de outro.
        b.Entity<PostureExposureFinding>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
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
        // [AEGIS-AUD-050] A solicitação de sync é tenant-owned; a varredura cross-tenant do worker usa
        // IgnoreQueryFilters (aquisição) explicitamente, como os demais componentes de background.
        b.Entity<PolicySyncRequest>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<TenantControlState>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<TenantScoreSnapshot>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<RemediationAdvisory>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        // Identify (ID.RA) — grafo, exposições e snapshots são ITenantOwned (Threat é reference data, sem filtro).
        b.Entity<AssetDependency>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<AssetThreatExposure>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        // [AEGIS-MVP-VULN-01] Binding e observação são ITenantOwned (fail-closed): uma fonte de um tenant jamais
        // enxerga bindings/observações de outro. Stamping do TenantId no insert é automático (SaveChanges guard).
        b.Entity<AssetSourceBinding>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<AssetThreatObservation>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<BlastRadiusAssessment>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<BlastRadiusImpactNode>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        // AEGIS KNIGHT — execução e resultados são ITenantOwned (fail-closed, como o restante do modelo).
        b.Entity<KnightAssessmentRun>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<KnightIndicatorResult>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        // Fotografia auditável de postura — pai e filhos são ITenantOwned (fail-closed): um tenant jamais lê,
        // consulta ou compara a fotografia de outro.
        b.Entity<PostureSnapshot>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<PostureSnapshotControl>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
        b.Entity<PostureSnapshotIndicator>().HasQueryFilter(e => e.TenantId == _tenant.TenantId);
    }

    // [AEGIS-AUD-008] Todos os quatro pontos de entrada públicos de SaveChanges são interceptados
    // sobrescrevendo APENAS os overloads que recebem `acceptAllChangesOnSuccess` — os parametrizados por
    // (bool) e (bool, ct). Os overloads sem bool do EF Core delegam para estes, então SaveChanges(),
    // SaveChanges(bool), SaveChangesAsync(ct) e SaveChangesAsync(bool, ct) passam TODOS pelo guard, sem
    // dupla validação (o sem-bool não é sobrescrito; só encaminha). Fecha o bypass dos overloads (bool).

    /// <summary>Guard de isolamento multi-tenant (fail-closed) + timestamps de auditoria, em toda gravação.</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenantWriteIsolation();
        StampAudit();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await EnforceTenantWriteIsolationAsync(cancellationToken);
        StampAudit();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// [AEGIS-AUD-008] Ponto central de proteção de ESCRITA multi-tenant. Query filters isolam LEITURAS,
    /// mas UPDATE/DELETE de entidades rastreadas são emitidos pela chave primária e não passam pelo filtro —
    /// por isso a proteção precisa viver aqui, no SaveChanges, e não nos controllers/services.
    ///
    /// Fail-CLOSED para <c>Added</c>, <c>Modified</c> e <c>Deleted</c> de qualquer <see cref="ITenantOwned"/>:
    ///  - <c>Added</c>: carimba o tenant ambiente e rejeita um TenantId fornecido divergente;
    ///  - <c>Modified</c>: a linha PERSISTIDA precisa pertencer ao tenant ambiente e o TenantId não pode mudar;
    ///  - <c>Deleted</c>: a linha PERSISTIDA precisa pertencer ao tenant ambiente.
    ///
    /// A dona da verdade para Modified/Deleted é a linha NO BANCO (<c>GetDatabaseValues</c>), nunca
    /// <c>entry.Entity.TenantId</c> nem os <c>OriginalValues</c> — um stub anexado à mão com Id de outro tenant
    /// e TenantId falsificado traz OriginalValues forjados. Sem tenant resolvido, ou <c>Guid.Empty</c>, falha.
    /// </summary>
    private void EnforceTenantWriteIsolation()
    {
        var tenantId = PrepareTenantWrites(out var modified, out var deleted);
        if (tenantId is null) return;

        foreach (var entry in modified)
            VerifyPersistedOwnership(entry, entry.GetDatabaseValues(), tenantId.Value, isModify: true);
        foreach (var entry in deleted)
            VerifyPersistedOwnership(entry, entry.GetDatabaseValues(), tenantId.Value, isModify: false);
    }

    private async Task EnforceTenantWriteIsolationAsync(CancellationToken ct)
    {
        var tenantId = PrepareTenantWrites(out var modified, out var deleted);
        if (tenantId is null) return;

        foreach (var entry in modified)
            VerifyPersistedOwnership(entry, await entry.GetDatabaseValuesAsync(ct), tenantId.Value, isModify: true);
        foreach (var entry in deleted)
            VerifyPersistedOwnership(entry, await entry.GetDatabaseValuesAsync(ct), tenantId.Value, isModify: false);
    }

    /// <summary>
    /// Resolve o tenant ambiente (fail-closed), carimba/valida os <c>Added</c> e devolve os <c>Modified</c> e
    /// <c>Deleted</c> pendentes de verificação contra o banco. Retorna <c>null</c> quando não há NENHUMA
    /// escrita de entidade tenant-owned — assim entidades globais (catálogo NIST, IdentityAccount) seguem
    /// gravando sem exigir tenant ambiente.
    /// </summary>
    private Guid? PrepareTenantWrites(
        out List<EntityEntry<ITenantOwned>> modified, out List<EntityEntry<ITenantOwned>> deleted)
    {
        modified = new();
        deleted = new();

        var owned = ChangeTracker.Entries<ITenantOwned>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        if (owned.Count == 0) return null;

        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException(
                "Gravação de entidade multi-tenant sem tenant resolvido no contexto (fail-closed).");
        if (tenantId == Guid.Empty)
            throw new TenantSecurityException("TenantId do contexto é inválido (Guid.Empty).");

        foreach (var entry in owned)
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    // Nunca confiar num TenantId fornecido pelo cliente que diverge do tenant ambiente.
                    var supplied = entry.Entity.TenantId;
                    if (supplied != Guid.Empty && supplied != tenantId)
                        throw new TenantSecurityException(
                            $"TenantId fornecido em '{entry.Entity.GetType().Name}' diverge do tenant do contexto.");
                    entry.Entity.TenantId = tenantId;
                    break;
                case EntityState.Modified:
                    modified.Add(entry);
                    break;
                case EntityState.Deleted:
                    deleted.Add(entry);
                    break;
            }
        }
        return tenantId;
    }

    /// <summary>
    /// Confirma, contra a linha AUTORITATIVA no banco, que um Modified/Deleted só toca dados do tenant
    /// ambiente. <paramref name="dbValues"/> nulo = a linha não existe OU não é visível ao tenant — em ambos
    /// os casos a escrita é recusada ANTES de qualquer mutação. Mensagens sem dados sensíveis da entidade.
    /// </summary>
    private static void VerifyPersistedOwnership(
        EntityEntry<ITenantOwned> entry, PropertyValues? dbValues, Guid tenantId, bool isModify)
    {
        var kind = isModify ? "alterar" : "remover";
        var name = entry.Entity.GetType().Name;

        if (dbValues is null)
            throw new TenantSecurityException(
                $"Tentativa de {kind} uma linha inexistente ou fora do tenant do contexto ('{name}').");

        var persistedTenantId = dbValues.GetValue<Guid>(nameof(ITenantOwned.TenantId));
        if (persistedTenantId != tenantId)
            throw new TenantSecurityException(
                $"Tentativa de {kind} uma linha pertencente a outro tenant ('{name}').");

        // Modified não pode reescrever o TenantId para longe do tenant ambiente (a linha persistida é dele).
        if (isModify && entry.Entity.TenantId != tenantId)
            throw new TenantSecurityException(
                $"Tentativa de alterar o TenantId de '{name}' para outro tenant.");
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
