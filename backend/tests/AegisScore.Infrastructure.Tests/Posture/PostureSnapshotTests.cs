using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AegisScore.Api.Controllers;
using AegisScore.Application.Posture;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Posture;

/// <summary>
/// Testes FOCADOS da fotografia AUDITÁVEL de postura (AEGIS-AUD-035/036/037): publicação pelas autoridades do
/// servidor; universo NIST COMPLETO (avaliados e não avaliados) com "não avaliado" distinto de zero/NonCompliant;
/// proveniência = a evidência DECISIVA do veredito; hash re-derivável após round-trip e INEQUÍVOCO (delimitadores,
/// Unicode, null≠vazio≠zero); comparação compatível/incompatível; isolamento entre tenants; separação AEGIS×KNIGHT;
/// e publicação restrita a Manager/TenantAdmin. A imutabilidade REFORÇADA no banco é validada no gate PostgreSQL
/// real (<see cref="PostureSnapshotPostgresTests"/>).
/// </summary>
public sealed class PostureSnapshotTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaa1111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("bbbb2222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;

    public PostureSnapshotTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- 1) Publicação AEGIS deriva das AUTORIDADES do servidor; universo COMPLETO ------------------

    [Fact]
    public async Task Publish_Aegis_DerivesFromLedger_AndIncludesFullUniverse()
    {
        await using var db = NewContext(TenantA);
        await SeedDefaultLedgerAsync(db);   // 01=Compliant(20/20), 02=NonCompliant(0/10), 03=elegível SEM estado

        var detail = await ServiceFor(db, TenantA).PublishAsync(PostureSnapshotType.AegisScoreNist, null);

        detail.Summary.FormulaVersion.Should().Be("aegis-score-v1");
        detail.AchievedPoints.Should().Be(20);
        detail.PossiblePoints.Should().Be(30);
        detail.EligiblePoints.Should().Be(40);
        detail.Summary.Score.Should().BeApproximately(66.7, 0.05);
        detail.Summary.Coverage.Should().BeApproximately(75.0, 0.05);

        detail.Controls.Should().HaveCount(3, "a fotografia parte do catálogo ATIVO COMPLETO, não só do ledger");
        detail.Summary.CompliantCount.Should().Be(1);
        detail.Summary.NonCompliantCount.Should().Be(1);
        detail.Summary.NotEvaluatedCount.Should().Be(1);
        detail.Summary.ContentHash.Should().HaveLength(64);
    }

    // ---- 2) "Não avaliado" é DISTINTO de zero e de NonCompliant ------------------------------------

    [Fact]
    public async Task Publish_Aegis_NotEvaluatedControl_IsDistinctFromZeroAndNonCompliant()
    {
        await using var db = NewContext(TenantA);
        await SeedDefaultLedgerAsync(db);

        var detail = await ServiceFor(db, TenantA).PublishAsync(PostureSnapshotType.AegisScoreNist, null);

        var notEvaluated = detail.Controls.Single(c => c.SubcategoryCode == "PR.AA-03");
        notEvaluated.Evaluated.Should().BeFalse();
        notEvaluated.Status.Should().BeNull("não avaliado NÃO é NonCompliant — é 'sem score'");
        notEvaluated.VerdictSource.Should().BeNull();
        notEvaluated.EvaluatedAt.Should().BeNull();
        notEvaluated.AchievedPoints.Should().Be(0);
        notEvaluated.MaxPoints.Should().Be(10, "o peso elegível é preservado para explicar a cobertura por item");

        var nonCompliant = detail.Controls.Single(c => c.SubcategoryCode == "PR.AA-02");
        nonCompliant.Evaluated.Should().BeTrue();
        nonCompliant.Status.Should().Be("NonCompliant");
    }

    // ---- 3) Score anulável: ledger vazio → NotEvaluated (nunca 0%) ----------------------------------

    [Fact]
    public async Task Publish_Aegis_EmptyLedger_ScoreNull_NotEvaluated()
    {
        await using var db = NewContext(TenantA);
        await SeedActiveFrameworkAsync(db);   // catálogo ativo, porém NENHUM TenantControlState

        var detail = await ServiceFor(db, TenantA).PublishAsync(PostureSnapshotType.AegisScoreNist, null);

        detail.Summary.Score.Should().BeNull("sem controle avaliado não existe percentual — nunca 0%");
        detail.Summary.EvaluationState.Should().Be("NotEvaluated");
        detail.Summary.EvaluatedItems.Should().Be(0);
        detail.Summary.EligibleItems.Should().Be(3);
        detail.Controls.Should().OnlyContain(c => !c.Evaluated);
    }

    // ---- 4) Proveniência: referencia a evidência DECISIVA (mais nova), não a "recente" --------------

    [Fact]
    public async Task Publish_Aegis_TelemetryProvenance_ReferencesDecidingEvidence()
    {
        await using var db = NewContext(TenantA);
        await SeedActiveFrameworkAsync(db);
        await SeedTelemetryProvenanceAsync(db);   // sinal antigo (50→NonCompliant) e novo (95→Compliant) p/ PR.AA-01

        var detail = await ServiceFor(db, TenantA).PublishAsync(PostureSnapshotType.AegisScoreNist, null);

        var control = detail.Controls.Single(c => c.SubcategoryCode == "PR.AA-01");
        control.VerdictSource.Should().Be("Telemetry");
        control.EvidenceRefs.Should().ContainSingle("só a evidência DECISIVA fundamenta o veredito vigente");
        control.EvidenceRefs[0].Kind.Should().Be("telemetry");
        control.EvidenceRefs[0].Reference.Should().Be("evt-new", "o veredito vigente vem do sinal mais NOVO");
        control.EvidenceRefs.Should().NotContain(r => r.Reference == "evt-old", "o sinal antigo não determinou o veredito");
    }

    // ---- 5) Imutabilidade de serviço: a fotografia NÃO muda após alteração operacional + re-deriva --

    [Fact]
    public async Task Snapshot_DoesNotChange_AfterLaterLedgerMutation_AndHashReDerives()
    {
        Guid snapshotId;
        double? scoreAtPublish;
        string hashAtPublish;

        await using (var db = NewContext(TenantA))
        {
            await SeedDefaultLedgerAsync(db);
            var published = await ServiceFor(db, TenantA).PublishAsync(PostureSnapshotType.AegisScoreNist, null);
            snapshotId = published.Summary.Id;
            scoreAtPublish = published.Summary.Score;
            hashAtPublish = published.Summary.ContentHash;
        }

        await using (var db = NewContext(TenantA))
        {
            var b = await db.TenantControlStates.Include(t => t.Subcategory)
                .SingleAsync(t => t.Subcategory!.Code == "PR.AA-02");
            b.Status = ControlStatus.Compliant;
            b.CurrentScore = 10;
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(TenantA))
        {
            var frozen = await db.PostureSnapshots.AsNoTracking()
                .Include(s => s.Controls).Include(s => s.Indicators)
                .SingleAsync(s => s.Id == snapshotId);

            frozen.Score.Should().Be(scoreAtPublish);
            frozen.ContentHash.Should().Be(hashAtPublish);
            PostureSnapshotHasher.Verify(frozen).Should().BeTrue("o hash é re-derivável da linha persistida");
            frozen.Controls.Single(c => c.SubcategoryCode == "PR.AA-02").Status
                .Should().Be(ControlStatus.NonCompliant, "a foto congelou o estado do instante da publicação");
        }
    }

    // ---- 6) Hash determinístico + INEQUÍVOCO (delimitadores, Unicode, null≠vazio≠zero) --------------

    [Fact]
    public void Hash_IsDeterministic_ForSameContent()
    {
        PostureSnapshotHasher.Compute(SampleSnapshot()).Should().Be(PostureSnapshotHasher.Compute(SampleSnapshot()));
    }

    [Fact]
    public void Hash_Unambiguous_DelimiterInValue_NoStructuralCollision()
    {
        // Sob concatenação ingênua "campo D campo", ("a","b;c") e ("a;b","c") colidiriam. Length-prefixed, não.
        var s1 = SampleSnapshot(); s1.FormulaVersion = "a"; s1.CatalogVersion = "b;c";
        var s2 = SampleSnapshot(); s2.FormulaVersion = "a;b"; s2.CatalogVersion = "c";
        PostureSnapshotHasher.Compute(s1).Should().NotBe(PostureSnapshotHasher.Compute(s2));

        // Delimitadores do escritor ('|', ':', ';', '~', newline) dentro de um valor não criam colisão.
        var s3 = SampleSnapshot(); s3.SourceLabel = "x:1|y;2\n~z";
        var s4 = SampleSnapshot(); s4.SourceLabel = "x:1|y;2\n~zEXTRA";
        PostureSnapshotHasher.Compute(s3).Should().NotBe(PostureSnapshotHasher.Compute(s4));
    }

    [Fact]
    public void Hash_Unicode_IsStable_AndDistinct()
    {
        var a1 = SampleSnapshot(); a1.SourceLabel = "café ☕ 中文 🛡";
        var a2 = SampleSnapshot(); a2.SourceLabel = "café ☕ 中文 🛡";
        var b = SampleSnapshot(); b.SourceLabel = "cafe ☕ 中文 🛡";
        PostureSnapshotHasher.Compute(a1).Should().Be(PostureSnapshotHasher.Compute(a2), "mesmo Unicode → mesmo hash");
        PostureSnapshotHasher.Compute(a1).Should().NotBe(PostureSnapshotHasher.Compute(b));
    }

    [Fact]
    public void Hash_NullVsEmptyVsZero_AreAllDistinct()
    {
        var nullLabel = SampleSnapshot(); nullLabel.SourceLabel = null;
        var emptyLabel = SampleSnapshot(); emptyLabel.SourceLabel = "";
        PostureSnapshotHasher.Compute(nullLabel).Should().NotBe(PostureSnapshotHasher.Compute(emptyLabel),
            "null é distinto de string vazia");

        var nullScore = SampleSnapshot(); nullScore.Score = null;
        var zeroScore = SampleSnapshot(); zeroScore.Score = 0;
        PostureSnapshotHasher.Compute(nullScore).Should().NotBe(PostureSnapshotHasher.Compute(zeroScore),
            "score null (não avaliado) é distinto de 0");

        // Controle não avaliado (Status null) ≠ NonCompliant.
        var notEval = SampleSnapshot(); notEval.Controls.First().Evaluated = false; notEval.Controls.First().Status = null;
        var nonComp = SampleSnapshot(); nonComp.Controls.First().Evaluated = true; nonComp.Controls.First().Status = ControlStatus.NonCompliant;
        PostureSnapshotHasher.Compute(notEval).Should().NotBe(PostureSnapshotHasher.Compute(nonComp));
    }

    [Fact]
    public void Hash_AnyAuditableFieldChange_ChangesHash_AndTamperDetected()
    {
        var baseline = SampleSnapshot();
        var h = PostureSnapshotHasher.Compute(baseline);

        Mutate(s => s.NonCompliantCount += 1).Should().NotBe(h);
        Mutate(s => s.Coverage = 74.9).Should().NotBe(h);
        Mutate(s => s.Controls.First().AchievedPoints += 1).Should().NotBe(h);
        Mutate(s => s.Controls.First().EvidenceRefs.Add(new PostureEvidenceRef("telemetry", "S", "ref", null))).Should().NotBe(h);
        Mutate(s => s.Indicators.First().Title += "x").Should().NotBe(h);
        Mutate(s => s.Indicators.First().Evidence += "x").Should().NotBe(h);
        Mutate(s => s.Indicators.First().NistCodes.Add("XX.YY-99")).Should().NotBe(h);

        baseline.ContentHash = h;
        PostureSnapshotHasher.Verify(baseline).Should().BeTrue();
        baseline.AchievedPoints += 1;
        PostureSnapshotHasher.Verify(baseline).Should().BeFalse("uma alteração indevida é detectada pelo hash");

        static string Mutate(Action<PostureSnapshot> change) { var s = SampleSnapshot(); change(s); return PostureSnapshotHasher.Compute(s); }
    }

    // ---- 7) Isolamento por tenant: outro tenant não lê, lista nem compara ---------------------------

    [Fact]
    public async Task TenantIsolation_OtherTenant_CannotReadListOrCompare()
    {
        Guid id1, id2;
        await using (var dbA = NewContext(TenantA))
        {
            await SeedDefaultLedgerAsync(dbA);
            var svc = ServiceFor(dbA, TenantA);
            id1 = (await svc.PublishAsync(PostureSnapshotType.AegisScoreNist, null)).Summary.Id;
            id2 = (await svc.PublishAsync(PostureSnapshotType.AegisScoreNist, null)).Summary.Id;
        }

        await using var dbB = NewContext(TenantB);
        var svcB = ServiceFor(dbB, TenantB);

        (await svcB.GetAsync(id1)).Should().BeNull("um tenant jamais lê a fotografia de outro");
        (await svcB.ListAsync(null)).Should().BeEmpty();
        (await svcB.CompareAsync(id1, id2)).Should().BeNull("comparar fotografias de outro tenant é impossível");
    }

    // ---- 8) Comparação COMPATÍVEL: melhora, contagens e delta de score ------------------------------

    [Fact]
    public async Task Compare_Compatible_ReportsImprovedAndDeltas()
    {
        await using var db = NewContext(TenantA);
        await SeedDefaultLedgerAsync(db);
        var svc = ServiceFor(db, TenantA);

        var before = (await svc.PublishAsync(PostureSnapshotType.AegisScoreNist, null)).Summary.Id;

        var b = await db.TenantControlStates.Include(t => t.Subcategory)
            .SingleAsync(t => t.Subcategory!.Code == "PR.AA-02");
        b.Status = ControlStatus.Compliant;
        b.CurrentScore = 10;
        await db.SaveChangesAsync();

        var after = (await svc.PublishAsync(PostureSnapshotType.AegisScoreNist, null)).Summary.Id;

        var result = await svc.CompareAsync(before, after);

        result!.Compatible.Should().BeTrue();
        result.Delta!.ScoreDeltaState.Should().Be(nameof(ScoreDeltaState.Numeric));
        result.Delta.ScoreDelta.Should().BeApproximately(33.3, 0.1);   // 100 − 66.7
        result.Delta.Counts.Compliant.Should().Be(1);
        result.Delta.Counts.NonCompliant.Should().Be(-1);
        result.Delta.Improved.Should().ContainSingle(i => i.Code == "PR.AA-02");
        result.Delta.Worsened.Should().BeEmpty();
    }

    // ---- 9) Transição NÃO AVALIADO → AVALIADO ------------------------------------------------------

    [Fact]
    public async Task Compare_BecameEvaluated_NoMisleadingScoreDelta()
    {
        await using var db = NewContext(TenantA);
        await SeedDefaultLedgerAsync(db);   // PR.AA-03 começa NÃO avaliado
        var svc = ServiceFor(db, TenantA);

        var before = (await svc.PublishAsync(PostureSnapshotType.AegisScoreNist, null)).Summary.Id;

        var subId = await db.Subcategories.Where(s => s.Code == "PR.AA-03").Select(s => s.Id).SingleAsync();
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = subId, Status = ControlStatus.Compliant, CurrentScore = 10,
            LastVerdictSource = VerdictSource.Telemetry, LastEvaluatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var after = (await svc.PublishAsync(PostureSnapshotType.AegisScoreNist, null)).Summary.Id;

        var result = await svc.CompareAsync(before, after);

        result!.Compatible.Should().BeTrue();
        result.Delta!.NowEvaluated.Should().Contain(i => i.Code == "PR.AA-03");
        result.Delta.NoLongerEvaluated.Should().NotContain(i => i.Code == "PR.AA-03");
    }

    // ---- 10) Transição AVALIADO → NÃO AVALIADO -----------------------------------------------------

    [Fact]
    public async Task Compare_NoLongerEvaluated_DetectedFromFullUniverse()
    {
        await using var db = NewContext(TenantA);
        await SeedDefaultLedgerAsync(db);
        var svc = ServiceFor(db, TenantA);

        var before = (await svc.PublishAsync(PostureSnapshotType.AegisScoreNist, null)).Summary.Id;

        var state = await db.TenantControlStates.Include(t => t.Subcategory)
            .SingleAsync(t => t.Subcategory!.Code == "PR.AA-02");
        db.TenantControlStates.Remove(state);   // PR.AA-02 deixa de ser avaliado
        await db.SaveChangesAsync();

        var after = (await svc.PublishAsync(PostureSnapshotType.AegisScoreNist, null)).Summary.Id;

        var result = await svc.CompareAsync(before, after);

        result!.Compatible.Should().BeTrue();
        result.Delta!.NoLongerEvaluated.Should().Contain(i => i.Code == "PR.AA-02");
        result.Delta.NowEvaluated.Should().NotContain(i => i.Code == "PR.AA-02");
    }

    // ---- 11) Comparação INCOMPATÍVEL (AEGIS × KNIGHT) recusada com motivos --------------------------

    [Fact]
    public async Task Compare_Incompatible_AegisVsKnight_RefusedWithReasons()
    {
        await using var db = NewContext(TenantA);
        await SeedDefaultLedgerAsync(db);
        await SeedKnightRunAsync(db);
        var svc = ServiceFor(db, TenantA);

        var aegis = (await svc.PublishAsync(PostureSnapshotType.AegisScoreNist, null)).Summary.Id;
        var knight = (await svc.PublishAsync(PostureSnapshotType.Knight, null)).Summary.Id;

        var result = await svc.CompareAsync(aegis, knight);

        result!.Compatible.Should().BeFalse("instrumentos distintos não se comparam — não há score combinado");
        result.IncompatibilityReasons.Should().Contain(PostureSnapshotComparer.ReasonDifferentType);
        result.Delta.Should().BeNull("nenhum delta enganoso é calculado entre incompatíveis");
    }

    // ---- 12) KNIGHT usa a fórmula PRÓPRIA e permanece separado do AEGIS Score; re-deriva -----------

    [Fact]
    public async Task Publish_Knight_UsesKnightAuthority_SeparateFromAegis_AndHashReDerives()
    {
        Guid id;
        await using (var db = NewContext(TenantA))
        {
            await SeedKnightRunAsync(db);
            var detail = await ServiceFor(db, TenantA).PublishAsync(PostureSnapshotType.Knight, null);
            id = detail.Summary.Id;

            detail.Summary.FormulaVersion.Should().Be("knight-score-v1", "score KNIGHT nunca é a fórmula aegis-score-v1");
            detail.Summary.SourceType.Should().Be("MicrosoftEntraId");
            detail.Summary.SemanticFamily.Should().Be("knight:MicrosoftEntraId");
            detail.Indicators.Should().NotBeEmpty();
            detail.Controls.Should().BeEmpty("uma fotografia KNIGHT não carrega controles NIST do AEGIS Score");
        }

        await using (var db = NewContext(TenantA))
        {
            var frozen = await db.PostureSnapshots.AsNoTracking()
                .Include(s => s.Controls).Include(s => s.Indicators).SingleAsync(s => s.Id == id);
            PostureSnapshotHasher.Verify(frozen).Should().BeTrue("o hash KNIGHT re-deriva após persistência (com evidência Unicode)");
        }
    }

    // ---- 13) Publicação KNIGHT sem assessment: recusa explícita (não cai para Demo) -----------------

    [Fact]
    public async Task Publish_Knight_NoAssessment_ThrowsNotAvailable()
    {
        await using var db = NewContext(TenantA);
        var act = () => ServiceFor(db, TenantA).PublishAsync(PostureSnapshotType.Knight, null);
        await act.Should().ThrowAsync<PostureSnapshotNotAvailableException>();
    }

    // ---- 14) Autorização: publicar exige Manager/TenantAdmin — Analyst NÃO publica ------------------

    [Fact]
    public void Publish_RequiresManagerOrTenantAdmin_AnalystNotAllowed()
    {
        var method = typeof(PostureSnapshotsController).GetMethod(nameof(PostureSnapshotsController.Publish))!;
        var authorize = method.GetCustomAttributes<AuthorizeAttribute>(true).ToList();
        authorize.Should().ContainSingle("a publicação é gated por papel do tenant");

        var roles = (authorize[0].Roles ?? "").Split(',').Select(r => r.Trim()).ToList();
        roles.Should().BeEquivalentTo(new[] { "Manager", "TenantAdmin" });
        roles.Should().NotContain("Analyst", "um Analyst pode LER o histórico, mas não publicar");
        roles.Should().NotContain("PlatformAdmin", "não amplia autoridade de plataforma");
    }

    // ---- infraestrutura do teste -------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private static IPostureSnapshotService ServiceFor(AegisScoreDbContext db, Guid? tenantId) =>
        new PostureSnapshotService(db, new SystemTenantContext(tenantId), new NistSignalMapper(db));

    /// <summary>Semeia um framework ATIVO com 3 subcategorias (PR.AA-01/02/03) — reference data global.</summary>
    private static async Task SeedActiveFrameworkAsync(AegisScoreDbContext db)
    {
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };
        var fn = new NistFunction { FrameworkVersionId = fv.Id, Code = "PR", Name = "PROTECT (PR)" };
        var cat = new NistCategory { Function = fn, Code = "PR.AA", Name = "Identity Management" };
        db.AddRange(fv, fn, cat);
        db.Subcategories.AddRange(
            new NistSubcategory { Category = cat, Code = "PR.AA-01", MaxScorePoints = 20 },
            new NistSubcategory { Category = cat, Code = "PR.AA-02", MaxScorePoints = 10 },
            new NistSubcategory { Category = cat, Code = "PR.AA-03", MaxScorePoints = 10 });
        await db.SaveChangesAsync();
    }

    /// <summary>Framework ativo + ledger: 01=Compliant(20/20), 02=NonCompliant(0/10), 03=SEM estado (não avaliado).</summary>
    private static async Task SeedDefaultLedgerAsync(AegisScoreDbContext db)
    {
        await SeedActiveFrameworkAsync(db);
        var byCode = await db.Subcategories.ToDictionaryAsync(s => s.Code, s => s.Id);

        db.TenantControlStates.AddRange(
            new TenantControlState
            {
                SubcategoryId = byCode["PR.AA-01"], Status = ControlStatus.Compliant, CurrentScore = 20,
                LastVerdictSource = VerdictSource.Telemetry, LastEvaluatedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            },
            new TenantControlState
            {
                SubcategoryId = byCode["PR.AA-02"], Status = ControlStatus.NonCompliant, CurrentScore = 0,
                LastVerdictSource = VerdictSource.Documentary, LastEvaluatedAt = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
            });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Semeia telemetria para PR.AA-01: um conector, um SignalMapping (percent higher-is-better) e DOIS sinais —
    /// antigo (50% → NonCompliant) e novo (95% → Compliant) — mais o estado do ledger (Compliant/Telemetry).
    /// A proveniência deve referenciar SÓ o sinal novo (o que determinou o veredito vigente).
    /// </summary>
    private static async Task SeedTelemetryProvenanceAsync(AegisScoreDbContext db)
    {
        var fvId = await db.FrameworkVersions.Where(f => f.IsActive).Select(f => f.Id).SingleAsync();
        db.SignalMappings.Add(new SignalMapping
        {
            FrameworkVersionId = fvId, Capability = ConnectorCapability.Siem, SignalKey = "mfa.coverage",
            SubcategoryCodes = new() { "PR.AA-01" }, ScoringHint = "percent.higherIsBetter.v1",
        });
        // ConnectorConfig tem FK obrigatória para Tenant — semeia a linha do tenant (não é ITenantOwned).
        db.Tenants.Add(new Tenant { Id = TenantA, Name = "Tenant A", Slug = "tenant-a", Status = TenantStatus.Active });
        var connector = new ConnectorConfig
        {
            Provider = ConnectorProvider.Generic, Capability = ConnectorCapability.Siem, DisplayName = "Generic SIEM",
        };
        db.Connectors.Add(connector);
        await db.SaveChangesAsync();

        db.Signals.AddRange(
            new EvidenceSignal
            {
                ConnectorConfigId = connector.Id, SignalKey = "mfa.coverage", NumericValue = 50, Unit = "percent",
                Source = "Generic SIEM", ExternalEventId = "evt-old", MappedSubcategoryCodes = new() { "PR.AA-01" },
                CollectedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            },
            new EvidenceSignal
            {
                ConnectorConfigId = connector.Id, SignalKey = "mfa.coverage", NumericValue = 95, Unit = "percent",
                Source = "Generic SIEM", ExternalEventId = "evt-new", MappedSubcategoryCodes = new() { "PR.AA-01" },
                CollectedAt = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero),
            });

        var subId = await db.Subcategories.Where(s => s.Code == "PR.AA-01").Select(s => s.Id).SingleAsync();
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = subId, Status = ControlStatus.Compliant, CurrentScore = 20,
            LastVerdictSource = VerdictSource.Telemetry, LastEvaluatedAt = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Semeia um assessment KNIGHT concluído (fonte Entra) com dois indicadores — 1 Passed, 1 Exposed.</summary>
    private static async Task SeedKnightRunAsync(AegisScoreDbContext db)
    {
        var run = new KnightAssessmentRun
        {
            Mode = KnightAssessmentMode.Live, SourceType = KnightSourceType.MicrosoftEntraId,
            SourceState = KnightSourceState.Completed, Source = "Microsoft Entra ID", Status = KnightRunStatus.Completed,
            CatalogVersion = "ak-knight-v2", ScoreFormulaVersion = "knight-score-v1",
            StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            Score = 58, Coverage = 100, PassedCount = 1, ExposedCount = 1,
        };
        run.Indicators.Add(new KnightIndicatorResult
        {
            IndicatorId = "AK-ENTRA-001", Title = "Contas privilegiadas sem MFA — café ☕",
            Category = KnightIndicatorCategory.PrivilegedAccess, Severity = SeverityLevel.Critical,
            Status = KnightIndicatorStatus.Exposed, Evidence = "2 de 5 sem MFA · 中文.", AffectedObjectCount = 2,
            NistCodes = new() { "PR.AA-01" }, MitreTechniques = new() { "T1078" },
            SourceType = KnightSourceType.MicrosoftEntraId, CollectedAt = DateTimeOffset.UtcNow,
        });
        run.Indicators.Add(new KnightIndicatorResult
        {
            IndicatorId = "AK-ENTRA-003", Title = "Contas privilegiadas com mailbox",
            Category = KnightIndicatorCategory.PrivilegedAccess, Severity = SeverityLevel.Medium,
            Status = KnightIndicatorStatus.Passed, Evidence = "Nenhuma.", AffectedObjectCount = 0,
            SourceType = KnightSourceType.MicrosoftEntraId, CollectedAt = DateTimeOffset.UtcNow,
        });
        db.KnightAssessmentRuns.Add(run);
        await db.SaveChangesAsync();
    }

    /// <summary>Uma fotografia em memória, fixa, para exercitar o hasher de forma pura (sem EF).</summary>
    private static PostureSnapshot SampleSnapshot() => new()
    {
        TenantId = TenantA,
        Type = PostureSnapshotType.AegisScoreNist,
        SchemaVersion = "posture-snapshot-v1",
        FormulaVersion = "aegis-score-v1",
        CatalogVersion = "NIST CSF 2.0",
        SemanticFamily = "aegis-nist:NIST CSF 2.0",
        CapturedAt = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero),
        Score = 66.7,
        AchievedPoints = 20,
        PossiblePoints = 30,
        EligiblePoints = 40,
        Coverage = 75,
        EvaluatedItems = 2,
        EligibleItems = 3,
        CompliantCount = 1,
        NonCompliantCount = 1,
        NotEvaluatedCount = 1,
        DataRecency = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
        Controls =
        {
            new PostureSnapshotControl
            {
                SubcategoryCode = "PR.AA-01", FunctionCode = "PR", Evaluated = true, Status = ControlStatus.Compliant,
                AchievedPoints = 20, MaxPoints = 20, VerdictSource = VerdictSource.Telemetry,
                EvaluatedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                EvidenceRefs = { new PostureEvidenceRef("telemetry", "Generic SIEM", "evt-1", new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)) },
            },
            new PostureSnapshotControl
            {
                SubcategoryCode = "PR.AA-03", FunctionCode = "PR", Evaluated = false, MaxPoints = 10,
            },
        },
        Indicators =
        {
            new PostureSnapshotIndicator
            {
                IndicatorId = "AK-ENTRA-001", Title = "Privilegiadas sem MFA", Evidence = "2 de 5 · 中文",
                Category = KnightIndicatorCategory.PrivilegedAccess, Severity = SeverityLevel.Critical,
                Status = KnightIndicatorStatus.Exposed, AffectedObjectCount = 2,
                NistCodes = { "PR.AA-01" }, MitreTechniques = { "T1078" },
                SourceType = KnightSourceType.MicrosoftEntraId, CollectedAt = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero),
            },
        },
    };
}
