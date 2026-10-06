using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Nist;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Nist;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Reference;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Nist;

/// <summary>
/// [AEGIS-NIST-JOURNEY-01] Jornada persistida do AEGIS NIST no serviço (SQLite in-memory, catálogo NIST real):
///   • avaliação e escopo criados; cada função traz TODAS as categorias/subcategorias do catálogo, avaliadas ou não;
///   • a avaliação humana sobrevive a um contexto novo (recarregamento) com autor, data e versão;
///   • ausência de nível nunca vira zero e a lacuna sem atual E alvo fica indeterminada;
///   • escala 1–5, "não se aplica" com justificativa, concorrência pela versão;
///   • nada atravessa tenants (avaliação, escopo, documento, execução do KNIGHT);
///   • evidência do KNIGHT só com mapeamento explícito e sem mudar nível; documento e inventário com procedência;
///   • a sugestão da IA nunca é gravada, e a jornada funciona com a IA desativada ou falhando.
/// </summary>
public sealed class NistAssessmentServiceTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0151-0151-0151-0000000000a1");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0151-0151-0151-0000000000b1");
    private static readonly RemediationActor Gestor = new(Guid.Parse("11111111-0151-0151-0151-000000000001"), "Gestora Demo");
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");

    private readonly SqliteConnection _connection;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private static readonly ControlLanguageCatalog Language = new(
        Path.Combine(DataDir, "aegis_control_language.pt-BR.json"), NullLogger<ControlLanguageCatalog>.Instance);

    public NistAssessmentServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        FrameworkSeeder.SeedAsync(ctx, Path.Combine(DataDir, "nist_csf_2_0_catalog.json"), Path.Combine(DataDir, "aegis_methodology.json"))
            .GetAwaiter().GetResult();
        foreach (var t in new[] { TenantA, TenantB })
            ctx.Tenants.Add(new Tenant { Id = t, Name = "Cliente Demo", Slug = $"t-{t:N}", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private NistAssessmentService Service(AegisScoreDbContext db, Guid tenant, IAiAssessmentService? ai = null, AiMode mode = AiMode.Simulated) =>
        new(db, new SystemTenantContext(tenant), _clock, Language, ai ?? new StubAssessmentService(), new FixedGate(mode), new FixedSlug());

    private static SaveNistEvaluationCommand Eval(int? current = null, int? target = null, int version = 0, bool na = false,
        string? rationale = null, string? owner = null, string? gaps = null) =>
        new(current, target, na, null, null, rationale, gaps, null, null, owner, version);

    private async Task<(Guid AssessmentId, Guid CycleId, Guid ScopeId)> CreateAsync(Guid tenant, string name = "Avaliação NIST 2026")
    {
        await using var db = NewContext(tenant);
        var created = await Service(db, tenant).CreateAsync(
            new CreateNistAssessmentCommand(name, "Diagnóstico organizacional", new DateOnly(2026, 10, 1), null,
                "Matriz e operação em nuvem", "Inclui a matriz e o ambiente Microsoft 365."), Gestor);
        return (created.Id, created.Cycles!.Single().Id, created.Scopes.Single().Id);
    }

    [Fact]
    public async Task Jornada_CriaAvaliacao_NavegaFuncoes_GravaESobreviveAoRecarregamento()
    {
        var (a, cy, s) = await CreateAsync(TenantA);

        await using (var db = NewContext(TenantA))
        {
            var svc = Service(db, TenantA);
            var all = new[] { "GV", "ID", "PR", "DE", "RS", "RC" };
            var total = 0;
            foreach (var fn in all)
            {
                var view = await svc.GetFunctionAsync(a, cy, s, fn.ToLowerInvariant());
                view.Code.Should().Be(fn);
                view.Categories.Should().NotBeEmpty();
                view.Categories.SelectMany(c => c.Subcategories).Should().OnlyContain(r => r.State == NistSubcategoryStates.NotEvaluated);
                view.Profile.Current.Should().BeNull("sem nenhum nível a média é ausente, não zero");
                view.Profile.Gap.Should().BeNull();
                total += view.Categories.Sum(c => c.Subcategories.Count);
            }
            total.Should().Be(106, "cada função traz o catálogo completo, inclusive o que ainda não foi avaliado");

            var gv = await svc.GetFunctionAsync(a, cy, s, "GV");
            gv.Categories.Select(c => c.Code).Should().Equal(new[] { "GV.OC", "GV.RM", "GV.RR", "GV.PO", "GV.OV", "GV.SC" },
                "Govern não se resume à biblioteca de documentos, e as categorias seguem a ordem oficial do CSF 2.0");
            gv.Categories.First(c => c.Code == "GV.OC").Subcategories.First().Title.Should().Be("Alinhar a segurança à missão da organização");

            var saved = await svc.SaveEvaluationAsync(a, cy, s, "gv.oc-01", Eval(2, 4, owner: "Diretoria de Riscos", gaps: "Sem registro formal da missão."), Gestor);
            saved.Evaluation!.Version.Should().Be(1);
            saved.Evaluation.Gap.Should().Be(2);
        }

        // Recarregamento: contexto NOVO, nada em memória.
        await using (var db = NewContext(TenantA))
        {
            var detail = await Service(db, TenantA).GetSubcategoryAsync(a, cy, s, "GV.OC-01");
            detail.Title.Should().Be("Alinhar a segurança à missão da organização");
            detail.OfficialOutcome.Should().StartWith("The organizational mission is understood");
            detail.MaturityScale.Should().HaveCount(5);
            var e = detail.Evaluation!;
            e.State.Should().Be(NistSubcategoryStates.Evaluated);
            e.CurrentLevel.Should().Be(2);
            e.TargetLevel.Should().Be(4);
            e.Gap.Should().Be(2);
            e.OwnerName.Should().Be("Diretoria de Riscos");
            e.Gaps.Should().Be("Sem registro formal da missão.");
            e.EvaluatedBy.Should().Be("Analyst");
            e.ReviewedByName.Should().Be("Gestora Demo");
            e.ReviewedAt.Should().Be(_clock.GetUtcNow());

            var stored = await db.Evaluations.AsNoTracking().SingleAsync();
            stored.TenantId.Should().Be(TenantA, "a avaliação carrega o próprio tenant");
            (await db.Assessments.AsNoTracking().SingleAsync()).Status.Should().Be(AssessmentStatus.InProgress);

            var list = await Service(db, TenantA).ListAsync();
            list.Single().Scopes.Single().Evaluated.Should().Be(1);
            list.Single().MethodologyVersion.Should().Be(AssessmentMethodology.Version);
        }
    }

    [Fact]
    public async Task AusenciaNaoEhZero_LacunaIndeterminada_ENaoSeAplicaForaDasMedias()
    {
        var (a, cy, s) = await CreateAsync(TenantA);
        await using var db = NewContext(TenantA);
        var svc = Service(db, TenantA);

        await svc.SaveEvaluationAsync(a, cy, s, "PR.AA-01", Eval(current: 2), Gestor);                 // sem alvo
        await svc.SaveEvaluationAsync(a, cy, s, "PR.AA-02", Eval(target: 4), Gestor);                  // sem atual
        await svc.SaveEvaluationAsync(a, cy, s, "PR.AA-03", Eval(3, 5), Gestor);                       // par completo
        await svc.SaveEvaluationAsync(a, cy, s, "PR.AA-04", Eval(na: true, rationale: "Sem federação de identidade no escopo."), Gestor);

        var pr = await svc.GetFunctionAsync(a, cy, s, "PR");
        var aa = pr.Categories.First(c => c.Code == "PR.AA");
        aa.Subcategories.First(r => r.Code == "PR.AA-01").Gap.Should().BeNull("sem alvo a lacuna é indeterminada");
        aa.Subcategories.First(r => r.Code == "PR.AA-02").State.Should().Be(NistSubcategoryStates.InProgress, "alvo sem situação atual não é avaliação");
        aa.Subcategories.First(r => r.Code == "PR.AA-04").State.Should().Be(NistSubcategoryStates.NotApplicable);

        aa.Profile.Current.Should().Be(2.5, "média só dos níveis registrados: (2 + 3) / 2");
        aa.Profile.Target.Should().Be(4.5, "média só dos alvos registrados: (4 + 5) / 2");
        aa.Profile.Gap.Should().Be(2, "a lacuna usa só a subcategoria com os dois níveis, nunca 4,5 − 2,5");
        (aa.Profile.WithCurrent, aa.Profile.WithTarget, aa.Profile.WithGap, aa.Profile.NotApplicable).Should().Be((2, 2, 1, 1));

        // Categoria sem nenhuma nota NÃO puxa a função para baixo (no cálculo antigo valeria 0).
        pr.Profile.Current.Should().Be(2.5);
        pr.Categories.Where(c => c.Code != "PR.AA").Should().OnlyContain(c => c.Profile.Current == null);

        var profile = await svc.GetProfileAsync(a, cy, s);
        profile.Gaps.Should().ContainSingle().Which.Code.Should().Be("PR.AA-03");
        profile.IndeterminateGaps.Should().Be(106 - 1 - 1, "sem par atual/alvo e aplicável: todas menos a completa e a não aplicável");
        profile.Overall.Current.Should().Be(2.5);
        profile.Functions.Single(f => f.Code == "GV").Current.Should().BeNull();
    }

    [Fact]
    public async Task Validacao_Escala_NaoSeAplica_ConteudoVazio_NaoGravaNada()
    {
        var (a, cy, s) = await CreateAsync(TenantA);
        await using var db = NewContext(TenantA);
        var svc = Service(db, TenantA);

        var invalid = new (SaveNistEvaluationCommand Cmd, string Message)[]
        {
            (Eval(0, 3), "*1 a 5*"),
            (Eval(6, 3), "*1 a 5*"),
            (Eval(2, 9), "*alvo*1 a 5*"),
            (Eval(2, null, na: true, rationale: "Não se aplica ao escopo avaliado."), "*não recebe*"),
            (Eval(na: true, rationale: "curta"), "*justificativa*"),
            (Eval(), "*Nada a registrar*"),
        };
        foreach (var (cmd, message) in invalid)
            await FluentActions.Awaiting(() => svc.SaveEvaluationAsync(a, cy, s, "DE.CM-01", cmd, Gestor))
                .Should().ThrowAsync<NistAssessmentValidationException>().WithMessage(message);

        await FluentActions.Awaiting(() => svc.SaveEvaluationAsync(a, cy, s, "XX.YY-99", Eval(2, 3), Gestor))
            .Should().ThrowAsync<NistAssessmentNotFoundException>();
        (await db.Evaluations.CountAsync()).Should().Be(0, "pedido recusado não grava nada");
    }

    [Fact]
    public async Task Concorrencia_VersaoDesatualizada_EhRecusada()
    {
        var (a, cy, s) = await CreateAsync(TenantA);

        await using (var db = NewContext(TenantA))
            (await Service(db, TenantA).SaveEvaluationAsync(a, cy, s, "RS.MA-01", Eval(2, 4), Gestor)).Evaluation!.Version.Should().Be(1);

        // Duas pessoas leram a versão 1; a primeira grava, a segunda é recusada.
        await using (var db = NewContext(TenantA))
            (await Service(db, TenantA).SaveEvaluationAsync(a, cy, s, "RS.MA-01", Eval(3, 4, version: 1), Gestor)).Evaluation!.Version.Should().Be(2);
        await using (var db = NewContext(TenantA))
        {
            await FluentActions.Awaiting(() => Service(db, TenantA).SaveEvaluationAsync(a, cy, s, "RS.MA-01", Eval(1, 4, version: 1), Gestor))
                .Should().ThrowAsync<NistAssessmentConflictException>().WithMessage("*outra pessoa*");
            // Criação "nova" sobre uma avaliação que já existe também é conflito.
            await FluentActions.Awaiting(() => Service(db, TenantA).SaveEvaluationAsync(a, cy, s, "RS.MA-01", Eval(1, 4, version: 0), Gestor))
                .Should().ThrowAsync<NistAssessmentConflictException>();
            (await db.Evaluations.AsNoTracking().SingleAsync()).CurrentLevel.Should().Be(3, "a gravação vencida não sobrescreve");
        }
    }

    [Fact]
    public async Task IsolamentoEntreTenants_AvaliacaoEscopoDocumentoEKnight()
    {
        var (a, cy, s) = await CreateAsync(TenantA);
        var (a2, cy2, s2) = await CreateAsync(TenantA, "Segunda avaliação");
        Guid docA, runA;
        await using (var db = NewContext(TenantA))
        {
            docA = await SeedDocumentAsync(db, "GV.PO-01");
            runA = await SeedKnightRunAsync(db, KnightAssessmentMode.Live, ("AK-ENTRA-001", KnightIndicatorStatus.Exposed, new[] { "PR.AA-01" }));
            await Service(db, TenantA).SaveEvaluationAsync(a, cy, s, "GV.PO-01", Eval(2, 3), Gestor);
        }

        await using (var db = NewContext(TenantB))
        {
            var svc = Service(db, TenantB);
            (await svc.ListAsync()).Should().BeEmpty("o tenant B não enxerga a avaliação do tenant A");
            await FluentActions.Awaiting(() => svc.GetAsync(a)).Should().ThrowAsync<NistAssessmentNotFoundException>();
            await FluentActions.Awaiting(() => svc.GetSubcategoryAsync(a, cy, s, "GV.PO-01")).Should().ThrowAsync<NistAssessmentNotFoundException>();
            await FluentActions.Awaiting(() => svc.SaveEvaluationAsync(a, cy, s, "GV.PO-01", Eval(5, 5, version: 1), Gestor))
                .Should().ThrowAsync<NistAssessmentNotFoundException>();
            await FluentActions.Awaiting(() => svc.AddScopeAsync(a, new CreateNistScopeCommand("Intruso", null), Gestor))
                .Should().ThrowAsync<NistAssessmentNotFoundException>();

            var (b, cyb, sb) = await CreateAsync(TenantB);
            await FluentActions.Awaiting(() => Service(db, TenantB).LinkEvidenceAsync(b, cyb, sb, "GV.PO-01",
                    new LinkNistEvidenceCommand("GovernanceDocument", docA, null, null, null, null, null, null, null), Gestor))
                .Should().ThrowAsync<NistAssessmentNotFoundException>("documento de outro tenant não existe para B");
            await FluentActions.Awaiting(() => Service(db, TenantB).LinkEvidenceAsync(b, cyb, sb, "PR.AA-01",
                    new LinkNistEvidenceCommand("KnightIndicator", null, runA, "AK-ENTRA-001", null, null, null, null, null), Gestor))
                .Should().ThrowAsync<NistAssessmentNotFoundException>("execução do KNIGHT de outro tenant não existe para B");
            (await Service(db, TenantB).GetSubcategoryAsync(b, cyb, sb, "PR.AA-01")).AvailableEvidence
                .Should().BeEmpty("a evidência disponível também é filtrada pelo tenant");
        }

        await using (var db = NewContext(TenantA))
        {
            await FluentActions.Awaiting(() => Service(db, TenantA).GetSubcategoryAsync(a2, cy2, s, "GV.PO-01"))
                .Should().ThrowAsync<NistAssessmentNotFoundException>("o escopo precisa pertencer à avaliação informada");
            (await db.Evaluations.AsNoTracking().SingleAsync()).CurrentLevel.Should().Be(2, "nada do tenant B alterou a avaliação de A");
        }
    }

    [Fact]
    public async Task EvidenciaKnight_SoComMapeamentoExplicito_ENaoAlteraNivel()
    {
        var (a, cy, s) = await CreateAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await SeedKnightRunAsync(db, KnightAssessmentMode.Live,
            ("AK-ENTRA-001", KnightIndicatorStatus.Passed, new[] { "PR.AA-01", "PR.AA-03" }),
            ("AK-ENTRA-002", KnightIndicatorStatus.NotEvaluated, new[] { "PR.AA-01" }));
        var svc = Service(db, TenantA);

        var detail = await svc.GetSubcategoryAsync(a, cy, s, "PR.AA-01");
        detail.AvailableEvidence.Should().HaveCount(2);
        var offered = detail.AvailableEvidence.Single(x => x.KnightIndicatorId == "AK-ENTRA-001");
        offered.Criterion.Should().Contain("Mapeamento explícito");
        offered.Limitation.Should().Contain("não comprova sozinha");
        offered.IsDemo.Should().BeFalse();

        var linked = await svc.LinkEvidenceAsync(a, cy, s, "PR.AA-01",
            new LinkNistEvidenceCommand("KnightIndicator", null, run, "AK-ENTRA-001", null, null, "Acesso condicional aplicado.", null, null), Gestor);
        linked.Evaluation.Should().BeNull("vincular evidência técnica não define nível nem conformidade");
        var ev = linked.Evidence.Single();
        ev.OriginKind.Should().Be("KnightIndicator");
        ev.OriginRef.Should().Be($"{run}/AK-ENTRA-001");
        ev.OriginScope.Should().Contain("coleta real").And.Contain("ak-knight-test");
        ev.CollectedAt.Should().Be(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero), "a data é a da coleta, não a do vínculo");
        ev.Notes.Should().Contain("não comprova sozinha");
        linked.AvailableEvidence.Single(x => x.KnightIndicatorId == "AK-ENTRA-001").AlreadyLinked.Should().BeTrue();

        await FluentActions.Awaiting(() => svc.LinkEvidenceAsync(a, cy, s, "GV.OC-01",
                new LinkNistEvidenceCommand("KnightIndicator", null, run, "AK-ENTRA-001", null, null, null, null, null), Gestor))
            .Should().ThrowAsync<NistAssessmentValidationException>().WithMessage("*não está mapeado*");
        await FluentActions.Awaiting(() => svc.LinkEvidenceAsync(a, cy, s, "PR.AA-01",
                new LinkNistEvidenceCommand("KnightIndicator", null, run, "AK-ENTRA-002", null, null, null, null, null), Gestor))
            .Should().ThrowAsync<NistAssessmentValidationException>().WithMessage("*não tem resultado avaliado*");
        await FluentActions.Awaiting(() => svc.LinkEvidenceAsync(a, cy, s, "PR.AA-01",
                new LinkNistEvidenceCommand("KnightIndicator", null, run, "AK-ENTRA-001", null, null, null, null, null), Gestor))
            .Should().ThrowAsync<NistAssessmentConflictException>("o mesmo controle não entra duas vezes");
    }

    [Fact]
    public async Task EvidenciaDocumentalEInventario_ComProcedencia_ERetiradaPreservaRegistro()
    {
        var (a, cy, s) = await CreateAsync(TenantA);
        await using var db = NewContext(TenantA);
        var doc = await SeedDocumentAsync(db, "GV.PO-01");
        db.Assets.Add(new Asset { Name = "srv-demo-01", Category = AssetCategory.Hardware, DiscoverySource = AssetDiscoverySource.Connector });
        await db.SaveChangesAsync();
        var svc = Service(db, TenantA);

        var detail = await svc.GetSubcategoryAsync(a, cy, s, "GV.PO-01");
        detail.AvailableEvidence.Should().ContainSingle(x => x.DocumentId == doc).Which.Criterion.Should().Contain("Trecho literal");

        var linked = await svc.LinkEvidenceAsync(a, cy, s, "GV.PO-01",
            new LinkNistEvidenceCommand("GovernanceDocument", doc, null, null, null, null, "Política aprovada pela diretoria.", null, null), Gestor);
        var ev = linked.Evidence.Single();
        (ev.Title, ev.OriginRef, ev.RecordedByName).Should().Be(("Politica de Seguranca da Informacao", doc.ToString(), "Gestora Demo"));
        ev.CollectedAt.Should().Be(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "a data é a do documento");
        (await svc.GetFunctionAsync(a, cy, s, "GV")).Categories.SelectMany(c => c.Subcategories).Single(r => r.Code == "GV.PO-01")
            .State.Should().Be(NistSubcategoryStates.InProgress, "evidência sem nível é avaliação em andamento, não avaliação concluída");

        var manual = await svc.LinkEvidenceAsync(a, cy, s, "GV.PO-01",
            new LinkNistEvidenceCommand("Manual", null, null, null, "Entrevista com a diretoria", null, "Confirma a revisão anual.", new DateOnly(2026, 9, 20), "Interview"), Gestor);
        manual.Evidence.Should().HaveCount(2);
        await FluentActions.Awaiting(() => svc.LinkEvidenceAsync(a, cy, s, "GV.PO-01",
                new LinkNistEvidenceCommand("Manual", null, null, null, "Link", "javascript:alert(1)", null, null, null), Gestor))
            .Should().ThrowAsync<NistAssessmentValidationException>().WithMessage("*http(s)*");

        await FluentActions.Awaiting(() => svc.LinkEvidenceAsync(a, cy, s, "GV.PO-01",
                new LinkNistEvidenceCommand("AssetInventory", null, null, null, null, null, null, null, null), Gestor))
            .Should().ThrowAsync<NistAssessmentValidationException>().WithMessage("*ID.AM*");
        var inventory = await svc.LinkEvidenceAsync(a, cy, s, "ID.AM-01",
            new LinkNistEvidenceCommand("AssetInventory", null, null, null, null, null, null, null, null), Gestor);
        inventory.Evidence.Single().OriginScope.Should().Contain("1 ativo(s) ativo(s)").And.Contain("conector 1");

        var removed = await svc.RemoveEvidenceAsync(a, cy, s, "GV.PO-01", ev.Id, Gestor);
        removed.Evidence.Should().ContainSingle().Which.Title.Should().Be("Entrevista com a diretoria");
        var row = await db.Evidence.AsNoTracking().SingleAsync(x => x.Id == ev.Id);
        row.RemovedAt.Should().NotBeNull("retirar o vínculo preserva o registro para auditoria");
        row.RemovedByName.Should().Be("Gestora Demo");
    }

    [Fact]
    public async Task SugestaoDaIA_NuncaGravada_EJornadaFuncionaSemIA()
    {
        var (a, cy, s) = await CreateAsync(TenantA);
        await using var db = NewContext(TenantA);

        var suggestion = await Service(db, TenantA).SuggestAsync(a, cy, s, "GV.RM-01");
        suggestion.Simulated.Should().BeTrue("sem provedor configurado a sugestão é simulada e assim declarada");
        suggestion.SuggestedCurrentLevel.Should().BeInRange(1, 5);
        (await db.Evaluations.CountAsync()).Should().Be(0, "a sugestão nunca é gravada como avaliação ou revisão humana");

        await FluentActions.Awaiting(() => Service(db, TenantA, mode: AiMode.Disabled).SuggestAsync(a, cy, s, "GV.RM-01"))
            .Should().ThrowAsync<NistAiUnavailableException>();
        await FluentActions.Awaiting(() => Service(db, TenantA, ai: new FailingAi()).SuggestAsync(a, cy, s, "GV.RM-01"))
            .Should().ThrowAsync<NistAiUnavailableException>();

        // Com a IA desativada, a avaliação humana segue normalmente.
        var saved = await Service(db, TenantA, mode: AiMode.Disabled).SaveEvaluationAsync(a, cy, s, "GV.RM-01", Eval(2, 3), Gestor);
        saved.Evaluation!.EvaluatedBy.Should().Be("Analyst");
    }

    [Fact]
    public async Task Historico_UmPontoPorRodadaEEscopo_NoMesDoFimDoPeriodo()
    {
        var (a, cy, s) = await CreateAsync(TenantA);
        await using var db = NewContext(TenantA);
        var svc = Service(db, TenantA);
        (await svc.HistoryAsync()).Should().BeEmpty("escopo sem revisão humana não representa mês nenhum");

        await svc.SaveEvaluationAsync(a, cy, s, "ID.AM-01", Eval(2, 4), Gestor);
        _clock.Advance(TimeSpan.FromDays(31));
        await svc.SaveEvaluationAsync(a, cy, s, "ID.AM-02", Eval(3, 4), Gestor);

        var item = (await svc.HistoryAsync()).Single();
        // [AEGIS-NIST-JOURNEY-02] O ponto é da RODADA: mês do fim do período (aqui, a rodada inicial de 01/10/2026), não do
        // dia em que alguém gravou — revisões feitas em novembro continuam pertencendo à rodada de outubro.
        item.ReferenceMonth.Should().Be(new DateOnly(2026, 10, 1));
        item.CycleId.Should().Be(cy);
        item.LastReviewedAt.Should().Be(_clock.GetUtcNow());
        item.MethodologyVersion.Should().Be(AssessmentMethodology.Version);
        item.Evaluated.Should().Be(2);
        item.Current.Should().Be(2.5);
        item.Target.Should().Be(4.0);
    }

    // ---- Apoio ---------------------------------------------------------------------------------------

    private static async Task<Guid> SeedDocumentAsync(AegisScoreDbContext db, string code)
    {
        var doc = new GovernanceDocument
        {
            Title = "Politica de Seguranca da Informacao", Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            FileName = "politica.pdf", DocumentDate = new DateOnly(2026, 3, 1), Status = GovernanceStatus.Vigente,
        };
        db.GovernanceDocuments.Add(doc);
        db.DocumentControlMappings.Add(new DocumentControlMapping
        {
            GovernanceDocumentId = doc.Id, SubcategoryCode = code, Confidence = 0.9, EvidenceQuote = "A política é revisada anualmente.",
        });
        await db.SaveChangesAsync();
        return doc.Id;
    }

    private static async Task<Guid> SeedKnightRunAsync(AegisScoreDbContext db, KnightAssessmentMode mode,
        params (string Id, KnightIndicatorStatus Status, string[] Codes)[] indicators)
    {
        var completed = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
        var run = new KnightAssessmentRun
        {
            Mode = mode, SourceType = KnightSourceType.MicrosoftEntraId, SourceState = KnightSourceState.Completed,
            Source = "Microsoft Entra ID", Status = KnightRunStatus.Completed, CatalogVersion = "ak-knight-test",
            ScoreFormulaVersion = "knight-score-v1", StartedAt = completed.AddMinutes(-5), CompletedAt = completed, Score = 50, Coverage = 80,
        };
        db.KnightAssessmentRuns.Add(run);
        foreach (var (id, status, codes) in indicators)
            db.KnightIndicatorResults.Add(new KnightIndicatorResult
            {
                RunId = run.Id, IndicatorId = id, Title = $"Controle {id}", Status = status, Severity = SeverityLevel.High,
                NistCodes = codes.ToList(), SourceType = KnightSourceType.MicrosoftEntraId, CollectedAt = completed,
            });
        await db.SaveChangesAsync();
        return run.Id;
    }

    private sealed class FixedGate : IAiFreeTierGate
    {
        public FixedGate(AiMode mode) => Mode = mode;
        public AiMode Mode { get; }
        public bool ProviderConfigured => false;
        public bool IsExternalAllowedForSlug(string? tenantSlug) => false;
    }

    private sealed class FixedSlug : IAiTenantResolver
    {
        public void OverrideTenant(Guid tenantId) { }
        public Task<string?> GetCurrentSlugAsync(CancellationToken ct = default) => Task.FromResult<string?>("demo");
    }

    /// <summary>IA que falha na sugestão (provedor fora do ar); o resto delega ao simulado.</summary>
    private sealed class FailingAi : IAiAssessmentService
    {
        private readonly StubAssessmentService _stub = new();
        public Task<MaturitySuggestion> SuggestMaturityAsync(MaturitySuggestionRequest request, CancellationToken ct) =>
            throw new System.Net.Http.HttpRequestException("provedor indisponível");
        public Task<DocumentAnalysis> AnalyzeDocumentAsync(DocumentAnalysisRequest request, CancellationToken ct) => _stub.AnalyzeDocumentAsync(request, ct);
        public Task<DocumentControlVerdict> EvaluateDocumentControlAsync(DocumentControlEvaluationRequest request, CancellationToken ct) =>
            _stub.EvaluateDocumentControlAsync(request, ct);
        public Task<InterviewTurn> ConductInterviewTurnAsync(InterviewContext context, CancellationToken ct) => _stub.ConductInterviewTurnAsync(context, ct);
        public Task<IReadOnlyList<ActionPlanSuggestion>> GenerateActionPlanAsync(ActionPlanRequest request, CancellationToken ct) =>
            _stub.GenerateActionPlanAsync(request, ct);
        public Task<string> GenerateExecutiveReportAsync(ExecutiveReportRequest request, CancellationToken ct) => _stub.GenerateExecutiveReportAsync(request, ct);
        public Task<IReadOnlyList<NormalizedSignal>> NormalizeSignalsAsync(RawSignalBatch batch, CancellationToken ct) => _stub.NormalizeSignalsAsync(batch, ct);
        public Task<AuditorReply> ChatAsync(AuditorChatRequest request, CancellationToken ct) => _stub.ChatAsync(request, ct);
        public Task<AdvisoryDraft> GenerateAdvisoryAsync(AdvisoryGenerationRequest request, CancellationToken ct) => _stub.GenerateAdvisoryAsync(request, ct);
    }
}
