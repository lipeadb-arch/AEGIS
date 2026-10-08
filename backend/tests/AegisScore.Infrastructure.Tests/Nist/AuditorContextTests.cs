using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Nist;
using AegisScore.Application.Remediation;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Nist;
using AegisScore.Infrastructure.Persistence;
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
/// [AEGIS-AUDITOR-CONTEXT-01] Contexto do Auditor e correlação KNIGHT × NIST sobre SQLite in-memory, catálogo NIST real e dados sintéticos:
/// <list type="bullet">
/// <item>correlação por registros: vinculado × disponível para revisão × não avaliado, sem duplicar controle vinculado a várias
/// subcategorias, com execução mais recente avisada, nível sem evidência, tratamentos (cada plano uma vez), demonstração e coleta parcial;</item>
/// <item>contexto do Auditor: KNIGHT, NIST (mesmo montador da assistência), correlação, documentos, inventário e publicações em fontes
/// citáveis com natureza e links que preservam a seleção; listas resumidas dizem o tamanho do universo; sem nome de pessoa;</item>
/// <item>isolamento: seleção, avaliação KNIGHT e conversa de outro tenant (ou de outra conta) não são lidas;</item>
/// <item>assistência NIST: revisão do resumo vinculada à aceitação vigente e de outra pessoa; catálogo não sustenta afirmação sobre o ambiente.</item>
/// </list>
/// </summary>
public sealed class AuditorContextTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0808-0808-0808-0000000000a1");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0808-0808-0808-0000000000b1");
    private static readonly RemediationActor Gestora = new(Guid.Parse("11111111-0808-0808-0808-000000000001"), "Gestora Demo");
    private static readonly RemediationActor Revisora = new(Guid.Parse("22222222-0808-0808-0808-000000000002"), "Revisora Demo");
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");
    private static readonly ControlLanguageCatalog Language = new(
        Path.Combine(DataDir, "aegis_control_language.pt-BR.json"), NullLogger<ControlLanguageCatalog>.Instance);

    private readonly SqliteConnection _connection;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));

    public AuditorContextTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        FrameworkSeeder.SeedAsync(ctx, Path.Combine(DataDir, "nist_csf_2_0_catalog.json"), Path.Combine(DataDir, "aegis_methodology.json"))
            .GetAwaiter().GetResult();
        foreach (var t in new[] { TenantA, TenantB })
            ctx.Tenants.Add(new Tenant { Id = t, Name = "Cliente Sintético", Slug = $"t-{t:N}", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private sealed record Services(
        AegisScoreDbContext Db, NistAssessmentService Nist, NistWorkService Work, NistAssistService Assist, NistEvidenceOverviewService Overview,
        AuditorAssessmentContextBuilder Builder, AuditorConversationStore Conversations) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private Services For(Guid tenant, IAiAssessmentService? ai = null)
    {
        var db = NewContext(tenant);
        var ctx = new SystemTenantContext(tenant);
        var remediation = new RemediationService(db, ctx, _clock,
            new AegisScore.Infrastructure.Queries.DevicePriorityQuery(db, _clock,
                Microsoft.Extensions.Options.Options.Create(new AegisScore.Application.Queries.CrossSourceCorrelationOptions())));
        var publication = new NistPublicationService(db, ctx, _clock, Language);
        var gate = new SimulatedGate();
        ai ??= new TenantScopedAssessmentRouter(new AegisAssessmentService(new NoLlm(), StaticAuditorPersonaProvider.Neutral, gate),
            new StubAssessmentService(), gate, new DemoSlug());
        var nist = new NistAssessmentService(db, ctx, _clock, Language);
        var assist = new NistAssistService(db, ctx, _clock, ai, gate, new DemoSlug(), publication, new NistAssistInFlight(), Language);
        var overview = new NistEvidenceOverviewService(db, ctx, _clock, Language);
        // Leituras do KNIGHT só usam o banco: as dependências de coleta não participam (nulas de propósito).
        var knight = new AegisKnightAssessmentService(db, null!, null!, null!, null!, null!, ctx);
        return new Services(db, nist, new NistWorkService(db, ctx, _clock, remediation, Language), assist, overview,
            new AuditorAssessmentContextBuilder(db, ctx, knight, nist, assist, overview, _clock), new AuditorConversationStore(db, ctx, _clock));
    }

    private async Task<(Guid A, Guid C, Guid S)> CreateAsync(Guid tenant)
    {
        await using var x = For(tenant);
        var created = await x.Nist.CreateAsync(new CreateNistAssessmentCommand("Avaliação NIST 2026", null, new DateOnly(2026, 10, 1), null,
            "Matriz", "Escopo sintético", "Outubro 2026", "Monthly", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), Gestora);
        return (created.Id, created.Cycles!.Single().Id, created.Scopes.Single().Id);
    }

    private static SaveNistEvaluationCommand Eval(int? current, int? target, int version, string? gaps = null) =>
        new(current, target, false, null, null, "Justificativa sintética da avaliação.", gaps, null, null, null, version);

    // ===================================================================================================
    //  Correlação KNIGHT × NIST
    // ===================================================================================================

    [Fact]
    public async Task Correlacao_VinculadoDisponivelNaoAvaliado_SemDuplicar_ComExecucaoMaisRecente_TratamentosEUmPlanoPorVez()
    {
        var (a, c, s) = await CreateAsync(TenantA);
        Guid older, latest;
        await using (var x = For(TenantA))
        {
            older = await SeedRunAsync(x.Db, new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), KnightAssessmentMode.Live, KnightSourceState.Completed,
                ("AK-ENTRA-002", KnightIndicatorStatus.Exposed, new[] { "PR.AA-01", "PR.AA-03" }));
            // Vínculo pela gravação de sempre (LinkEvidenceAsync), do MESMO controle em duas subcategorias.
            await x.Nist.LinkEvidenceAsync(a, c, s, "PR.AA-01", new LinkNistEvidenceCommand("KnightIndicator", null, older, "AK-ENTRA-002", null, null, null, null, null), Gestora);
            await x.Nist.LinkEvidenceAsync(a, c, s, "PR.AA-03", new LinkNistEvidenceCommand("KnightIndicator", null, older, "AK-ENTRA-002", null, null, null, null, null), Gestora);
            latest = await SeedRunAsync(x.Db, new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero), KnightAssessmentMode.Live, KnightSourceState.PartialCollection,
                ("AK-ENTRA-002", KnightIndicatorStatus.Passed, new[] { "PR.AA-01", "PR.AA-03" }),
                ("AK-ENTRA-005", KnightIndicatorStatus.Exposed, new[] { "PR.AA-01" }),
                ("AK-ENTRA-007", KnightIndicatorStatus.NotEvaluated, new[] { "PR.AA-01" }),
                ("AK-ENTRA-009", KnightIndicatorStatus.NotApplicable, new[] { "PR.AA-01" }));
            // Nível confirmado sem evidência e achado NIST com plano; plano KNIGHT do controle disponível.
            await x.Nist.SaveEvaluationAsync(a, c, s, "GV.PO-01", Eval(2, 4, 0, gaps: "Política sem aprovação formal."), Gestora);
            await x.Work.CreateFindingAsync(a, c, s, "GV.PO-01", new CreateNistFindingCommand("Política não aprovada", "Sem ata.", "Diretriz sem respaldo.",
                "Atraso.", "Medium", "Afeta a governança.", "Medium", "Base dos demais.", "Aprovar a política.", null,
                new NistPlanInput("Aprovar a política na diretoria", "Levar à diretoria.", null, null, new DateOnly(2026, 11, 30))), Gestora);
            x.Db.ActionPlans.Add(new ActionPlan
            {
                Title = "Exigir MFA forte", KnightIndicatorId = "AK-ENTRA-005", OriginKind = ActionPlanOriginKind.KnightFinding,
                OriginSourceType = KnightSourceType.MicrosoftEntraId, OriginMode = KnightAssessmentMode.Live, OriginRunId = latest,
                Status = ActionPlanStatus.Aberto, DueDate = new DateOnly(2026, 12, 15), CycleStartedAt = _clock.GetUtcNow(),
            });
            await x.Db.SaveChangesAsync();
        }

        await using (var x = For(TenantA))
        {
            var o = await x.Overview.GetAsync(a, c, s);
            var aa01 = o.Rows.Single(r => r.Code == "PR.AA-01");
            aa01.LinkedTechnical.Should().ContainSingle(t => t.KnightIndicatorId == "AK-ENTRA-002" && t.EvidenceId != null && t.KnightRunId == older);
            var linked = aa01.LinkedTechnical.Single();
            linked.StatusLabel.Should().Be("Reprovado", "o vínculo guarda o resultado da execução de origem");
            linked.NewerStatus.Should().Be("Aprovado", "há execução mais recente da mesma fonte, com outro resultado");
            linked.NewerRunId.Should().Be(latest);
            aa01.Candidates.Select(t => t.KnightIndicatorId).Should().Equal(new[] { "AK-ENTRA-005" },
                "o mesmo controle já vinculado não reaparece como disponível; não avaliado e não aplicável não são disponíveis");
            aa01.Candidates.Single().EvidenceId.Should().BeNull();
            aa01.Candidates.Single().PartialCollection.Should().BeTrue();
            aa01.TechnicalNotEvaluated.Should().Be(1, "não avaliado conta como limitação; não aplicável não");
            aa01.Plans.Should().ContainSingle(p => p.Origin == "KnightFinding" && p.KnightIndicatorId == "AK-ENTRA-005");

            var po01 = o.Rows.Single(r => r.Code == "GV.PO-01");
            po01.EvaluatedWithoutEvidence.Should().BeTrue();
            po01.Plans.Should().ContainSingle(p => p.Origin == "NistFinding" && p.NistFindingId != null);
            po01.OpenFindings.Should().Be(1);

            o.Summary.LinkedTechnicalLinks.Should().Be(2, "dois vínculos (controle × subcategoria)");
            o.Summary.LinkedTechnicalControls.Should().Be(1, "um único controle técnico — sem duplicar pela vinculação");
            o.Summary.SubcategoriesWithLinkedTechnical.Should().Be(2);
            o.Summary.CandidateControls.Should().Be(1);
            o.Summary.SubcategoriesWithTechnicalNotEvaluated.Should().Be(1);
            o.Summary.SubcategoriesEvaluatedWithoutEvidence.Should().Be(1);
            o.Summary.Plans.Should().Be(2, "cada plano conta uma vez");
            o.Summary.SubcategoriesInScope.Should().BeGreaterThan(100, "o universo é o catálogo inteiro do escopo");
            o.Limitations.Should().Contain(l => l.Contains("Não é interpretação de IA") && l.Contains("não aprova requisitos"));
            o.Limitations.Should().Contain(l => l.StartsWith("Coleta parcial em: Microsoft Entra ID"));
            o.KnightRuns.Should().ContainSingle(r => r.RunId == latest && r.Partial);

            // Nada foi alterado pela leitura.
            var eval = await x.Nist.GetSubcategoryAsync(a, c, s, "GV.PO-01");
            eval.Evaluation!.Version.Should().Be(1);
        }

        await using (var b = For(TenantB))
            await FluentActions.Awaiting(() => b.Overview.GetAsync(a, c, s)).Should().ThrowAsync<NistAssessmentNotFoundException>("outro tenant não lê a correlação");
    }

    [Fact]
    public async Task Correlacao_DocumentosEInventarioVinculadosPelaGravacaoDeSempre()
    {
        var (a, c, s) = await CreateAsync(TenantA);
        await using (var x = For(TenantA))
        {
            var doc = await SeedDocumentAsync(x.Db, "GV.PO-01", "Política de Segurança", "A política é revisada anualmente pela diretoria.");
            x.Db.Assets.Add(new Asset { Name = "srv-01", Category = AssetCategory.Hardware, DiscoverySource = AssetDiscoverySource.Manual, IsActive = true });
            await x.Db.SaveChangesAsync();
            await x.Nist.LinkEvidenceAsync(a, c, s, "GV.PO-01", new LinkNistEvidenceCommand("GovernanceDocument", doc, null, null, null, null, "Demonstra a revisão anual.", null, null), Gestora);
            await x.Nist.LinkEvidenceAsync(a, c, s, "ID.AM-01", new LinkNistEvidenceCommand("AssetInventory", null, null, null, null, null, null, null, null), Gestora);
            await FluentActions.Awaiting(() => x.Nist.LinkEvidenceAsync(a, c, s, "GV.PO-01",
                    new LinkNistEvidenceCommand("AssetInventory", null, null, null, null, null, null, null, null), Gestora))
                .Should().ThrowAsync<NistAssessmentValidationException>("o inventário só sustenta gestão de ativos (ID.AM)");

            var o = await x.Overview.GetAsync(a, c, s);
            o.DocumentLinks.Should().ContainSingle(l => l.DocumentId == doc && l.Code == "GV.PO-01");
            o.Inventory.Single(t => t.Code == "ID.AM-01").Linked.Should().ContainSingle();
            o.Inventory.Where(t => t.Code != "ID.AM-01").Should().OnlyContain(t => t.Linked.Count == 0);
            o.ActiveAssets.Should().Be(1);
            o.InventorySnapshot.Should().Contain("1 ativo(s) ativo(s)");
        }
    }

    // ===================================================================================================
    //  Contexto do Auditor
    // ===================================================================================================

    [Fact]
    public async Task ContextoDoAuditor_ReuneKnightNistCorrelacaoDocumentosAtivosEPublicacoes_ComNaturezaLinksELimites()
    {
        var (a, c, s) = await CreateAsync(TenantA);
        await using (var x = For(TenantA))
        {
            var run = await SeedRunAsync(x.Db, new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero), KnightAssessmentMode.Demo, KnightSourceState.Completed,
                Enumerable.Range(1, 10).Select(i => ($"AK-ENTRA-0{i:00}", KnightIndicatorStatus.Exposed, new[] { "PR.AA-01" })).ToArray());
            await x.Nist.LinkEvidenceAsync(a, c, s, "PR.AA-01", new LinkNistEvidenceCommand("KnightIndicator", null, run, "AK-ENTRA-001", null, null, null, null, null), Gestora);
            await x.Nist.SaveEvaluationAsync(a, c, s, "PR.AA-01", Eval(2, 4, 0, gaps: "MFA não cobre contas de serviço."), Gestora);
            await SeedDocumentAsync(x.Db, "PR.AA-01", "Política de Identidades", "O MFA é obrigatório para administradores.");
            x.Db.PostureSnapshots.Add(new PostureSnapshot
            {
                Type = PostureSnapshotType.Knight, SourceType = KnightSourceType.MicrosoftEntraId, SourceLabel = "Microsoft Entra ID", Score = 61,
                Coverage = 90, FormulaVersion = "knight-score-v1", CapturedAt = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero), ContentHash = "h",
                SchemaVersion = "1", CatalogVersion = "c", SemanticFamily = "knight",
            });
            await x.Db.SaveChangesAsync();
        }

        await using (var x = For(TenantA))
        {
            var focus = AuditorFocus.From("nist", a, c, s, "PR.AA-01", null, null);
            var ctx = await x.Builder.BuildAsync(focus);

            ctx.FocusLabel.Should().Contain("AEGIS NIST").And.Contain("Avaliação NIST 2026").And.Contain("PR.AA-01");
            var keys = ctx.Sources.Select(k => k.Key).ToList();
            keys.Should().OnlyHaveUniqueItems();
            ctx.Sources.Should().Contain(k => k.Key.StartsWith('K') && k.Title.StartsWith("Avaliação KNIGHT") && k.IsDemo);
            ctx.Sources.Count(k => k.Key.StartsWith('K') && k.Nature == AuditorSourceNature.ObservedConfiguration).Should().Be(8);
            ctx.Limitations.Should().Contain(l => l.Contains("8 de 10 controles reprovados"), "a amostra diz o tamanho do universo");

            // NIST: o montador da assistência, com a natureza de cada fonte e o link para a subcategoria com a seleção.
            ctx.Sources.Should().Contain(k => k.Key.StartsWith('N') && k.Nature == AuditorSourceNature.Reference);
            ctx.Sources.Should().Contain(k => k.Key.StartsWith('N') && k.Nature == AuditorSourceNature.AssessorStatement);
            var knightEvidence = ctx.Sources.Single(k => k.Key.StartsWith('N') && k.Nature == AuditorSourceNature.ObservedConfiguration);
            knightEvidence.Link!.Route.Should().Be("/nist/pr/PR.AA-01");
            knightEvidence.Link.Query!["avaliacao"].Should().Be(a.ToString("D"));
            knightEvidence.Link.Fragment.Should().StartWith("ev-");

            // Correlação, documento, inventário e publicação.
            ctx.Sources.Should().Contain(k => k.Key == "R1" && k.Detail!.Contains("1 controle(s) técnico(s) vinculado(s)"));
            ctx.Sources.Should().Contain(k => k.Key.StartsWith('R') && k.Title.StartsWith("PR.AA-01") && k.Detail!.Contains("Disponíveis para revisão"));
            ctx.Sources.Should().Contain(k => k.Key.StartsWith('D') && k.Nature == AuditorSourceNature.Documentation && k.Detail!.Contains("Não vinculado como evidência"));
            ctx.Sources.Should().Contain(k => k.Key == "A1" && k.Nature == AuditorSourceNature.InventoryRecord
                                              && k.Limitation!.Contains("ausência de registro não demonstra ausência de ativos"));
            ctx.Sources.Single(k => k.Key == "K1").IsDemo.Should().BeTrue("o consolidado inclui uma avaliação de demonstração");
            ctx.Sources.Should().Contain(k => k.Key == "P1" && k.Nature == AuditorSourceNature.Publication);

            var json = JsonSerializer.Serialize(ctx);
            json.Should().NotContain("Gestora Demo", "sem nomes de pessoas no contexto enviado à IA");
            json.Should().NotContain("Revisora Demo");
        }
    }

    [Fact]
    public async Task ContextoDoAuditor_SelecaoOuAvaliacaoDeOutroTenant_EhRecusada_NuncaIgnorada()
    {
        var (a, c, s) = await CreateAsync(TenantA);
        Guid run;
        await using (var x = For(TenantA))
            run = await SeedRunAsync(x.Db, new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero), KnightAssessmentMode.Live, KnightSourceState.Completed,
                ("AK-ENTRA-001", KnightIndicatorStatus.Passed, new[] { "PR.AA-01" }));

        await using (var b = For(TenantB))
        {
            await FluentActions.Awaiting(() => b.Builder.BuildAsync(AuditorFocus.From("nist", a, c, s, null, null, null)))
                .Should().ThrowAsync<AuditorFocusNotFoundException>();
            await FluentActions.Awaiting(() => b.Builder.BuildAsync(AuditorFocus.From("knight", null, null, null, null, run, "AK-ENTRA-001")))
                .Should().ThrowAsync<AuditorFocusNotFoundException>();
            var own = await b.Builder.BuildAsync(AuditorFocus.General);
            JsonSerializer.Serialize(own).Should().NotContain("AK-ENTRA-001", "nada do tenant A chega ao contexto do tenant B");
            own.Limitations.Should().Contain(l => l.Contains("nenhuma avaliação técnica concluída"));
        }
        await using (var x = For(TenantA))
        {
            await FluentActions.Awaiting(() => x.Builder.BuildAsync(AuditorFocus.From("nist", a, c, s, "ZZ.ZZ-99", null, null)))
                .Should().ThrowAsync<AuditorFocusNotFoundException>("código fora do catálogo da avaliação é recusado");
            var knightFocus = await x.Builder.BuildAsync(AuditorFocus.From("knight", null, null, null, null, run, "AK-ENTRA-001"));
            knightFocus.Sources.Should().Contain(k => k.Title.StartsWith("Controle em foco: AK-ENTRA-001") && k.Link!.Query!["finding"] == "AK-ENTRA-001");
        }
    }

    // ===================================================================================================
    //  Conversa: tenant + conta, concorrência e limite
    // ===================================================================================================

    [Fact]
    public async Task Conversa_PertenceAoTenantEAConta_ConcorrenciaDaConflito_HistoricoLimitado()
    {
        var conta = Guid.Parse("33333333-0808-0808-0808-000000000003");
        var outra = Guid.Parse("44444444-0808-0808-0808-000000000004");
        AuditorConversation conv;
        await using (var x = For(TenantA))
        {
            conv = await x.Conversations.ReadAsync(conta, null);
            conv.Messages.Should().BeEmpty();
            for (var i = 1; i <= 10; i++)
            {
                await x.Conversations.AppendAsync(conta, conv, $"pergunta {i}", $"resposta {i}", "AEGIS NIST", simulated: true);
                conv = await x.Conversations.ReadAsync(conta, conv.Id);
            }
            conv.Sequence.Should().Be(10);
            conv.Messages.Should().HaveCount(16, "só os oito turnos mais recentes voltam ao motor");
            conv.Messages.First().Content.Should().Be("pergunta 3");

            await FluentActions.Awaiting(() => x.Conversations.ReadAsync(outra, conv.Id))
                .Should().ThrowAsync<AuditorConversationNotFoundException>("outra pessoa do mesmo tenant não lê a conversa");

            // Duas respostas sobre a MESMA leitura: a segunda recebe conflito, nada se mistura.
            await x.Conversations.AppendAsync(conta, conv, "a", "b", "AEGIS NIST", false);
            await FluentActions.Awaiting(() => x.Conversations.AppendAsync(conta, conv, "c", "d", "AEGIS NIST", false))
                .Should().ThrowAsync<AuditorConversationConflictException>();
            await FluentActions.Awaiting(() => x.Conversations.AppendAsync(conta, conv with { Sequence = AuditorConversationStore.MaxTurns }, "e", "f", "x", false))
                .Should().ThrowAsync<AuditorConversationConflictException>("o limite de turnos pede uma nova conversa");
        }
        await using (var b = For(TenantB))
            await FluentActions.Awaiting(() => b.Conversations.ReadAsync(conta, conv.Id))
                .Should().ThrowAsync<AuditorConversationNotFoundException>("a mesma conta noutro tenant não continua a conversa");
    }

    // ===================================================================================================
    //  Assistência NIST: revisão e fontes
    // ===================================================================================================

    [Fact]
    public async Task ResumoExecutivo_RevisaoValeSoParaAAceitacaoVigente_EDeOutraPessoa()
    {
        var (a, c, s) = await CreateAsync(TenantA);
        await using var x = For(TenantA);
        await x.Nist.SaveEvaluationAsync(a, c, s, "GV.PO-01", Eval(1, 3, 0, gaps: "Política sem aprovação."), Gestora);
        var sections = new[] { new NistExecutiveSectionInput("situation", "Atual 1,0 e alvo 3,0 sobre uma subcategoria.") };

        var accepted = await x.Assist.SaveExecutiveSummaryAsync(a, c, s, new SaveNistExecutiveSummaryCommand(sections, null, 0), Gestora);
        var reviewed = await x.Assist.ReviewExecutiveSummaryAsync(a, c, s, new ReviewNistExecutiveSummaryCommand(accepted.Version, "Coerente."), Revisora);
        reviewed.ReviewCurrent.Should().BeTrue();

        // Aceitar de novo o MESMO texto (outra aceitação) derruba a revisão: precisa de nova revisão de outra pessoa.
        var again = await x.Assist.SaveExecutiveSummaryAsync(a, c, s, new SaveNistExecutiveSummaryCommand(sections, null, reviewed.Version), Gestora);
        again.ReviewCurrent.Should().BeFalse();
        again.ReviewedByName.Should().BeNull();
        var report = await new NistPublicationService(x.Db, new SystemTenantContext(TenantA), _clock, Language).BuildReportAsync(a, c, s);
        report.Interpretation!.ReviewedByName.Should().BeNull("a publicação não leva revisão de outra aceitação");

        // Quem revisou e depois aceitou não revisa a própria aceitação.
        var byReviewer = await x.Assist.SaveExecutiveSummaryAsync(a, c, s, new SaveNistExecutiveSummaryCommand(sections, null, again.Version), Revisora);
        await FluentActions.Awaiting(() => x.Assist.ReviewExecutiveSummaryAsync(a, c, s, new ReviewNistExecutiveSummaryCommand(byReviewer.Version, null), Revisora))
            .Should().ThrowAsync<NistAssessmentValidationException>();

        // Registro anterior à regra (revisão mantida com o revisor como autor da aceitação) não conta como revisado.
        var entity = await x.Db.NistExecutiveSummaries.SingleAsync();
        entity.ReviewedByAccountId = Revisora.AccountId;
        entity.ReviewedByName = "Revisora Demo";
        entity.ReviewedAt = _clock.GetUtcNow();
        entity.ReviewedContentHash = entity.ContentHash;
        await x.Db.SaveChangesAsync();
        x.Db.ChangeTracker.Clear();
        (await x.Assist.GetExecutiveSummaryAsync(a, c, s))!.ReviewCurrent.Should().BeFalse("autor da aceitação e revisor são a mesma pessoa");

        // Concorrência: versão desatualizada é recusada (o frontend nunca adota a versão recém-carregada em silêncio).
        await FluentActions.Awaiting(() => x.Assist.SaveExecutiveSummaryAsync(a, c, s, new SaveNistExecutiveSummaryCommand(sections, null, again.Version), Gestora))
            .Should().ThrowAsync<NistAssessmentConflictException>();
    }

    [Fact]
    public async Task AssistenciaSubcategoria_CatalogoNaoSustentaAfirmacaoSobreOAmbiente()
    {
        var (a, c, s) = await CreateAsync(TenantA);
        await using (var x = For(TenantA))
        {
            var run = await SeedRunAsync(x.Db, new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero), KnightAssessmentMode.Live, KnightSourceState.Completed,
                ("AK-ENTRA-001", KnightIndicatorStatus.Passed, new[] { "PR.AA-01" }));
            await x.Nist.LinkEvidenceAsync(a, c, s, "PR.AA-01", new LinkNistEvidenceCommand("KnightIndicator", null, run, "AK-ENTRA-001", null, null, null, null, null), Gestora);
        }
        var llm = new ScriptedLlm(ctx =>
        {
            string Key(string kind) => ctx.GetProperty("sources").EnumerateArray().First(k => k.GetProperty("kind").GetString() == kind).GetProperty("key").GetString()!;
            return JsonSerializer.Serialize(new
            {
                sections = new Dictionary<string, object>
                {
                    ["outcome"] = new[] { new { text = "O resultado pede identidades gerenciadas.", sources = new[] { Key("Catalog") } } },
                    ["justification"] = new object[]
                    {
                        new { text = "A organização gerencia todas as identidades.", sources = new[] { Key("Catalog") } },
                        new { text = "O controle técnico aprovado apoia parte do resultado.", sources = new[] { Key("Catalog"), Key("KnightIndicator") } },
                    },
                    ["supporting"] = new[] { new { text = "Processo de identidades implementado.", sources = new[] { Key("Catalog") } } },
                },
            });
        });
        await using (var x = For(TenantA, new AegisAssessmentService(llm, StaticAuditorPersonaProvider.Neutral, new RealGate())))
        {
            var view = await x.Assist.AssistSubcategoryAsync(a, c, s, "PR.AA-01", new NistAssistRequest(), Gestora);
            view.Sections.Single(k => k.Key == "outcome").Items.Should().ContainSingle("orientação geral pode citar o catálogo");
            var justification = view.Sections.Single(k => k.Key == "justification").Items;
            justification.Should().ContainSingle().Which.Text.Should().StartWith("O controle técnico aprovado");
            justification.Single().Basis.Should().Be(NistAssistBasis.Fact);
            view.Sections.Single(k => k.Key == "supporting").Items.Should().BeEmpty();
            view.ValidationNotes.Should().Contain(n => n.Contains("2 afirmação(ões) sobre o ambiente apoiadas só no catálogo"));
        }
    }

    // ===================================================================================================
    //  Apoio
    // ===================================================================================================

    private static async Task<Guid> SeedRunAsync(AegisScoreDbContext db, DateTimeOffset completed, KnightAssessmentMode mode, KnightSourceState state,
        params (string Id, KnightIndicatorStatus Status, string[] Codes)[] indicators)
    {
        var run = new KnightAssessmentRun
        {
            Mode = mode, SourceType = KnightSourceType.MicrosoftEntraId, SourceState = state, Source = "Microsoft Entra ID",
            Status = KnightRunStatus.Completed, CatalogVersion = "ak-knight-test", ScoreFormulaVersion = "knight-score-v1",
            StartedAt = completed.AddMinutes(-5), CompletedAt = completed, Score = 50, Coverage = 80,
        };
        db.KnightAssessmentRuns.Add(run);
        foreach (var (id, status, codes) in indicators)
            db.KnightIndicatorResults.Add(new KnightIndicatorResult
            {
                RunId = run.Id, IndicatorId = id, Title = $"Controle {id}", Status = status, Severity = SeverityLevel.High,
                NistCodes = codes.ToList(), SourceType = KnightSourceType.MicrosoftEntraId, CollectedAt = completed,
                Evidence = "3 de 3 administradores com método forte registrado.",
            });
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static async Task<Guid> SeedDocumentAsync(AegisScoreDbContext db, string code, string title, string literal)
    {
        var doc = new GovernanceDocument
        {
            Title = title, Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), FileName = "doc.pdf",
            DocumentDate = new DateOnly(2026, 3, 1), Status = GovernanceStatus.Vigente, AnalysisStatus = AiAnalysisStatus.Analyzed,
            AnalysisSummary = "Política analisada.", AnalyzedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        };
        db.GovernanceDocuments.Add(doc);
        db.DocumentControlMappings.Add(new DocumentControlMapping { GovernanceDocumentId = doc.Id, SubcategoryCode = code, Confidence = 0.9, EvidenceQuote = literal });
        await db.SaveChangesAsync();
        return doc.Id;
    }

    private sealed class NoLlm : ILLMClient
    {
        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new InvalidOperationException("O provedor real nunca é chamado sem dublê.");
    }

    /// <summary>Provedor dublê da assistência NIST: lê o bloco de contexto e responde por roteiro.</summary>
    private sealed class ScriptedLlm : ILLMClient
    {
        private readonly Func<JsonElement, string> _respond;
        public ScriptedLlm(Func<JsonElement, string> respond) => _respond = respond;

        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            const string begin = "<<<BEGIN_CONTEXT";
            var start = userPrompt.IndexOf(begin, StringComparison.Ordinal) + begin.Length;
            var end = userPrompt.IndexOf("END_CONTEXT>>>", StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(userPrompt[start..end]);
            return Task.FromResult(_respond(doc.RootElement.Clone()));
        }
    }

    private sealed class SimulatedGate : IAiFreeTierGate
    {
        public AiMode Mode => AiMode.Simulated;
        public bool ProviderConfigured => false;
        public bool IsExternalAllowedForSlug(string? tenantSlug) => false;
    }

    private sealed class RealGate : IAiFreeTierGate
    {
        public AiMode Mode => AiMode.ExternalEnterprise;
        public bool ProviderConfigured => true;
        public bool IsExternalAllowedForSlug(string? tenantSlug) => true;
    }

    private sealed class DemoSlug : IAiTenantResolver
    {
        public void OverrideTenant(Guid tenantId) { }
        public Task<string?> GetCurrentSlugAsync(CancellationToken ct = default) => Task.FromResult<string?>("demo");
    }
}
