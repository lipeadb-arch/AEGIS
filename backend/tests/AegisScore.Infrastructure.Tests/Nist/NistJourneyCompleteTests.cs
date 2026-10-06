using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AegisScore.Application.Nist;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Nist;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Posture.Export;
using AegisScore.Infrastructure.Reference;
using AegisScore.Infrastructure.Remediation;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Nist;

/// <summary>
/// [AEGIS-NIST-JOURNEY-02] A jornada COMPLETA do AEGIS NIST no serviço (SQLite in-memory, catálogo NIST real, dados
/// sintéticos): rodadas, procedimentos (planejado × resultado), responsáveis vinculados, revisão por outra pessoa, achados
/// com plano de origem NIST, trilha com valores anteriores/novos, publicação imutável com HTML/PDF/CSV da MESMA fotografia
/// e importação CSV com prévia. Cada teste afirma uma regra de negócio, não a forma da implementação.
/// </summary>
public sealed class NistJourneyCompleteTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0202-0202-0202-0000000000a1");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0202-0202-0202-0000000000b1");
    private static readonly Guid GestoraAccount = Guid.Parse("11111111-0202-0202-0202-000000000001");
    private static readonly Guid RevisorAccount = Guid.Parse("22222222-0202-0202-0202-000000000002");
    private static readonly RemediationActor Gestora = new(GestoraAccount, "Gestora Demo");
    private static readonly RemediationActor Revisor = new(RevisorAccount, "Revisor Demo");
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");
    private static readonly ControlLanguageCatalog Language = new(
        Path.Combine(DataDir, "aegis_control_language.pt-BR.json"), NullLogger<ControlLanguageCatalog>.Instance);

    private readonly SqliteConnection _connection;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
    private Guid _analystUser, _managerUser, _inactiveUser, _foreignUser;

    public NistJourneyCompleteTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        FrameworkSeeder.SeedAsync(ctx, Path.Combine(DataDir, "nist_csf_2_0_catalog.json"), Path.Combine(DataDir, "aegis_methodology.json"))
            .GetAwaiter().GetResult();
        foreach (var t in new[] { TenantA, TenantB })
            ctx.Tenants.Add(new Tenant { Id = t, Name = t == TenantA ? "Cliente Sintético A" : "Cliente Sintético B", Slug = $"t-{t:N}", Status = TenantStatus.Active });
        ctx.SaveChanges();
        _analystUser = AddUser(TenantA, "Analista Demo", TenantRole.Analyst, true);
        _managerUser = AddUser(TenantA, "Revisor Demo", TenantRole.Manager, true);
        _inactiveUser = AddUser(TenantA, "Pessoa Inativa", TenantRole.Analyst, false);
        _foreignUser = AddUser(TenantB, "Pessoa do Tenant B", TenantRole.Manager, true);
    }

    public void Dispose() => _connection.Dispose();

    private Guid AddUser(Guid tenant, string name, TenantRole role, bool active)
    {
        using var ctx = NewContext(tenant);
        var account = new IdentityAccount { Email = $"{Guid.NewGuid():N}@demo.example.com" };
        ctx.IdentityAccounts.Add(account);
        var user = new User { TenantId = tenant, IdentityAccountId = account.Id, DisplayName = name, Role = role, IsActive = active };
        ctx.Users.Add(user);
        ctx.SaveChanges();
        return user.Id;
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private sealed record Services(AegisScoreDbContext Db, NistAssessmentService Nist, NistWorkService Work, NistPublicationService Publication,
        NistImportService Import, RemediationService Remediation) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private Services For(Guid tenant)
    {
        var db = NewContext(tenant);
        var ctx = new SystemTenantContext(tenant);
        var remediation = new RemediationService(db, ctx, _clock,
            new AegisScore.Infrastructure.Queries.DevicePriorityQuery(db, _clock,
                Microsoft.Extensions.Options.Options.Create(new AegisScore.Application.Queries.CrossSourceCorrelationOptions())));
        return new Services(db, new NistAssessmentService(db, ctx, _clock, Language), new NistWorkService(db, ctx, _clock, remediation, Language),
            new NistPublicationService(db, ctx, _clock, Language), new NistImportService(db, ctx, _clock, Language), remediation);
    }

    private static SaveNistEvaluationCommand Eval(int? current, int? target, int version, string? gaps = null, string? rationale = null,
        bool na = false, Guid? ownerUser = null, string? owner = null) =>
        new(current, target, na, null, null, rationale, gaps, null, null, owner, version, ownerUser);

    private async Task<(Guid A, Guid C, Guid S)> CreateAsync(Guid tenant)
    {
        await using var s = For(tenant);
        var created = await s.Nist.CreateAsync(new CreateNistAssessmentCommand("Avaliação NIST 2026", null, new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30),
            "Matriz e nuvem", null, "T3 2026", "Quarterly", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30)), Gestora);
        return (created.Id, created.Cycles!.Single().Id, created.Scopes.Single().Id);
    }

    private static CreateNistFindingCommand Finding(NistPlanInput? plan = null, IReadOnlyList<Guid>? evidence = null, string title = "Inventário sem dono definido") =>
        new(title, "A planilha de ativos não indica responsável para 40% dos servidores.",
            "Ativos sem dono não recebem correção nem revisão de acesso.", "Atraso no tratamento de vulnerabilidades críticas.",
            "High", "Afeta servidores que hospedam sistemas de produção.", "High", "Pré-requisito de outros controles de identificação.",
            "Atribuir dono a cada ativo e revisar trimestralmente.", evidence, plan);

    // ===================================================================================================
    //  Rodadas
    // ===================================================================================================

    [Fact]
    public async Task NovaRodada_EmRascunho_PreservaAAnterior_NaoContaComoRevisaoHumana()
    {
        var (a, c1, s) = await CreateAsync(TenantA);
        await using (var x = For(TenantA))
        {
            await x.Nist.SaveEvaluationAsync(a, c1, s, "ID.AM-01", Eval(2, 4, 0, gaps: "Inventário parcial."), Gestora);
            await x.Nist.SaveEvaluationAsync(a, c1, s, "ID.AM-02", Eval(3, 3, 0), Gestora);
            var proc = await x.Work.AddProcedureAsync(a, c1, s, "ID.AM-01", new AddNistProcedureCommand("Examine", "Examinar a planilha de inventário de ativos."), Gestora);
            await x.Work.UpdateProcedureAsync(a, c1, s, "ID.AM-01", proc.Id, new UpdateNistProcedureCommand(null, "Performed", "Unsatisfactory",
                "40% dos servidores sem responsável na planilha.", new DateOnly(2026, 9, 10), null, proc.Version), Gestora);
        }

        // Validação do período: trimestral segue o calendário; mensal idem.
        await using (var x = For(TenantA))
        {
            await FluentActions.Awaiting(() => x.Nist.CreateCycleAsync(a, new CreateNistCycleCommand("Errada", "Quarterly", new DateOnly(2026, 10, 2), new DateOnly(2026, 12, 31), null, "None"), Gestora))
                .Should().ThrowAsync<NistAssessmentValidationException>().WithMessage("*trimestre*");
            await FluentActions.Awaiting(() => x.Nist.CreateCycleAsync(a, new CreateNistCycleCommand("t3 2026", "Monthly", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), null, "None"), Gestora))
                .Should().ThrowAsync<NistAssessmentConflictException>("o nome da rodada é único na avaliação, sem diferenciar maiúsculas");
        }

        Guid c2;
        await using (var x = For(TenantA))
        {
            var cycle = await x.Nist.CreateCycleAsync(a, new CreateNistCycleCommand("Outubro 2026", "Monthly", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), c1, "Draft"), Gestora);
            c2 = cycle.Id;
            cycle.SeedFromCycleName.Should().Be("T3 2026");
            cycle.SeedMode.Should().Be("Draft");
        }

        await using (var x = For(TenantA))
        {
            var draft = await x.Nist.GetSubcategoryAsync(a, c2, s, "ID.AM-01");
            draft.Evaluation!.State.Should().Be(NistSubcategoryStates.PendingConfirmation, "rascunho herdado não é avaliação confirmada");
            draft.Evaluation.HumanConfirmed.Should().BeFalse();
            draft.Evaluation.ReviewedAt.Should().BeNull("aproveitar a rodada anterior não é uma nova revisão humana");
            draft.Evaluation.OriginNote.Should().Contain("T3 2026").And.Contain("Gestora Demo");
            draft.Evidence.Should().BeEmpty("evidência não é herdada");
            draft.Procedures!.Should().ContainSingle().Which.Status.Should().Be("Planned", "o procedimento vem planejado, sem o resultado da rodada anterior");
            draft.Reference!.CycleName.Should().Be("T3 2026");
            draft.Reference.CurrentLevel.Should().Be(2);

            var profile = await x.Nist.GetProfileAsync(a, c2, s);
            profile.Overall.Current.Should().BeNull("rascunho não entra nas médias até ser confirmado");
            profile.States!.PendingConfirmation.Should().Be(2);

            // A rodada anterior continua exatamente como foi registrada.
            var old = await x.Nist.GetSubcategoryAsync(a, c1, s, "ID.AM-01");
            old.Evaluation!.State.Should().Be(NistSubcategoryStates.Evaluated);
            old.Procedures!.Single().Outcome.Should().Be("Unsatisfactory");
            (await x.Nist.GetProfileAsync(a, c1, s)).Overall.Current.Should().Be(2.5);

            // Confirmar na tela torna o rascunho avaliação desta rodada.
            var confirmed = await x.Nist.SaveEvaluationAsync(a, c2, s, "ID.AM-01", Eval(3, 4, draft.Evaluation.Version, gaps: "Inventário parcial."), Gestora);
            confirmed.Evaluation!.State.Should().Be(NistSubcategoryStates.Evaluated);
            (await x.Nist.GetProfileAsync(a, c2, s)).Overall.Current.Should().Be(3);
        }

        // Encerrar a rodada a torna somente leitura (a reabertura fica na trilha).
        await using (var x = For(TenantA))
        {
            var closed = await x.Nist.SetCycleStatusAsync(a, c1, new SetNistCycleStatusCommand("Closed", 1), Gestora);
            closed.Status.Should().Be("Closed");
            await FluentActions.Awaiting(() => x.Nist.SaveEvaluationAsync(a, c1, s, "ID.AM-02", Eval(4, 4, 1), Gestora))
                .Should().ThrowAsync<NistAssessmentValidationException>().WithMessage("*encerrada*");
            var reopened = await x.Nist.SetCycleStatusAsync(a, c1, new SetNistCycleStatusCommand("Open", closed.Version), Gestora);
            reopened.Status.Should().Be("Open");
            var audit = await x.Nist.AuditAsync(a, c1, null, null);
            audit.Should().Contain(e => e.Subject == "Cycle" && e.Action == "Closed").And.Contain(e => e.Subject == "Cycle" && e.Action == "Reopened");
        }
    }

    // ===================================================================================================
    //  Procedimentos, responsáveis e revisão
    // ===================================================================================================

    [Fact]
    public async Task Procedimento_PlanejadoNaoEhResultado_EResultadoExigeDataObservacaoEConclusao()
    {
        var (a, c, s) = await CreateAsync(TenantA);
        await using var x = For(TenantA);
        var p = await x.Work.AddProcedureAsync(a, c, s, "PR.AA-01", new AddNistProcedureCommand("Test", "Testar o bloqueio de conta após falhas de login."), Gestora);
        p.Status.Should().Be("Planned");
        p.Outcome.Should().BeNull();

        await FluentActions.Awaiting(() => x.Work.UpdateProcedureAsync(a, c, s, "PR.AA-01", p.Id,
                new UpdateNistProcedureCommand(null, "Planned", "Satisfactory", null, null, null, p.Version), Gestora))
            .Should().ThrowAsync<NistAssessmentValidationException>().WithMessage("*escolher o método não comprova*");
        await FluentActions.Awaiting(() => x.Work.UpdateProcedureAsync(a, c, s, "PR.AA-01", p.Id,
                new UpdateNistProcedureCommand(null, "Performed", "Satisfactory", "ok", new DateOnly(2026, 10, 1), null, p.Version), Gestora))
            .Should().ThrowAsync<NistAssessmentValidationException>().WithMessage("*observado*");

        // Evidência citada precisa ser desta subcategoria, rodada e escopo.
        var other = await x.Nist.LinkEvidenceAsync(a, c, s, "PR.AA-02", new LinkNistEvidenceCommand("Manual", null, null, null, "Captura do console", null, null, null, "Screenshot"), Gestora);
        await FluentActions.Awaiting(() => x.Work.UpdateProcedureAsync(a, c, s, "PR.AA-01", p.Id,
                new UpdateNistProcedureCommand(null, "Performed", "Satisfactory", "Conta bloqueada após 5 tentativas.", new DateOnly(2026, 10, 1),
                    new[] { other.Evidence.Single().Id }, p.Version), Gestora))
            .Should().ThrowAsync<NistAssessmentValidationException>().WithMessage("*evidências vigentes desta subcategoria*");

        var ev = await x.Nist.LinkEvidenceAsync(a, c, s, "PR.AA-01", new LinkNistEvidenceCommand("Manual", null, null, null, "Registro do teste de bloqueio", null, null, null, "Screenshot"), Gestora);
        var done = await x.Work.UpdateProcedureAsync(a, c, s, "PR.AA-01", p.Id, new UpdateNistProcedureCommand(null, "Performed", "Satisfactory",
            "Conta bloqueada após 5 tentativas, como a política define.", new DateOnly(2026, 10, 1), new[] { ev.Evidence.Single().Id }, p.Version), Revisor);
        done.ResultRecordedByName.Should().Be("Revisor Demo", "o autor do resultado vem da sessão");
        await FluentActions.Awaiting(() => x.Work.UpdateProcedureAsync(a, c, s, "PR.AA-01", p.Id,
                new UpdateNistProcedureCommand(null, "InProgress", null, null, null, null, p.Version), Gestora))
            .Should().ThrowAsync<NistAssessmentConflictException>("versão antiga não sobrescreve o resultado");

        // A evidência citada pelo procedimento não sai em silêncio.
        await FluentActions.Awaiting(() => x.Nist.RemoveEvidenceAsync(a, c, s, "PR.AA-01", ev.Evidence.Single().Id, Gestora))
            .Should().ThrowAsync<NistAssessmentConflictException>().WithMessage("*sustenta*");

        var audit = await x.Nist.AuditAsync(a, c, s, "PR.AA-01");
        var result = audit.First(e => e.Subject == "Procedure" && e.Action == "ResultRecorded");
        result.Changes.Should().Contain(ch => ch.Field == "status" && ch.From == "Planejado" && ch.To == "Realizado");
        result.Changes.Should().Contain(ch => ch.Field == "outcome" && ch.From == null && ch.To == "Satisfatório");
        result.ActorName.Should().Be("Revisor Demo");
    }

    [Fact]
    public async Task Responsaveis_UsuarioAtivoDoMesmoTenant_ExternoETextoLegado_SemConversaoAutomatica()
    {
        var (a, c, s) = await CreateAsync(TenantA);
        await using var x = For(TenantA);
        (await x.Nist.AssigneesAsync()).Select(u => u.DisplayName).Should().BeEquivalentTo(new[] { "Analista Demo", "Revisor Demo" },
            "só usuários ATIVOS deste tenant recebem trabalho");

        await FluentActions.Awaiting(() => x.Nist.AssignAsync(a, c, s, "GV.OC-01", new AssignNistRolesCommand(_inactiveUser, null, 0), Gestora))
            .Should().ThrowAsync<NistAssessmentValidationException>("usuário inativo não recebe designação");
        await FluentActions.Awaiting(() => x.Nist.AssignAsync(a, c, s, "GV.OC-01", new AssignNistRolesCommand(_foreignUser, null, 0), Gestora))
            .Should().ThrowAsync<NistAssessmentValidationException>("usuário de outro tenant não existe aqui");

        var assigned = await x.Nist.AssignAsync(a, c, s, "GV.OC-01", new AssignNistRolesCommand(_analystUser, _managerUser, 0), Gestora);
        assigned.Evaluation!.AssessorName.Should().Be("Analista Demo");
        assigned.Evaluation.ReviewerName.Should().Be("Revisor Demo");
        assigned.Evaluation.State.Should().Be(NistSubcategoryStates.NotEvaluated, "designar alguém não é avaliar");

        var linked = await x.Nist.SaveEvaluationAsync(a, c, s, "GV.OC-01", Eval(2, 3, assigned.Evaluation.Version, ownerUser: _analystUser, owner: "texto ignorado"), Gestora);
        linked.Evaluation!.Owner!.Kind.Should().Be(NistResponsibleView.KindUser);
        linked.Evaluation.OwnerName.Should().Be("Analista Demo", "o nome vem do cadastro do usuário, não do corpo");

        var legacy = await x.Nist.SaveEvaluationAsync(a, c, s, "GV.OC-02", Eval(2, 3, 0, owner: "Analista Demo"), Gestora);
        legacy.Evaluation!.Owner!.Kind.Should().Be(NistResponsibleView.KindText, "texto igual ao nome de um usuário NÃO vira vínculo");
        legacy.Evaluation.Owner.UserId.Should().BeNull();

        var external = await x.Nist.SaveEvaluationAsync(a, c, s, "GV.OC-03",
            new SaveNistEvaluationCommand(2, 3, false, null, null, null, null, null, null, "Consultoria Exemplo", 0, null, true, "contato@demo.example.com"), Gestora);
        (external.Evaluation!.Owner!.Kind, external.Evaluation.Owner.Contact).Should().Be((NistResponsibleView.KindExternal, "contato@demo.example.com"));

        var audit = await x.Nist.AuditAsync(a, c, s, "GV.OC-01");
        audit.First(e => e.Subject == "Assignment").Changes.Should().Contain(ch => ch.Field == "assessor" && ch.From == null && ch.To == "Analista Demo");
    }

    [Fact]
    public async Task Revisao_PorOutraPessoa_FicaDesatualizadaQuandoOConteudoMuda()
    {
        var (a, c, s) = await CreateAsync(TenantA);
        await using var x = For(TenantA);
        var saved = await x.Nist.SaveEvaluationAsync(a, c, s, "DE.CM-01", Eval(2, 4, 0, rationale: "Monitoramento parcial da rede."), Gestora);

        await FluentActions.Awaiting(() => x.Nist.ReviewAsync(a, c, s, "DE.CM-01", new ReviewNistEvaluationCommand("Approved", null, saved.Evaluation!.Version), Gestora))
            .Should().ThrowAsync<NistAssessmentValidationException>().WithMessage("*outra pessoa*");
        await FluentActions.Awaiting(() => x.Nist.ReviewAsync(a, c, s, "DE.CM-01", new ReviewNistEvaluationCommand("ChangesRequested", "curto", saved.Evaluation!.Version), Revisor))
            .Should().ThrowAsync<NistAssessmentValidationException>();

        var approved = await x.Nist.ReviewAsync(a, c, s, "DE.CM-01", new ReviewNistEvaluationCommand("Approved", "Coerente com a evidência.", saved.Evaluation!.Version), Revisor);
        approved.Evaluation!.ReviewState.Should().Be(NistReviewStates.Approved);
        approved.Evaluation.ReviewDecisionByName.Should().Be("Revisor Demo");
        approved.Evaluation.ReviewedByName.Should().Be("Gestora Demo", "a revisão do revisor não substitui o registro humano da avaliação");

        // Designar responsável não toca o conteúdo: a revisão continua valendo.
        var reassigned = await x.Nist.AssignAsync(a, c, s, "DE.CM-01", new AssignNistRolesCommand(_analystUser, null, approved.Evaluation.Version), Gestora);
        reassigned.Evaluation!.ReviewState.Should().Be(NistReviewStates.Approved);

        var changed = await x.Nist.SaveEvaluationAsync(a, c, s, "DE.CM-01", Eval(3, 4, reassigned.Evaluation.Version, rationale: "Monitoramento parcial da rede."), Gestora);
        changed.Evaluation!.ReviewState.Should().Be(NistReviewStates.Outdated, "o conteúdo aprovado não é mais o vigente");
    }

    // ===================================================================================================
    //  Achados e planos
    // ===================================================================================================

    [Fact]
    public async Task Achado_SoComLacunaDocumentada_PlanoNistAtomico_CicloCompletoSemMudarMaturidade()
    {
        var (a, c, s) = await CreateAsync(TenantA);
        await using (var x = For(TenantA))
        {
            // Atual = alvo e sem lacuna escrita: nada a registrar.
            await x.Nist.SaveEvaluationAsync(a, c, s, "ID.AM-02", Eval(3, 3, 0), Gestora);
            await FluentActions.Awaiting(() => x.Work.CreateFindingAsync(a, c, s, "ID.AM-02", Finding(), Gestora))
                .Should().ThrowAsync<NistAssessmentValidationException>().WithMessage("*meta de melhoria não é, por si, um achado*");
            await FluentActions.Awaiting(() => x.Work.CreateFindingAsync(a, c, s, "ID.AM-03", Finding(), Gestora))
                .Should().ThrowAsync<NistAssessmentValidationException>().WithMessage("*Confirme a avaliação*");

            await x.Nist.SaveEvaluationAsync(a, c, s, "ID.AM-01", Eval(2, 4, 0, gaps: "Planilha sem responsável por ativo."), Gestora);
            (await x.Nist.GetProfileAsync(a, c, s)).Treatment!.FindingsOpen.Should().Be(0, "diferença entre atual e alvo não vira achado sozinha");

            // Justificativas obrigatórias.
            await FluentActions.Awaiting(() => x.Work.CreateFindingAsync(a, c, s, "ID.AM-01", Finding() with { SeverityRationale = "" }, Gestora))
                .Should().ThrowAsync<NistAssessmentValidationException>().WithMessage("*severidade*");

            // Plano com responsável inválido: nem o achado nem o plano são gravados.
            await FluentActions.Awaiting(() => x.Work.CreateFindingAsync(a, c, s, "ID.AM-01",
                    Finding(new NistPlanInput("Atribuir donos", null, new NistResponsibleInput(_inactiveUser, null, false, null), null, new DateOnly(2026, 11, 30))), Gestora))
                .Should().ThrowAsync<NistAssessmentValidationException>();
        }
        await using (var x = For(TenantA))
            (await x.Db.NistFindings.CountAsync()).Should().Be(0, "achado e plano nascem juntos ou nenhum dos dois");

        NistFindingView finding;
        await using (var x = For(TenantA))
        {
            finding = await x.Work.CreateFindingAsync(a, c, s, "ID.AM-01",
                Finding(new NistPlanInput("Atribuir donos aos ativos", null, new NistResponsibleInput(_analystUser, null, false, null), "TI", new DateOnly(2026, 11, 30))), Gestora);
            finding.Origin!.CurrentLevel.Should().Be(2);
            finding.Origin.GapsText.Should().Be("Planilha sem responsável por ativo.");
            var plan = finding.Plan!;
            plan.OriginKind.Should().Be("NistFinding");
            plan.NistOrigin.Should().Be(new NistPlanOrigin(finding.Id, a, c, s, "ID.AM-01"), "avaliação, rodada e escopo ficam explícitos no plano");
            plan.ResponsiblePerson.Should().Be("Analista Demo");
            plan.ResponsibleUserId.Should().Be(_analystUser);

            (await x.Db.ActionPlans.AsNoTracking().SingleAsync()).KnightIndicatorId.Should().BeNull("nenhum identificador do KNIGHT é inventado para encaixar o NIST");
            (await x.Remediation.ListAsync(new ActionPlanFilter())).Should().BeEmpty("a lista padrão (KNIGHT) não mistura planos NIST");
            (await x.Remediation.ListAsync(new ActionPlanFilter(Origin: ActionPlanOriginScope.NistFinding))).Should().ContainSingle();
            (await x.Remediation.ListAsync(new ActionPlanFilter(Origin: ActionPlanOriginScope.All))).Should()
                .ContainSingle("a visão consolidada de todas as origens inclui o plano do achado NIST")
                .Which.NistOrigin!.SubcategoryCode.Should().Be("ID.AM-01");

            await FluentActions.Awaiting(() => x.Work.CreatePlanAsync(a, c, s, finding.Id, new NistPlanInput("Duplicado", null, null, null, null), Gestora))
                .Should().ThrowAsync<NistAssessmentConflictException>("um único plano ativo por achado");
        }

        await using (var x = For(TenantA))
        {
            var plan = finding.Plan!;
            var started = await x.Work.UpdatePlanAsync(a, c, s, finding.Id, plan.Id, new UpdateNistPlanCommand(plan.Version, null, null,
                new NistResponsibleInput(null, "Fornecedor Exemplo", true, "suporte@demo.example.com"), null, new DateOnly(2026, 12, 15), "EmAndamento"), Gestora);
            started.Plan!.ResponsibleIsExternal.Should().BeTrue();
            _clock.Advance(TimeSpan.FromDays(10));
            var executed = await x.Work.RecordPlanExecutionAsync(a, c, s, finding.Id, plan.Id,
                new RecordExecutionCommand(started.Plan.Version, "Donos atribuídos a todos os servidores.", "CHG-0001"), Gestora);
            await FluentActions.Awaiting(() => x.Work.ValidatePlanAsync(a, c, s, finding.Id, plan.Id,
                    new ValidateActionPlanCommand(executed.Plan!.Version, Guid.NewGuid(), null, null), Gestora))
                .Should().ThrowAsync<NistAssessmentValidationException>("achado NIST não se valida por coleta do KNIGHT");
            _clock.Advance(TimeSpan.FromDays(1));
            var validated = await x.Work.ValidatePlanAsync(a, c, s, finding.Id, plan.Id,
                new ValidateActionPlanCommand(executed.Plan!.Version, null, "Planilha revisada em 01/12 (DOC-77).", null), Revisor);
            validated.Plan!.ApplicableValidation!.Outcome.Should().Be("HumanAttested");
            var closed = await x.Work.UpdatePlanAsync(a, c, s, finding.Id, plan.Id, new UpdateNistPlanCommand(validated.Plan.Version, null, null, null, null, null, "Concluido"), Gestora);
            closed.Plan!.Status.Should().Be("Concluido");
            closed.Plan.NextStep.Should().Contain("não altera a maturidade");
            closed.TreatmentLabel.Should().Contain("Concluída");

            _clock.Advance(TimeSpan.FromDays(30));
            var reopened = await x.Work.UpdatePlanAsync(a, c, s, finding.Id, plan.Id, new UpdateNistPlanCommand(closed.Plan.Version, null, null, null, null, null, "EmAndamento"), Gestora);
            reopened.Plan!.WasReopened.Should().BeTrue();
            reopened.Plan.ApplicableValidation.Should().BeNull("a validação do ciclo anterior não autoriza o novo ciclo");

            var eval = (await x.Nist.GetSubcategoryAsync(a, c, s, "ID.AM-01")).Evaluation!;
            (eval.CurrentLevel, eval.TargetLevel).Should().Be((2, 4), "concluir ou reabrir o plano não altera a maturidade");

            var trail = await x.Nist.AuditAsync(a, c, s, "ID.AM-01");
            trail.Should().Contain(e => e.Subject == "Plan" && e.Changes.Any(ch => ch.Field == "responsible"
                && ch.From == "Analista Demo (usuário)" && ch.To == "Fornecedor Exemplo (externo, suporte@demo.example.com)"));
            trail.Should().Contain(e => e.Subject == "Plan" && e.Changes.Any(ch => ch.Field == "status" && ch.From == "Concluída"));
            trail.Should().Contain(e => e.Subject == "Finding" && e.Action == "Created");

            var status = await x.Work.SetFindingStatusAsync(a, c, s, finding.Id, new SetNistFindingStatusCommand("RiskAccepted", "curto", 1), Gestora)
                .ContinueWith(t => t.Exception?.InnerException);
            status.Should().BeOfType<NistAssessmentValidationException>("aceitar risco exige justificativa");
        }

        // Isolamento: o tenant B não enxerga nem altera o achado, o plano ou a trilha de A.
        await using (var y = For(TenantB))
        {
            await FluentActions.Awaiting(() => y.Work.GetFindingAsync(a, c, s, finding.Id)).Should().ThrowAsync<NistAssessmentNotFoundException>();
            await FluentActions.Awaiting(() => y.Work.UpdatePlanAsync(a, c, s, finding.Id, finding.Plan!.Id,
                new UpdateNistPlanCommand(1, "x", null, null, null, null, null), Gestora)).Should().ThrowAsync<NistAssessmentNotFoundException>();
            await FluentActions.Awaiting(() => y.Nist.AuditAsync(a, null, null, null)).Should().ThrowAsync<NistAssessmentNotFoundException>();
            (await y.Remediation.ListAsync(new ActionPlanFilter(Origin: ActionPlanOriginScope.NistFinding))).Should().BeEmpty();
        }
    }

    // ===================================================================================================
    //  Publicação e relatórios
    // ===================================================================================================

    [Fact]
    public async Task Publicacao_ConfereOConteudoRevisado_CongelaTudo_EOsTresFormatosSaoDaMesmaFotografia()
    {
        var (a, c, s) = await CreateAsync(TenantA);
        Guid findingId;
        await using (var x = For(TenantA))
        {
            await x.Nist.SaveEvaluationAsync(a, c, s, "ID.AM-01", Eval(2, 4, 0, gaps: "Planilha sem responsável por ativo."), Gestora);
            await x.Nist.SaveEvaluationAsync(a, c, s, "GV.OC-01", Eval(3, 4, 0, rationale: "=HYPERLINK(\"http://x\")"), Gestora);
            await x.Nist.SaveEvaluationAsync(a, c, s, "RC.RP-01", Eval(null, null, 0, na: true, rationale: "Sem operação de recuperação no escopo."), Gestora);
            var ev = await x.Nist.LinkEvidenceAsync(a, c, s, "ID.AM-01", new LinkNistEvidenceCommand("Manual", null, null, null, "Planilha de ativos",
                "https://intranet.demo.example.com/ativos", "Mostra a ausência de donos.", new DateOnly(2026, 9, 1), "Document"), Gestora);
            var p = await x.Work.AddProcedureAsync(a, c, s, "ID.AM-01", new AddNistProcedureCommand("Interview", "Entrevistar o gestor de infraestrutura."), Gestora);
            await x.Work.UpdateProcedureAsync(a, c, s, "ID.AM-01", p.Id, new UpdateNistProcedureCommand(null, "Performed", "PartiallySatisfactory",
                "O gestor confirma que o inventário não é revisado.", new DateOnly(2026, 9, 2), new[] { ev.Evidence.Single().Id }, p.Version), Gestora);
            findingId = (await x.Work.CreateFindingAsync(a, c, s, "ID.AM-01", Finding(new NistPlanInput("Atribuir donos", null,
                new NistResponsibleInput(_analystUser, null, false, null), null, new DateOnly(2026, 11, 30)), new[] { ev.Evidence.Single().Id }), Gestora)).Id;
        }

        NistPublicationPreview preview;
        await using (var x = For(TenantA))
        {
            preview = await x.Publication.PreviewAsync(a, c, s);
            preview.Summary.Evaluated.Should().Be(2);
            preview.Summary.NotApplicable.Should().Be(1);
            preview.Summary.Current.Should().Be(2.5);
            (await x.Publication.PreviewAsync(a, c, s)).ContentFingerprint.Should().Be(preview.ContentFingerprint, "o mesmo conteúdo dá a mesma impressão digital");
        }

        // O conteúdo mudou depois da revisão na tela: a publicação é recusada, nada é publicado.
        await using (var x = For(TenantA))
            await x.Nist.SaveEvaluationAsync(a, c, s, "GV.OC-02", Eval(1, 3, 0), Gestora);
        await using (var x = For(TenantA))
        {
            await FluentActions.Awaiting(() => x.Publication.PublishAsync(a, c, s, new PublishNistCommand(preview.ContentFingerprint), Gestora))
                .Should().ThrowAsync<NistAssessmentConflictException>().WithMessage("*mudou desde a revisão*");
            (await x.Db.PostureSnapshots.CountAsync()).Should().Be(0);
            preview = await x.Publication.PreviewAsync(a, c, s);
        }

        NistPublicationView published;
        await using (var x = For(TenantA))
            published = await x.Publication.PublishAsync(a, c, s, new PublishNistCommand(preview.ContentFingerprint), Gestora);
        published.ContentFingerprint.Should().Be(preview.ContentFingerprint);

        // A fotografia: score 0–100 nulo; maturidade em colunas próprias; hash íntegro.
        await using (var x = For(TenantA))
        {
            var snap = await x.Db.PostureSnapshots.AsNoTracking().SingleAsync();
            snap.Type.Should().Be(PostureSnapshotType.NistMaturity);
            snap.Score.Should().BeNull("uma nota 1–5 não é gravada no score 0–100");
            snap.MaturityCurrent.Should().Be(preview.Summary.Current);
            snap.NistCycleId.Should().Be(c);
            PostureSnapshotHasher.Verify(snap).Should().BeTrue();
            snap.NistReportJson = snap.NistReportJson!.Replace("Planilha de ativos", "Planilha adulterada");
            PostureSnapshotHasher.Verify(snap).Should().BeFalse("o relatório congelado está sob o hash");
        }

        // Depois de publicar, o presente muda — e a fotografia não.
        await using (var x = For(TenantA))
        {
            var f = await x.Work.GetFindingAsync(a, c, s, findingId);
            await x.Work.UpdateFindingAsync(a, c, s, findingId, new UpdateNistFindingCommand("Título atual diferente", null, null, null, null, null, null, null, null, null, f.Version), Gestora);
            var e = (await x.Nist.GetSubcategoryAsync(a, c, s, "ID.AM-01")).Evaluation!;
            await x.Nist.SaveEvaluationAsync(a, c, s, "ID.AM-01", Eval(4, 4, e.Version, gaps: "Resolvido."), Gestora);
            await x.Db.Tenants.Where(t => t.Id == TenantA).ExecuteUpdateAsync(u => u.SetProperty(t => t.Name, "Nome Novo do Cliente"));
        }

        await using (var x = For(TenantA))
        {
            var exporter = new PostureSnapshotExporter(x.Db);
            var html = Encoding.UTF8.GetString((await exporter.ExportAsync(published.SnapshotId, PostureExportFormat.Html))!.Content);
            var csv = Encoding.UTF8.GetString((await exporter.ExportAsync(published.SnapshotId, PostureExportFormat.Csv))!.Content);
            var pdf = (await exporter.ExportAsync(published.SnapshotId, PostureExportFormat.Pdf))!;

            // Reexportar não busca o presente.
            html.Should().Contain("Inventário sem dono definido").And.NotContain("Título atual diferente");
            html.Should().Contain("Cliente Sintético A").And.NotContain("Nome Novo do Cliente");
            csv.Should().Contain("Inventário sem dono definido").And.NotContain("Título atual diferente");
            csv.Should().Contain("AUTORAL do AEGIS", "o CSV também diz que a escala é do AEGIS, não exigência do NIST");
            pdf.ContentType.Should().Be("application/pdf");
            Encoding.ASCII.GetString(pdf.Content, 0, 5).Should().Be("%PDF-");
            pdf.FileName.Should().Contain("nist-maturidade");

            // HTML: autocontido, offline, CSP restritiva; evidência e procedimento expansíveis; metodologia identificada.
            html.Should().Contain("default-src 'none'").And.NotContain("http://cdn").And.NotContain("<link ");
            html.Should().Contain("id=\"sub-ID.AM-01\"").And.Contain("Entrevistar o gestor de infraestrutura.").And.Contain("Parcialmente satisfatório");
            html.Should().Contain("Escala de maturidade 1–5 AUTORAL do AEGIS").And.Contain("não define pontuação");
            html.Should().NotContain("style=\"", "a CSP não admite estilo inline");

            // Reconciliação dos três formatos com a mesma fotografia.
            var report = NistReportCanonical.Deserialize((await x.Db.PostureSnapshots.AsNoTracking().SingleAsync()).NistReportJson!);
            var lines = csv.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            lines.Count(l => l.StartsWith("Subcategoria;", StringComparison.Ordinal)).Should().Be(report.Summary.Subcategories).And.Be(106);
            lines.Count(l => l.StartsWith("Achado;", StringComparison.Ordinal)).Should().Be(report.Findings.Count).And.Be(1);
            lines.Count(l => l.StartsWith("Evidência;", StringComparison.Ordinal)).Should().Be(report.Summary.EvidenceLinked).And.Be(1);
            lines.Count(l => l.StartsWith("Procedimento;", StringComparison.Ordinal)).Should().Be(1);
            CountOf(html, "<details class=\"sub\"").Should().Be(106);
            CountOf(html, "<details class=\"finding\"").Should().Be(1);
            lines.All(l => l.Contains(published.SnapshotId.ToString("D"))).Should().BeFalse("o cabeçalho não leva a fotografia");
            lines.Skip(1).Should().OnlyContain(l => l.Contains(published.SnapshotId.ToString("D")), "toda linha de dado identifica a fotografia");

            // Fórmula neutralizada no CSV; número negativo do sistema não é tratado como fórmula.
            csv.Should().Contain("'=HYPERLINK").And.NotContain(";=HYPERLINK");

            // Comparação genérica de fotografias não fabrica "sem mudança" para a maturidade.
            var compare = await new PostureSnapshotService(x.Db, new SystemTenantContext(TenantA), new AegisScore.Infrastructure.Connectors.NistSignalMapper(x.Db))
                .CompareAsync(published.SnapshotId, published.SnapshotId);
            compare!.Compatible.Should().BeFalse();
        }
    }

    [Fact]
    public async Task EvolucaoMensal_MaturidadeEmSerieSeparada_ComCoberturaEUniversoVisiveis()
    {
        var (a, c1, s) = await CreateAsync(TenantA);
        await using (var x = For(TenantA))
        {
            await x.Nist.SaveEvaluationAsync(a, c1, s, "ID.AM-01", Eval(4, 4, 0), Gestora);
            await x.Publication.PublishAsync(a, c1, s, new PublishNistCommand((await x.Publication.PreviewAsync(a, c1, s)).ContentFingerprint), Gestora);
        }
        _clock.Advance(TimeSpan.FromDays(31));
        Guid c2;
        await using (var x = For(TenantA))
        {
            c2 = (await x.Nist.CreateCycleAsync(a, new CreateNistCycleCommand("Novembro 2026", "Monthly", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), c1, "Reference"), Gestora)).Id;
            await x.Nist.SaveEvaluationAsync(a, c2, s, "ID.AM-01", Eval(3, 4, 0), Gestora);
            await x.Nist.SaveEvaluationAsync(a, c2, s, "ID.AM-02", Eval(2, 4, 0), Gestora);
            await x.Nist.SaveEvaluationAsync(a, c2, s, "RC.RP-01", Eval(null, null, 0, na: true, rationale: "Fora do escopo desta rodada."), Gestora);
            (await x.Nist.GetSubcategoryAsync(a, c2, s, "ID.AM-01")).Reference!.CurrentLevel.Should().Be(4, "referência mostra a rodada anterior sem copiar nada");
            await x.Publication.PublishAsync(a, c2, s, new PublishNistCommand((await x.Publication.PreviewAsync(a, c2, s)).ContentFingerprint), Gestora);

            var comparison = await x.Publication.CompareCyclesAsync(a, s, c2, c1);
            comparison.Compatible.Should().BeTrue();
            (comparison.BaseCycleName, comparison.TargetCycleName).Should().Be(("T3 2026", "Novembro 2026"), "a base é a rodada mais antiga, qualquer que seja a ordem pedida");
            comparison.CurrentDelta.Should().Be(-1.5);
            comparison.Notes.Should().Contain(n => n.Contains("não indica, sozinho, piora"));
            comparison.Notes.Should().Contain(n => n.Contains("universo aplicável mudou de 106 para 105"));
            comparison.Changes.Should().Contain(ch => ch.Code == "ID.AM-01" && ch.Kind == "LevelDown");

            var all = await new PostureSnapshotService(x.Db, new SystemTenantContext(TenantA), new AegisScore.Infrastructure.Connectors.NistSignalMapper(x.Db)).ListAsync(null);
            var history = PostureMonthlyHistory.Build(all, _clock.GetUtcNow(), 12);
            var series = history.Series.Should().ContainSingle(se => se.Type == "NistMaturity").Subject;
            series.Label.Should().StartWith("NIST · maturidade 1–5");
            series.Points.Should().HaveCount(2);
            series.Points.Should().OnlyContain(p => p.Score == null, "o eixo 0–100 não é usado pela maturidade");
            var last = series.Points.Last();
            (last.MaturityCurrent, last.ApplicableItems, last.CycleName).Should().Be((2.5, 105, "Novembro 2026"));
            last.ComparableWithPrevious.Should().BeTrue();
            last.Notes.Should().Contain(n => n.Contains("Universo aplicável mudou")).And.Contain(n => n.Contains("Cobertura mudou"));
        }
    }

    // ===================================================================================================
    //  Importação CSV
    // ===================================================================================================

    [Fact]
    public async Task ImportacaoCsv_PreviaValida_AplicaComoRascunho_ENuncaApaga()
    {
        var (a, c, s) = await CreateAsync(TenantA);
        int version;
        await using (var x = For(TenantA))
            version = (await x.Nist.SaveEvaluationAsync(a, c, s, "GV.OC-01", Eval(2, 4, 0, rationale: "Missão documentada em parte."), Gestora)).Evaluation!.Version;

        string template;
        await using (var x = For(TenantA))
        {
            var file = await x.Import.TemplateAsync(a, c, s);
            template = Encoding.UTF8.GetString(file.Content);
            template.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(107, "cabeçalho + uma linha por subcategoria");
            var roundTrip = await x.Import.PreviewAsync(a, c, s, template, "trabalho.csv");
            (roundTrip.Creates, roundTrip.Updates, roundTrip.Errors, roundTrip.Conflicts).Should().Be((0, 0, 0, 0), "o arquivo exportado volta sem mudança");
            roundTrip.CanApply.Should().BeFalse("não há o que gravar");
        }

        // Edição em planilha: altera uma linha existente, cria outra, e erra outras.
        var lines = template.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();
        string Edit(string code, Func<string[], string[]> change)
        {
            var i = lines.FindIndex(l => l.Contains($";{code};"));
            var cells = lines[i].Split(';');
            return string.Join(';', change(cells));
        }
        var header = lines[0].Split(';').ToList();
        int Col(string name) => header.IndexOf(name);
        var updated = Edit("GV.OC-01", cells => { cells[Col("Atual")] = "3"; cells[Col("Justificativa")] = "Missão aprovada pelo conselho em setembro."; return cells; });
        var created = Edit("GV.OC-02", cells => { cells[Col("Atual")] = "2"; cells[Col("Alvo")] = "4"; cells[Col("Lacunas observadas")] = "=cmd|' /C calc'!A0"; return cells; });
        var badLevel = Edit("GV.OC-03", cells => { cells[Col("Atual")] = "7"; return cells; });
        var badScope = Edit("GV.OC-04", cells => { cells[Col("Escopo")] = "Outro escopo"; cells[Col("Atual")] = "2"; return cells; });
        var unknown = Edit("GV.OC-05", cells => { cells[Col("Código")] = "XX.YY-01"; return cells; });
        var csvWithErrors = string.Join("\r\n", new[] { lines[0], updated, created, badLevel, badScope, unknown });

        await using (var x = For(TenantA))
        {
            var preview = await x.Import.PreviewAsync(a, c, s, csvWithErrors, "planilha.csv");
            preview.CanApply.Should().BeFalse();
            preview.Items.Single(i => i.Code == "GV.OC-01").Action.Should().Be(NistImportActions.Update);
            preview.Items.Single(i => i.Code == "GV.OC-01").Changes.Should().Contain(ch => ch.Field == "currentLevel" && ch.From == "2" && ch.To == "3");
            preview.Items.Single(i => i.Code == "GV.OC-02").Action.Should().Be(NistImportActions.Create);
            preview.Items.Single(i => i.Code == "GV.OC-03").Messages.Should().Contain(m => m.Contains("fora da escala"));
            preview.Items.Single(i => i.Code == "GV.OC-04").Messages.Should().Contain(m => m.Contains("Escopo"));
            preview.Items.Single(i => i.Code == "XX.YY-01").Messages.Should().Contain(m => m.Contains("não existe no catálogo"));
            await FluentActions.Awaiting(() => x.Import.ApplyAsync(a, c, s, csvWithErrors, "planilha.csv", preview.Token, Gestora))
                .Should().ThrowAsync<NistAssessmentValidationException>("com erro, nada é gravado");
            var duplicated = await x.Import.PreviewAsync(a, c, s, string.Join("\r\n", new[] { lines[0], updated, updated }), "dup.csv");
            duplicated.Items.Should().OnlyContain(i => i.Messages.Any(m => m.Contains("mais de uma vez")));
        }

        var good = string.Join("\r\n", new[] { lines[0], updated, created });
        NistImportPreview goodPreview;
        await using (var x = For(TenantA))
        {
            goodPreview = await x.Import.PreviewAsync(a, c, s, good, "planilha.csv");
            goodPreview.CanApply.Should().BeTrue();
            (goodPreview.Creates, goodPreview.Updates).Should().Be((1, 1));
        }

        // Outra pessoa grava entre a prévia e a aplicação: a aplicação inteira é recusada.
        await using (var x = For(TenantA))
            await x.Nist.SaveEvaluationAsync(a, c, s, "GV.OC-01", Eval(2, 5, version, rationale: "Missão documentada em parte."), Revisor);
        await using (var x = For(TenantA))
        {
            await FluentActions.Awaiting(() => x.Import.ApplyAsync(a, c, s, good, "planilha.csv", goodPreview.Token, Gestora))
                .Should().ThrowAsync<NistAssessmentConflictException>();
            var stale = await x.Import.PreviewAsync(a, c, s, good, "planilha.csv");
            stale.Items.Single(i => i.Code == "GV.OC-01").Action.Should().Be(NistImportActions.Conflict, "a versão da planilha ficou para trás");
            (await x.Db.Evaluations.CountAsync(e => e.CycleId == c)).Should().Be(1, "nada foi criado pela tentativa recusada");
        }

        // Sem a linha em conflito, aplica: conteúdo importado não é revisão humana e fica fora das médias.
        var onlyCreate = string.Join("\r\n", new[] { lines[0], created });
        await using (var x = For(TenantA))
        {
            var p = await x.Import.PreviewAsync(a, c, s, onlyCreate, "nova.csv");
            var result = await x.Import.ApplyAsync(a, c, s, onlyCreate, "nova.csv", p.Token, Gestora);
            result.Created.Should().Be(1);
        }
        await using (var x = For(TenantA))
        {
            var imported = (await x.Nist.GetSubcategoryAsync(a, c, s, "GV.OC-02")).Evaluation!;
            imported.State.Should().Be(NistSubcategoryStates.PendingConfirmation);
            imported.ContentOrigin.Should().Be("Imported");
            imported.OriginNote.Should().Contain("nova.csv").And.Contain("linha 2");
            imported.Gaps.Should().Be("=cmd|' /C calc'!A0", "o texto é guardado como dado; a proteção é aplicada na exportação");
            (await x.Nist.GetProfileAsync(a, c, s)).Overall.Current.Should().Be(2, "só a avaliação confirmada (GV.OC-01 = 2) entra na média");
            (await x.Db.Evaluations.CountAsync(e => e.CycleId == c)).Should().Be(2, "importar nunca apaga avaliações existentes");
            (await x.Nist.AuditAsync(a, c, s, "GV.OC-02")).Should().Contain(e => e.Subject == "Import" && e.Action == "Created");

            var exported = Encoding.UTF8.GetString((await x.Import.TemplateAsync(a, c, s)).Content);
            exported.Should().Contain("'=cmd|", "a exportação neutraliza fórmula vinda de texto importado");
            var reimport = await x.Import.PreviewAsync(a, c, s, exported, "reexportado.csv");
            reimport.Items.Single(i => i.Code == "GV.OC-02").Action.Should().Be(NistImportActions.Unchanged,
                "o apóstrofo de neutralização volta sem acrescentar nem perder caracteres");
        }
    }

    private static int CountOf(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
