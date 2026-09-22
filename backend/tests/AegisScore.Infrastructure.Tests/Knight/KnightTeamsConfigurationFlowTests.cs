using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Teams;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Posture.Export;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02] O percurso INTEIRO dos controles do Microsoft Teams sobre um ambiente sintético:
/// adaptador de coleta → coletor → configuração normalizada no ADM → persistência → RELEITURA → avaliação →
/// evidências e afetados → fotografia imutável → HTML, CSV e PDF. Nada é montado direto no contexto de
/// avaliação: a regra só vê o que passou pelo ADM.
///
/// Os quatro desfechos que o produto precisa distinguir estão aqui: conforme, inadequado, incompleto (a fonte
/// não informou) e falha de coleta (permissão). Mais o cenário que só existe no Teams: a política padrão da
/// organização atende, mas uma política personalizada atribuída a um grupo não.
/// </summary>
public sealed class KnightTeamsConfigurationFlowTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-7e00-7e00-7e00-7e007e007e00");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-7e00-7e00-7e00-7e007e007e00");

    private readonly SqliteConnection _connection;
    private readonly ITestOutputHelper _output;

    public KnightTeamsConfigurationFlowTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static IReadOnlyList<string> TeamsControlIds =>
        TeamsConfigurationControls.Definitions.Select(d => d.Id).ToList();

    [Fact]
    public async Task Conforme_TodosOsControlesDeTeams_AprovadosPelaColetaRelidaDoAdm()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, TeamsCollectionScenario.Variant.Compliant);
        Dump(run);

        run.SourceType.Should().Be(KnightSourceType.MicrosoftTeams);
        run.SourceState.Should().Be(KnightSourceState.Completed);
        run.CatalogVersion.Should().Be("ak-knight-v6");

        var entity = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        entity.IdentityAcquisitionId.Should().NotBeNull("a avaliação leu a aquisição persistida do ADM");

        // A configuração do Teams foi PERSISTIDA como evidência da aquisição, com contrato e versão.
        var persisted = await db.IdentityConfigurationObservations.AsNoTracking()
            .Where(c => c.AcquisitionId == entity.IdentityAcquisitionId)
            .Select(c => new { c.Kind, c.SchemaVersion, c.ExternalId }).ToListAsync();
        persisted.Select(p => p.Kind).Distinct().Should().BeEquivalentTo(new[]
        {
            ConfigurationObjectKind.TeamsClientConfiguration, ConfigurationObjectKind.TeamsFederationConfiguration,
            ConfigurationObjectKind.TeamsMeetingPolicy, ConfigurationObjectKind.TeamsMessagingPolicy,
            ConfigurationObjectKind.TeamsAppPermissionPolicy, ConfigurationObjectKind.TeamsPolicyAssignment,
        });
        persisted.Should().OnlyContain(p => p.SchemaVersion.StartsWith("aegis-config-teams-"));
        persisted.Where(p => p.Kind == ConfigurationObjectKind.TeamsMeetingPolicy).Should().HaveCount(2,
            "a política padrão da organização E a personalizada foram preservadas — ler só a global responderia outra pergunta");

        // A aquisição é de OUTRA FONTE: a do Entra ID não é tocada, e nenhuma identidade é inventada.
        var acquisition = await db.IdentityAcquisitions.AsNoTracking().SingleAsync();
        acquisition.Provider.Should().Be(KnightSourceType.MicrosoftTeams);
        acquisition.SourceLabel.Should().Be("Microsoft Teams");
        (await db.IdentityEntities.CountAsync()).Should().Be(0, "a coleta do Teams não observa identidade nenhuma");
        (await db.IdentityEvidenceSnapshots.CountAsync()).Should().Be(0, "o snapshot agregado de identidade é do Entra ID e não pode ser sobrescrito aqui");

        var teams = run.Indicators.Where(i => TeamsControlIds.Contains(i.IndicatorId)).ToList();
        teams.Should().HaveCount(TeamsControlIds.Count);

        // AK-TEAMS-007 fica de fora por um motivo declarado, e ele é do MÉTODO, não deste cenário: a leitura que
        // diria se a política de permissão ainda governa os aplicativos não é suportada com autenticação de
        // aplicativo. Um ambiente conforme não muda isso — por isso ele não aparece aqui como aprovado nem como
        // reprovado. Ver AkTeams007_NaoConclui_ComACausaRealDeclarada_ESemDegradarAColeta.
        teams.Single(i => i.IndicatorId == "AK-TEAMS-007").Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        teams.Where(i => i.IndicatorId != "AK-TEAMS-007")
            .Where(i => i.Status is KnightIndicatorStatus.Exposed or KnightIndicatorStatus.NotEvaluated)
            .Select(i => $"{i.IndicatorId}={i.Status}: {i.Evidence}")
            .Should().BeEmpty("no cenário conforme cada critério AVALIÁVEL tem evidência suficiente");

        // Com a comunicação com contas não gerenciadas desligada, a restrição de ENTRADA não se aplica — e
        // "não se aplica" não é aprovação disfarçada.
        run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-005").Status.Should().Be(KnightIndicatorStatus.NotApplicable);

        // Nenhum controle do Entra ID entra numa execução do Teams: cada fonte avalia o que consegue avaliar.
        run.Indicators.Should().NotContain(i => i.IndicatorId.StartsWith("AK-ENTRA-"));
    }

    [Fact]
    public async Task Inadequado_CadaCriterioReprovado_ComAlcanceDeclarado()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, TeamsCollectionScenario.Variant.NonCompliant);
        Dump(run);

        // AK-TEAMS-007 não reprova nem no ambiente inadequado: sem saber se a configuração lida governa, reprovar
        // seria tão infundado quanto aprovar. A exceção é nominal e o motivo é o do método (ver o cenário conforme).
        run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-007").Status
            .Should().Be(KnightIndicatorStatus.NotEvaluated);

        var naoReprovados = run.Indicators
            .Where(i => TeamsControlIds.Contains(i.IndicatorId) && i.IndicatorId != "AK-TEAMS-007")
            .Where(i => i.Status != KnightIndicatorStatus.Exposed)
            .Select(i => $"{i.IndicatorId}={i.Status}: {i.Evidence}").ToList();
        naoReprovados.Should().BeEmpty("no cenário inadequado cada critério AVALIÁVEL é violado de forma comprovável");

        var svc = ServiceFor(db, TenantA, TeamsCollectionScenario.Variant.NonCompliant);

        // Controle de POLÍTICA: os afetados são as políticas — objeto concreto que precisa mudar, nomeado.
        var lobby = (await svc.GetAffectedObjectsAsync(run.Id, "AK-TEAMS-010", 1, 50, null))!;
        lobby.Items.Should().HaveCount(2).And.OnlyContain(o => o.Kind == KnightAffectedObjectKind.Policy);
        lobby.Items.Select(o => o.DisplayName).Should().Contain("Política padrão da organização");
        lobby.Limitation.Should().Contain("atribuições DIRETAS de política por conta de usuário", "o alcance por conta de usuário não é enumerado, e isso é dito");

        // Controle de CONFIGURAÇÃO do locatário: a configuração é evidência, não entra na contagem de afetados.
        var federacao = run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-003");
        federacao.AffectedObjectCount.Should().Be(0);
        var evidencia = (await svc.GetAffectedObjectsAsync(run.Id, "AK-TEAMS-003", 1, 50, null, default, KnightObjectRelation.Evidence))!;
        evidencia.Items.Should().ContainSingle(o => o.Kind == KnightAffectedObjectKind.TenantSetting);
        evidencia.Items.Single().Detail.Should().Contain("Encontrado:").And.Contain("Esperado:");

        // A narrativa do achado explica o problema e a configuração encontrada — sem jargão de máquina.
        federacao.Evidence.Should().Contain("qualquer organização que não esteja bloqueada");
        run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-001").Evidence
            .Should().Contain("Dropbox").And.Contain("Google Drive");
    }

    [Fact]
    public async Task PoliticaPadraoAtende_MasUmaPersonalizadaNao_ReprovaComAlcanceDoGrupo()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, TeamsCollectionScenario.Variant.CustomPolicyFails);
        Dump(run);

        var lobby = run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-010");
        lobby.Status.Should().Be(KnightIndicatorStatus.Exposed,
            "existir uma política adequada não basta: o critério tem de valer em todas as instâncias atribuíveis");
        lobby.AffectedObjectCount.Should().Be(1);
        lobby.Evidence.Should().Contain("política “Convidados”")
            .And.Contain("A política padrão da organização atende ao critério");

        var svc = ServiceFor(db, TenantA, TeamsCollectionScenario.Variant.CustomPolicyFails);
        var afetados = (await svc.GetAffectedObjectsAsync(run.Id, "AK-TEAMS-010", 1, 50, null))!;
        afetados.Items.Should().ContainSingle(o => o.DisplayName == "Política “Convidados”");
        afetados.Items.Single().Detail
            .Should().Contain("atribuída a 1 grupo(s)").And.Contain("11111111-2222-3333-4444-555555555555")
            .And.Contain("não são enumerados", "o alcance vai até onde a coleta prova, e nem um passo além");

        // A política padrão continua aparecendo como EVIDÊNCIA: é o que sustenta a leitura do ambiente.
        var evidencias = (await svc.GetAffectedObjectsAsync(run.Id, "AK-TEAMS-010", 1, 50, null, default, KnightObjectRelation.Evidence))!;
        evidencias.Items.Should().ContainSingle(o => o.DisplayName == "Política padrão da organização");
    }

    [Fact]
    public async Task Incompleto_FonteNaoInformou_NaoAprovaNemReprova()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, TeamsCollectionScenario.Variant.Incomplete);
        Dump(run);

        var lobby = run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-010");
        lobby.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        lobby.NotEvaluatedReason.Should().Contain("não informou").And.Contain("Quem entra sem passar pelo lobby");
        lobby.AffectedObjectCount.Should().Be(0);

        var canal = run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-002");
        canal.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        canal.NotEvaluatedReason.Should().Contain("não informou");

        // Os demais critérios continuam avaliados: um valor ausente não contamina o resto da coleta.
        run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-008").Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    [Fact]
    public async Task FalhaDeColeta_ControlesDependentesNaoAvaliados_ComOMotivoDaFonte()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, TeamsCollectionScenario.Variant.MeetingPoliciesDenied);
        Dump(run);

        run.SourceState.Should().Be(KnightSourceState.PartialCollection);

        var reuniao = new[] { "AK-TEAMS-008", "AK-TEAMS-009", "AK-TEAMS-010", "AK-TEAMS-011", "AK-TEAMS-012", "AK-TEAMS-013", "AK-TEAMS-014", "AK-TEAMS-015", "AK-TEAMS-016" };
        foreach (var id in reuniao)
        {
            var i = run.Indicators.Single(x => x.IndicatorId == id);
            i.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, id);
            i.NotEvaluatedReason.Should().Contain("Leitor do Teams", id);
        }

        // As leituras que concluíram continuam valendo: a falha de uma não apaga as outras.
        run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-017").Status.Should().Be(KnightIndicatorStatus.Passed);
        run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-001").Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    /// <summary>
    /// A prova de que cada referência de Teams declarada como implementada é mesmo avaliada pelo caminho do
    /// coletor: o MESMO controle aprova num ambiente conforme e reprova num inadequado. Declarar a referência
    /// numa regra não basta.
    /// </summary>
    [Fact]
    public async Task ReferenciasDeTeams_TemAprovacaoEReprovacaoComprovadasPeloColetor()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var ok = await RunAsync(db, TenantA, TeamsCollectionScenario.Variant.Compliant);
        var bad = await RunAsync(db, TenantA, TeamsCollectionScenario.Variant.NonCompliant);

        var implementadas = KnightReferenceCatalog.Coverage().Controls
            .Where(c => c.Disposition is KnightReferenceDisposition.Implemented or KnightReferenceDisposition.Partial)
            .SelectMany(c => c.IndicatorIds.Select(id => (Reference: c.Control.Key, Id: id)))
            .Where(x => x.Id.StartsWith("AK-TEAMS-", StringComparison.Ordinal))
            .ToList();
        // 16 das 17 referências de Microsoft Teams do catálogo. A 17ª (8.4.1) NÃO é avaliada: a leitura que diria
        // qual modelo governa os aplicativos não é suportada com autenticação de aplicativo, então ela está
        // declarada como "exige acesso que o conector não tem" e não é citada por controle nenhum.
        implementadas.Should().HaveCount(16);
        implementadas.Should().NotContain(x => x.Reference.EndsWith(":8.4.1", StringComparison.Ordinal));

        var lacunas = new List<string>();
        foreach (var (reference, id) in implementadas)
        {
            var conforme = ok.Indicators.SingleOrDefault(i => i.IndicatorId == id)?.Status;
            var inadequado = bad.Indicators.SingleOrDefault(i => i.IndicatorId == id)?.Status;

            // AK-TEAMS-005 só existe quando a comunicação com contas não gerenciadas está ligada; no ambiente
            // conforme ela está desligada, e o controle se declara NÃO APLICÁVEL — que não é aprovação.
            var conformeOk = conforme == KnightIndicatorStatus.Passed
                || (id == "AK-TEAMS-005" && conforme == KnightIndicatorStatus.NotApplicable);
            if (!conformeOk || inadequado != KnightIndicatorStatus.Exposed)
                lacunas.Add($"{reference} → {id}: conforme={conforme}, inadequado={inadequado}");
        }
        lacunas.Should().BeEmpty("uma referência só conta como implementada quando o controle aprova e reprova pelo caminho do coletor");
    }

    [Fact]
    public async Task SincronizarTeams_NaoDescartaAAvaliacaoDoEntraId()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);

        var entra = await KnightMulticloudReportTests.ServiceFor(db, TenantA).RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);
        var teams = await RunAsync(db, TenantA, TeamsCollectionScenario.Variant.Compliant);

        // A leitura COMPOSTA devolve as duas fontes, cada uma com a própria nota e a própria data.
        var porFonte = await ServiceFor(db, TenantA, TeamsCollectionScenario.Variant.Compliant).GetLatestBySourceAsync();
        porFonte.Sources.Select(s => s.Source).Should().BeEquivalentTo(new[] { KnightSourceType.MicrosoftEntraId, KnightSourceType.MicrosoftTeams });
        porFonte.Sources.Single(s => s.Source == KnightSourceType.MicrosoftEntraId).Assessment!.Id.Should().Be(entra.Id);
        porFonte.Sources.Single(s => s.Source == KnightSourceType.MicrosoftTeams).Assessment!.Id.Should().Be(teams.Id);
        porFonte.Sources.Select(s => s.Label).Should().BeEquivalentTo(new[] { "Microsoft Entra ID", "Microsoft Teams" });

        // A evidência de identidade do Entra ID continua intacta depois da coleta do Teams.
        var snapshot = await db.IdentityEvidenceSnapshots.AsNoTracking().SingleAsync();
        snapshot.Should().NotBeNull();
        (await db.IdentityAcquisitions.AsNoTracking().CountAsync(a => a.Provider == KnightSourceType.MicrosoftEntraId)).Should().Be(1);
        (await db.IdentityAcquisitions.AsNoTracking().CountAsync(a => a.Provider == KnightSourceType.MicrosoftTeams)).Should().Be(1);
    }

    [Fact]
    public async Task Relatorio_FiltraPorTeams_ComAsAquisicoesIdentificadas_EContagensReconciliadas()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, TeamsCollectionScenario.Variant.NonCompliant);

        var published = await PostureFor(db, TenantA).PublishAsync(
            PostureSnapshotType.Knight, KnightSourceType.MicrosoftTeams, run.Id);
        var snapshotId = published.Summary.Id;
        var snapshot = await db.PostureSnapshots.AsNoTracking()
            .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
            .SingleAsync(s => s.Id == snapshotId);

        snapshot.SourceType.Should().Be(KnightSourceType.MicrosoftTeams);
        snapshot.Indicators.Should().OnlyContain(i => i.IndicatorId.StartsWith("AK-TEAMS-"));

        var model = KnightReportModelBuilder.Build(snapshot, true);
        model.Header.SourceType.Should().Be("MicrosoftTeams");
        model.Header.Provider.Should().Be("Microsoft");
        model.ByService.Should().ContainSingle(r => r.Label == "Microsoft Teams");
        model.ByPlatform.Should().NotBeNull();
        model.ByPlatform!.Should().ContainSingle(r => r.Label == "Microsoft 365");
        model.Controls.Should().OnlyContain(c => c.Service == "Microsoft Teams" && c.Platform == "Microsoft 365");
        model.Controls.Should().OnlyContain(c => c.Impact != null);
        // Todo controle cita a referência que avalia — menos o que não avalia nenhuma. Ver a justificativa em
        // ControlesDoTeams_SoConsomemCapacidadesDoColetorReal.
        model.Controls.Where(c => c.Id != "AK-TEAMS-007")
            .Should().OnlyContain(c => c.References.Any(r => r.Framework.StartsWith("CIS ")));
        // O relatório cita o framework do controle (o vínculo é que não existe): a lista de referências do
        // AK-TEAMS-007 não traz nenhum controle CIS, e o motivo aparece no texto de não avaliado.
        var aplicativos = model.Controls.Single(c => c.Id == "AK-TEAMS-007");
        aplicativos.References.Should().NotContain(r => r.Framework.StartsWith("CIS "));
        aplicativos.NotEvaluatedReason.Should().Contain("NÃO SUPORTADOS com autenticação de aplicativo");

        // A contagem de ocorrências do relatório é a soma dos afetados dos controles reprovados — e não outra.
        var occurrences = model.Controls.Where(c => c.Status is "Exposed" or "Mitigated")
            .Sum(c => c.Objects.Count(o => o.Relation == "Affected"));
        occurrences.Should().Be(model.Kpis.Occurrences);

        var exporter = new PostureSnapshotExporter(db);
        var html = Encoding.UTF8.GetString((await exporter.ExportAsync(snapshotId, PostureExportFormat.Html))!.Content);
        html.Should().Contain("Microsoft Teams").And.Contain("Impacto potencial");
        html.Should().NotContain("<script src").And.NotContain("<link", "o relatório abre offline, sem buscar nada");

        var csv = Encoding.UTF8.GetString((await exporter.ExportAsync(snapshotId, PostureExportFormat.Csv))!.Content).TrimStart('﻿');
        csv.Should().Contain("AK-TEAMS-010").And.Contain("Política").And.NotContain("objeto(s)");

        var pdf = (await exporter.ExportAsync(snapshotId, PostureExportFormat.Pdf))!.Content;
        Encoding.ASCII.GetString(pdf, 0, 4).Should().Be("%PDF");

        // HTML, CSV e PDF saem da MESMA fotografia: o número de controles não diverge entre eles.
        model.Controls.Should().HaveCount(snapshot.Indicators.Count);
    }

    [Fact]
    public async Task IsolamentoEntreClientes_ConfiguracaoDoTeamsNaoVazaParaOutroTenant()
    {
        await SeedAsync(TenantA);
        await SeedAsync(TenantB);
        await using (var db = NewContext(TenantA))
            await RunAsync(db, TenantA, TeamsCollectionScenario.Variant.NonCompliant);

        await using var dbB = NewContext(TenantB);
        (await dbB.IdentityConfigurationObservations.CountAsync()).Should().Be(0);
        (await dbB.IdentityAcquisitions.CountAsync()).Should().Be(0);
        (await dbB.KnightAssessmentRuns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AbrirALeituraPorFonte_NaoDisparaColeta()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var reader = new TeamsCollectionScenario(TeamsCollectionScenario.Variant.Compliant);
        var svc = ServiceFor(db, TenantA, reader);

        await svc.RunAssessmentAsync(KnightSourceType.MicrosoftTeams);
        var depois = reader.Reads;

        await svc.GetLatestBySourceAsync();
        await svc.GetLatestAsync();
        reader.Reads.Should().Be(depois, "ler o resultado persistido nunca consulta a fonte");
    }

    [Fact]
    public async Task ConectorDesabilitado_NaoColeta_ENaoApagaOQueJaExiste()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        await RunAsync(db, TenantA, TeamsCollectionScenario.Variant.Compliant);

        var connector = await db.Connectors.SingleAsync();
        connector.Enabled = false;
        await db.SaveChangesAsync();

        await FluentActions.Awaiting(() => RunAsync(db, TenantA, TeamsCollectionScenario.Variant.Compliant))
            .Should().ThrowAsync<KnightSourceNotConfiguredException>();

        (await db.IdentityAcquisitions.CountAsync(a => a.Provider == KnightSourceType.MicrosoftTeams))
            .Should().Be(1, "a coleta recusada não destrói a evidência anterior");
    }

    // ======================================================================================================
    //  [Revisão dirigida] Os defeitos, reproduzidos pelo caminho INTEIRO: coletor → ADM → releitura → avaliação
    // ======================================================================================================

    /// <summary>
    /// AK-TEAMS-010 com a admissão automática em “pessoas que foram convidadas”. Pela documentação oficial esse
    /// valor deixa passar convidados e participantes externos convidados — logo, o percurso completo precisa
    /// REPROVAR, e o texto publicado não pode afirmar que só entram pessoas da organização.
    /// </summary>
    [Fact]
    public async Task InvitedUsers_ReprovaNoPercursoCompleto_EOTextoPublicadoDescreveOValor()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);

        var run = await ServiceFor(db, TenantA,
                new TeamsCollectionScenario(TeamsCollectionScenario.Variant.Compliant, autoAdmittedUsers: "InvitedUsers"))
            .RunAssessmentAsync(KnightSourceType.MicrosoftTeams);
        Dump(run);

        var lobby = run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-010");
        lobby.Status.Should().Be(KnightIndicatorStatus.Exposed);
        lobby.Evidence.Should().NotContain("Somente pessoas da organização entram");

        // E o relatório publicado carrega a descrição precisa do valor encontrado.
        var published = await PostureFor(db, TenantA).PublishAsync(
            PostureSnapshotType.Knight, KnightSourceType.MicrosoftTeams, run.Id);
        var html = Encoding.UTF8.GetString(
            (await new PostureSnapshotExporter(db).ExportAsync(published.Summary.Id, PostureExportFormat.Html))!.Content);
        html.Should().Contain("CONVIDADOS").And.Contain("encaminhado");
    }

    /// <summary>
    /// Registros de política sem identificação, com valores conformes e sem Global explícita: pelo percurso
    /// inteiro isso é dado insuficiente — a avaliação NÃO aprova e o ADM preserva os registros separados.
    /// </summary>
    [Fact]
    public async Task PoliticasSemIdentificacao_NaoAprovamOAmbiente_ENaoSeFundemNoAdm()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);

        var run = await RunAsync(db, TenantA, TeamsCollectionScenario.Variant.UnidentifiedPolicies);
        Dump(run);

        var deReuniao = run.Indicators
            .Where(i => i.IndicatorId is "AK-TEAMS-008" or "AK-TEAMS-010" or "AK-TEAMS-013" or "AK-TEAMS-016")
            .ToList();
        deReuniao.Should().OnlyContain(i => i.Status == KnightIndicatorStatus.NotEvaluated);
        deReuniao.Should().OnlyContain(i => i.NotEvaluatedReason!.Contains("SEM identificação"));

        var entity = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        var politicas = await db.IdentityConfigurationObservations.AsNoTracking()
            .Where(c => c.AcquisitionId == entity.IdentityAcquisitionId
                && c.Kind == ConfigurationObjectKind.TeamsMeetingPolicy)
            .Select(c => c.ExternalId)
            .ToListAsync();

        politicas.Should().HaveCount(3).And.OnlyHaveUniqueItems("três registros distintos, três documentos");
        politicas.Should().NotContain("TeamsMeetingPolicy:Global", "nada aqui é a política padrão da organização");
    }

    /// <summary>
    /// [Revisão dirigida] AK-TEAMS-007 pelo fluxo COMPLETO (coletor → ADM → avaliação): mesmo num locatário
    /// inteiramente conforme, com a política de permissão restrita a listas de permitidos, o critério NÃO
    /// CONCLUI — e o motivo nomeia a causa real, que é a incompatibilidade do comando de leitura do modelo
    /// ACM/UAM com a autenticação de aplicativo, não uma permissão faltando.
    ///
    /// Também se verifica aqui que a coleta continua COMPLETA: a leitura incompatível não é mais tentada, então
    /// não há capacidade recusada. Um critério sem veredito não é um defeito de coleta.
    /// </summary>
    [Fact]
    public async Task AkTeams007_NaoConclui_ComACausaRealDeclarada_ESemDegradarAColeta()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);

        var run = await ServiceFor(db, TenantA,
                new TeamsCollectionScenario(TeamsCollectionScenario.Variant.Compliant))
            .RunAssessmentAsync(KnightSourceType.MicrosoftTeams);
        Dump(run);

        var apps = run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-007");
        apps.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        apps.NotEvaluatedReason.Should().Contain("NÃO SUPORTADOS com autenticação de aplicativo");
        apps.NotEvaluatedReason.Should().NotContain("Leitor do Teams");

        // A configuração legada foi coletada, persistida e preservada como evidência do achado — verificada na
        // fotografia publicada, que é o que chega ao relatório.
        var publicado = await PostureFor(db, TenantA).PublishAsync(
            PostureSnapshotType.Knight, KnightSourceType.MicrosoftTeams, run.Id);
        var fotografia = await db.PostureSnapshots.AsNoTracking().Include(f => f.Objects)
            .SingleAsync(f => f.Id == publicado.Summary.Id);
        fotografia.Objects.Where(o => o.IndicatorId == "AK-TEAMS-007")
            .Should().Contain(o => o.Detail!.Contains("preservada como evidência"));

        // Os outros controles do cenário conforme continuam aprovando, e a coleta não foi degradada.
        run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-010").Status.Should().Be(KnightIndicatorStatus.Passed);
        run.SourceState.Should().Be(KnightSourceState.Completed);
        run.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.Collected);
    }

    /// <summary>
    /// Leitura OMITIDA pelo adaptador: a coleta não pode ser declarada concluída, e o controle que dependia
    /// dela fica não avaliado com o motivo.
    /// </summary>
    [Fact]
    public async Task LeituraOmitida_NaoViraColetaCompleta_NemControleAprovado()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);

        var run = await RunAsync(db, TenantA, TeamsCollectionScenario.Variant.ReadOmitted);
        Dump(run);

        run.SourceState.Should().Be(KnightSourceState.PartialCollection);
        run.SourceState.Should().NotBe(KnightSourceState.Completed);

        var relato = run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-017");
        relato.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    /// <summary>
    /// Nenhum marcador sintético de segredo sobrevive até o que é PERSISTIDO e PUBLICADO quando a conexão falha
    /// com diagnóstico contaminado. A orientação útil continua lá.
    /// </summary>
    [Fact]
    public async Task SegredoNoDiagnostico_NaoChegaAoAdm_NemAoRelatorio()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);

        var run = await ServiceFor(db, TenantA, new ConexaoComSegredoNoDiagnostico())
            .RunAssessmentAsync(KnightSourceType.MicrosoftTeams);
        Dump(run);

        var published = await PostureFor(db, TenantA).PublishAsync(
            PostureSnapshotType.Knight, KnightSourceType.MicrosoftTeams, run.Id);
        var exporter = new PostureSnapshotExporter(db);
        var html = Encoding.UTF8.GetString((await exporter.ExportAsync(published.Summary.Id, PostureExportFormat.Html))!.Content);
        var csv = Encoding.UTF8.GetString((await exporter.ExportAsync(published.Summary.Id, PostureExportFormat.Csv))!.Content);

        var tudo = string.Join(" | ",
            string.Join(" ", run.Capabilities.Select(c => c.Detail)),
            string.Join(" ", run.Indicators.Select(i => i.Evidence + " " + i.NotEvaluatedReason)),
            html, csv);

        tudo.Should().NotContain(ConexaoComSegredoNoDiagnostico.MarcadorDeToken);
        tudo.Should().NotContain(ConexaoComSegredoNoDiagnostico.MarcadorDeSegredo);
        tudo.Should().NotContain("Bearer ");
        tudo.Should().Contain("Leitor do Teams", "a orientação sobre a causa é preservada");
    }

    // ---- Infraestrutura -----------------------------------------------------------------------------

    /// <summary>
    /// Adaptador que devolve, no diagnóstico da conexão, marcadores SINTÉTICOS com forma de segredo. Nenhuma
    /// credencial real: são cadeias inventadas para este teste.
    /// </summary>
    private sealed class ConexaoComSegredoNoDiagnostico : ITeamsAdminReader
    {
        internal const string MarcadorDeToken = "eyJhZWdpcyI6ImZsdXhvIn0.QUVHSVMtRkxVWE8tU0lOVEVUSUNP.YXNzaW5hdHVyYQ";
        internal const string MarcadorDeSegredo = "client_secret=AEGIS-SEGREDO-SINTETICO-do-fluxo-0001";

        public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(PowerShellTeamsAdminReader.Parse(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    runtime = new { powerShell = "7.4.6", module = "6.9.0", platform = "Unix" },
                    connected = false,
                    connectionErrorCategory = "InsufficientPermission",
                    connectionErrorId = MarcadorDeToken + " " + MarcadorDeSegredo,
                    reads = Array.Empty<object>(),
                })));

        public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) => ReadAsync(null!, ct);
    }

    private Task<KnightAssessment> RunAsync(AegisScoreDbContext db, Guid tenant, TeamsCollectionScenario.Variant variant) =>
        ServiceFor(db, tenant, variant).RunAssessmentAsync(KnightSourceType.MicrosoftTeams);

    private static IAegisKnightAssessmentService ServiceFor(AegisScoreDbContext db, Guid tenantId, TeamsCollectionScenario.Variant variant) =>
        ServiceFor(db, tenantId, new TeamsCollectionScenario(variant));

    internal static IAegisKnightAssessmentService ServiceFor(AegisScoreDbContext db, Guid tenantId, ITeamsAdminReader reader)
    {
        var tenant = new SystemTenantContext(tenantId);
        var registry = new KnightCollectorRegistry(new IKnightCollector[]
        {
            new EntraIdKnightCollector(new EntraGraphClient(new System.Net.Http.HttpClient(new KnightGraphScenario.StubHandler()))),
            new TeamsKnightCollector(new StubTokens(), reader),
        });
        var config = new MicrosoftConfig();
        var store = new IdentityAcquisitionStore(db, tenant, TimeProvider.System);
        var evidence = new IdentityEvidenceService(db, registry, config, store, tenant);
        return new AegisKnightAssessmentService(db, registry, config, new KnightMulticloudReportTests.SemIa(), evidence, store, tenant);
    }

    private static IPostureSnapshotService PostureFor(AegisScoreDbContext db, Guid tenantId) =>
        new PostureSnapshotService(db, new SystemTenantContext(tenantId), new AegisScore.Infrastructure.Connectors.NistSignalMapper(db));

    /// <summary>O MESMO conector Microsoft resolve as duas fontes — é o mesmo registro de aplicação.</summary>
    private sealed class MicrosoftConfig : IKnightSourceConfigurationProvider
    {
        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default) =>
            Task.FromResult<KnightSourceConfiguration>(source switch
            {
                KnightSourceType.MicrosoftEntraId => new KnightEntraIdConfiguration("dir-demo-0001", "client", "secret"),
                KnightSourceType.MicrosoftTeams => new KnightTeamsConfiguration("dir-demo-0001", "client", "secret"),
                _ => new KnightSourceNotConfigured(source),
            });

        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }

    private sealed class StubTokens : ITeamsTokenClient
    {
        public Task<TeamsAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(new TeamsAdminCredentials(credentials.AzureTenantId, "graph", "teams"));
    }

    private void Dump(KnightAssessment run)
    {
        foreach (var i in run.Indicators.Where(i => i.IndicatorId.StartsWith("AK-TEAMS-")).OrderBy(i => i.IndicatorId, StringComparer.Ordinal))
            _output.WriteLine($"{i.IndicatorId} {i.Status} [{i.AffectedObjectCount}] {i.Evidence}");
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private async Task SeedAsync(Guid tenantId)
    {
        await using (var db = NewContext(null))
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Cliente Demo", Slug = $"t-{tenantId:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using var dbt = NewContext(tenantId);
        dbt.Connectors.Add(new ConnectorConfig
        {
            TenantId = tenantId, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
            DisplayName = "Microsoft 365", Enabled = true, EncryptedSettings = "cifrado",
        });
        await dbt.SaveChangesAsync();
    }
}
