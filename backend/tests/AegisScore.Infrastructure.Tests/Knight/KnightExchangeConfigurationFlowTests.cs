using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Exchange;
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
/// [AEGIS-KNIGHT-COVERAGE-03] O percurso INTEIRO dos controles do Exchange Online sobre um ambiente sintético:
/// adaptador de coleta → coletor → configuração normalizada no ADM → persistência → RELEITURA → avaliação →
/// evidências e afetados → fotografia imutável → HTML, CSV e PDF. Nada é montado direto no contexto de
/// avaliação: a regra só vê o que passou pelo ADM.
///
/// Cada teste aqui reproduz uma armadilha do Exchange que um caminho descuidado transformaria em afirmação
/// falsa — herança, enumeração truncada, leitura omitida, mecanismo faltando, regra inerte e texto hostil.
/// </summary>
public sealed class KnightExchangeConfigurationFlowTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-e0e0-e0e0-e0e0-e0e0e0e0e0e0");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-e0e0-e0e0-e0e0-e0e0e0e0e0e0");

    private readonly SqliteConnection _connection;
    private readonly ITestOutputHelper _output;

    public KnightExchangeConfigurationFlowTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static IReadOnlyList<string> ExchangeControlIds =>
        ExchangeConfigurationControls.Definitions.Select(d => d.Id).ToList();

    // ======================================================================================================
    //  Os quatro desfechos que o produto precisa distinguir
    // ======================================================================================================

    [Fact]
    public async Task Conforme_TodosOsControlesDeExchange_AprovadosPelaColetaRelidaDoAdm()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.Compliant);
        Dump(run);

        run.SourceType.Should().Be(KnightSourceType.MicrosoftExchangeOnline);
        run.SourceState.Should().Be(KnightSourceState.Completed);
        run.CatalogVersion.Should().Be("ak-knight-v6");

        var entity = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        entity.IdentityAcquisitionId.Should().NotBeNull("a avaliação leu a aquisição persistida do ADM");

        // A configuração foi PERSISTIDA como evidência da aquisição, com contrato e versão.
        var persisted = await db.IdentityConfigurationObservations.AsNoTracking()
            .Where(c => c.AcquisitionId == entity.IdentityAcquisitionId)
            .Select(c => new { c.Kind, c.SchemaVersion, c.ExternalId }).ToListAsync();
        persisted.Should().OnlyContain(p => p.SchemaVersion.StartsWith("aegis-config-exo-"));
        persisted.Select(p => p.Kind).Should().Contain(new[]
        {
            ConfigurationObjectKind.ExchangeOrganizationConfiguration,
            ConfigurationObjectKind.ExchangeTransportConfiguration,
            ConfigurationObjectKind.ExchangeSharingPolicy,
            ConfigurationObjectKind.ExchangeOwaMailboxPolicy,
            ConfigurationObjectKind.ExchangeRoleAssignmentPolicy,
            ConfigurationObjectKind.ExchangeExternalSenderIdentification,
            ConfigurationObjectKind.ExchangeOutboundSpamFilterPolicy,
            ConfigurationObjectKind.ExchangeSharedMailboxInventory,
            ConfigurationObjectKind.ExchangeMailboxAuditInventory,
            ConfigurationObjectKind.ExchangeMailboxForwardingInventory,
            ConfigurationObjectKind.ExchangePolicyReachInventory,
            ConfigurationObjectKind.ExchangeSmtpAuthOverrideInventory,
            ConfigurationObjectKind.ExchangeOwaPolicyReachInventory,
            ConfigurationObjectKind.ExchangeAuditBypassInventory,
        });
        persisted.Count(p => p.Kind == ConfigurationObjectKind.ExchangeSharingPolicy).Should().Be(2,
            "a política padrão E a desligada foram preservadas — a desligada é evidência, não desaparece");

        // A aquisição é de OUTRA FONTE: a do Entra ID e a do Teams não são tocadas, e nenhuma identidade é inventada.
        var acquisition = await db.IdentityAcquisitions.AsNoTracking().SingleAsync();
        acquisition.Provider.Should().Be(KnightSourceType.MicrosoftExchangeOnline);
        acquisition.SourceLabel.Should().Be("Exchange Online");
        (await db.IdentityEntities.CountAsync()).Should().Be(0, "a coleta do Exchange não observa identidade nenhuma");
        (await db.IdentityEvidenceSnapshots.CountAsync()).Should().Be(0,
            "o snapshot agregado de identidade é do Entra ID e não pode ser sobrescrito aqui");

        var exo = run.Indicators.Where(i => ExchangeControlIds.Contains(i.IndicatorId)).ToList();
        exo.Should().HaveCount(ExchangeControlIds.Count).And.HaveCount(17);
        exo.Where(i => i.Status is KnightIndicatorStatus.Exposed or KnightIndicatorStatus.NotEvaluated)
            .Select(i => $"{i.IndicatorId}={i.Status}: {i.Evidence}")
            .Should().BeEmpty("no cenário conforme cada critério tem evidência suficiente e aprova");

        // Nenhum controle de outra fonte entra numa execução do Exchange.
        run.Indicators.Should().NotContain(i => i.IndicatorId.StartsWith("AK-ENTRA-"));
        run.Indicators.Should().NotContain(i => i.IndicatorId.StartsWith("AK-TEAMS-"));
    }

    [Fact]
    public async Task Inadequado_CadaCriterioReprovado_ComOsObjetosQueOSustentam()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.NonCompliant);
        Dump(run);

        var exo = run.Indicators.Where(i => ExchangeControlIds.Contains(i.IndicatorId)).ToList();
        exo.Should().OnlyContain(i => i.Status == KnightIndicatorStatus.Exposed,
            "no cenário inadequado cada critério tem violação demonstrável");

        var svc = ServiceFor(db, TenantA, new ExchangeCollectionScenario(ExchangeCollectionScenario.Variant.NonCompliant));

        // A caixa compartilhada reprovada é NOMEADA pelo endereço — não "1 objeto".
        var compartilhada = (await svc.GetAffectedObjectsAsync(run.Id, "AK-EXO-001", 1, 50, null))!;
        compartilhada.Items.Should().ContainSingle(o => o.UserPrincipalName == "faturamento@example.com");

        // A auditoria reprova pelo tipo de acesso que FALTA, e não pelos três: eles são independentes.
        var auditoria = (await svc.GetAffectedObjectsAsync(run.Id, "AK-EXO-006", 1, 50, null))!;
        auditoria.Items.Should().ContainSingle(o => o.UserPrincipalName == "ana.silva@example.com");
        auditoria.Items.Single().Detail.Should().Contain("acesso delegado").And.NotContain("acesso do dono");

        // O encaminhamento reprova nos TRÊS mecanismos, cada um com o seu objeto nomeado.
        var encaminhamento = (await svc.GetAffectedObjectsAsync(run.Id, "AK-EXO-008", 1, 50, null))!;
        encaminhamento.Items.Should().Contain(o => o.DisplayName!.Contains("Default"), "a política de saída libera o encaminhamento");
        encaminhamento.Items.Should().Contain(o => o.UserPrincipalName == "ana.silva@example.com", "a caixa tem encaminhamento configurado");
        encaminhamento.Items.Should().Contain(o => o.DisplayName!.Contains("Copia auditoria"), "a regra de transporte desvia mensagens");
    }

    // ======================================================================================================
    //  HERANÇA — a armadilha nº 1 do Exchange
    // ======================================================================================================

    /// <summary>
    /// O SMTP AUTH está desabilitado na ORGANIZAÇÃO e UMA caixa o habilita explicitamente. Ler só a organização
    /// aprovaria o ambiente descrevendo o padrão — e o padrão é justamente o que a substituição ignora.
    ///
    /// O teste também fixa a leitura do valor AUSENTE: a outra caixa não declara nada, e isso é HERANÇA, nunca
    /// "desabilitado na caixa". Um contrato que colapsasse os dois inverteria a contagem publicada.
    /// </summary>
    [Fact]
    public async Task SmtpAuth_SubstituicaoPorCaixa_ReprovaMesmoComAOrganizacaoDesabilitando()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.SmtpAuthOverride);
        Dump(run);

        var smtp = run.Indicators.Single(i => i.IndicatorId == "AK-EXO-016");
        smtp.Status.Should().Be(KnightIndicatorStatus.Exposed);
        smtp.Evidence.Should().Contain("desabilitado na organização").And.Contain("substituição");
        var afetadasSmtp = (await ServiceFor(db, TenantA, new ExchangeCollectionScenario(ExchangeCollectionScenario.Variant.SmtpAuthOverride))
            .GetAffectedObjectsAsync(run.Id, "AK-EXO-016", 1, 50, null))!;
        afetadasSmtp.Items.Should().ContainSingle(o => o.UserPrincipalName == "ana.silva@example.com");

        // A caixa que NÃO declarou valor é contada como HERDEIRA, não como desabilitada.
        var entity = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        var config = await ReadConfigurationAsync(db, entity.IdentityAcquisitionId!.Value);
        var inv = config.Read<ExchangeSmtpAuthOverrideInventory>().Single!;
        inv.InheritingOrganizationCount.Should().Be(1);
        inv.ExplicitlyDisabledCount.Should().Be(0);
        inv.ExplicitlyEnabled.Should().ContainSingle(m => m.SmtpClientAuthenticationDisabled == false);

        // E a leitura da substituição é OBRIGATÓRIA: sem ela o controle não conclui, em vez de aprovar.
        var soTransporte = new[] { new KnightCapabilityStatus(KnightCapability.ExchangeTransportConfig, KnightCapabilityOutcome.Collected) };
        var semSubstituicoes = new KnightEvaluationContext(
            KnightFactSet.Empty, soTransporte, null,
            new KnightTenantConfiguration(
                config.Documents.Where(d => d.Kind == ConfigurationObjectKind.ExchangeTransportConfiguration),
                soTransporte),
            Array.Empty<KnightAffectedObjectEvidence>(), DateTimeOffset.UtcNow);
        var semLeitura = ExchangeConfigurationControls.Definitions.Single(d => d.Id == "AK-EXO-016").Evaluate!(semSubstituicoes);
        semLeitura.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        semLeitura.NotEvaluatedReason.Should().Contain("HERANÇA");
    }

    // ======================================================================================================
    //  ENUMERAÇÃO LIMITADA — a armadilha nº 3
    // ======================================================================================================

    /// <summary>
    /// As enumerações atingiram o teto e nada de errado apareceu no trecho lido. Isso NÃO é ambiente sem
    /// problema: os controles que dependem da população ficam não avaliados, e o motivo diz que a leitura não
    /// cobre a organização. Os controles que NÃO dependem de população continuam concluindo normalmente.
    /// </summary>
    [Fact]
    public async Task EnumeracaoTruncada_NaoAprovaAOrganizacao_EDizPorQue()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.Truncated);
        Dump(run);

        var dependentes = run.Indicators
            .Where(i => i.IndicatorId is "AK-EXO-001" or "AK-EXO-006" or "AK-EXO-008" or "AK-EXO-016")
            .ToList();
        dependentes.Should().OnlyContain(i => i.Status == KnightIndicatorStatus.NotEvaluated);
        dependentes.Should().OnlyContain(i => i.NotEvaluatedReason!.Contains("não cobre a organização inteira")
            || i.NotEvaluatedReason!.Contains("teto"));

        // Os critérios que vivem numa configuração única continuam concluindo: o teto não contamina o que não é população.
        run.Indicators.Single(i => i.IndicatorId == "AK-EXO-003").Status.Should().Be(KnightIndicatorStatus.Passed);
        run.Indicators.Single(i => i.IndicatorId == "AK-EXO-013").Status.Should().Be(KnightIndicatorStatus.Passed);

        // E o alcance das políticas diz, com todas as letras, que a contagem não cobre a organização.
        var suplementos = run.Indicators.Single(i => i.IndicatorId == "AK-EXO-011");
        suplementos.Status.Should().Be(KnightIndicatorStatus.Passed);
        suplementos.Evidence.Should().Contain("teto");
    }

    // ======================================================================================================
    //  LEITURAS QUE FALTAM — recusada, omitida, e o mecanismo ausente
    // ======================================================================================================

    [Fact]
    public async Task LeituraRecusada_DeixaOsControlesQueDependemDelaSemVeredito_ComOComandoNomeado()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.MailboxesDenied);
        Dump(run);

        run.SourceState.Should().Be(KnightSourceState.PartialCollection, "onze leituras concluíram; uma foi recusada");

        var capacidade = run.Capabilities.Single(c => c.Capability == KnightCapability.ExchangeMailboxes);
        capacidade.Outcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        capacidade.Detail.Should().Contain("Get-Mailbox", "o operador precisa saber QUAL comando foi recusado");
        capacidade.Detail.Should().Contain("Leitor Global", "e qual papel cobriria a leitura");

        foreach (var id in new[] { "AK-EXO-001", "AK-EXO-006", "AK-EXO-008" })
            run.Indicators.Single(i => i.IndicatorId == id).Status
                .Should().Be(KnightIndicatorStatus.NotEvaluated, id);

        // As leituras que concluíram continuam valendo — uma recusa não contamina as outras.
        run.Indicators.Single(i => i.IndicatorId == "AK-EXO-005").Status.Should().Be(KnightIndicatorStatus.Passed);
        run.Indicators.Single(i => i.IndicatorId == "AK-EXO-007").Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    /// <summary>
    /// A leitura das CONTAS foi omitida. "Caixa compartilhada" e "conta bloqueada" são dois fatos: sem o
    /// segundo, o critério não conclui — e o motivo publicado diz exatamente isso, em vez de aprovar pelo tipo
    /// da caixa.
    /// </summary>
    [Fact]
    public async Task LeituraOmitida_CaixaCompartilhadaNaoViraContaBloqueada()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.SignInReadOmitted);
        Dump(run);

        run.Capabilities.Single(c => c.Capability == KnightCapability.ExchangeMailboxSignIn).Outcome
            .Should().Be(KnightCapabilityOutcome.NotAttempted, "leitura omitida não é leitura concluída sem registros");
        run.SourceState.Should().Be(KnightSourceState.PartialCollection);

        var compartilhadas = run.Indicators.Single(i => i.IndicatorId == "AK-EXO-001");
        compartilhadas.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        compartilhadas.NotEvaluatedReason.Should().Contain("NÃO significa que a conta correspondente esteja bloqueada");

        // O inventário ainda preserva as caixas encontradas — o que falta é o estado delas, e ele é declarado.
        var entity = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        var inv = (await ReadConfigurationAsync(db, entity.IdentityAcquisitionId!.Value))
            .Read<ExchangeSharedMailboxInventory>().Single!;
        inv.SharedMailboxTotal.Should().Be(1);
        inv.SignInStateResolved.Should().BeFalse();
        inv.Mailboxes.Should().OnlyContain(m => m.SignInBlocked == null);
    }

    /// <summary>
    /// A armadilha nº 2: "todas as formas de encaminhamento bloqueadas" é uma afirmação sobre TRÊS mecanismos.
    /// Com um deles não lido, o controle não conclui e NOMEIA o que falta — ainda que os outros dois estejam
    /// impecáveis.
    /// </summary>
    [Fact]
    public async Task UmMecanismoDeEncaminhamentoNaoLido_ImpedeDeclararTodasAsFormasBloqueadas()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.ForwardingMechanismMissing);
        Dump(run);

        var encaminhamento = run.Indicators.Single(i => i.IndicatorId == "AK-EXO-008");
        encaminhamento.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        encaminhamento.NotEvaluatedReason.Should().Contain("TRÊS mecanismos");
        encaminhamento.NotEvaluatedReason.Should().Contain("encaminhamento automático das políticas de saída");
        encaminhamento.Evidence.Should().NotContain("foram verificados e estão fechados",
            "nunca declarar bloqueado o que não foi lido");

        // O motivo é GRAVADO: ele cabe na coluna que o guarda, inclusive montado a partir de lista variável.
        (encaminhamento.NotEvaluatedReason!.Length + "Não avaliado: ".Length).Should().BeLessThanOrEqualTo(500);
    }

    // ======================================================================================================
    //  INÉRCIA — uma regra que existe e não age
    // ======================================================================================================

    [Fact]
    public async Task RegraDesabilitada_EhEvidencia_NaoExposicao_ESeguePublicadaComOMotivo()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.InertTransportRule);
        Dump(run);

        var regras = run.Indicators.Single(i => i.IndicatorId == "AK-EXO-009");
        regras.Status.Should().Be(KnightIndicatorStatus.Passed, "a regra existe, mas não age sobre mensagem alguma");
        regras.AffectedObjectCount.Should().Be(0);
        regras.Evidence.Should().Contain("não produzem efeito");

        // Ela NÃO some: continua publicada como evidência, com o estado que a torna inerte.
        var evidencia = (await ServiceFor(db, TenantA, new ExchangeCollectionScenario(ExchangeCollectionScenario.Variant.InertTransportRule))
            .GetAffectedObjectsAsync(run.Id, "AK-EXO-009", 1, 50, null, default, KnightObjectRelation.Evidence))!;
        evidencia.Items.Should().Contain(o => o.DisplayName!.Contains("Isenta parceiro (desabilitada)"));
        evidencia.Items.Should().Contain(o => o.Detail!.Contains("Disabled") && o.Detail.Contains("habilitá-la basta"));
    }

    // ======================================================================================================
    //  Fotografia e exportações — a mesma contagem em HTML, CSV e PDF
    // ======================================================================================================

    [Fact]
    public async Task Fotografia_HtmlCsvEPdf_SaemDaMesmaColeta_ComEscopoDeExchangeDeclarado()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.NonCompliant);

        var published = await PostureFor(db, TenantA).PublishAsync(
            PostureSnapshotType.Knight, KnightSourceType.MicrosoftExchangeOnline, run.Id);
        var snapshotId = published.Summary.Id;
        var snapshot = await db.PostureSnapshots.AsNoTracking()
            .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
            .SingleAsync(s => s.Id == snapshotId);

        snapshot.SourceType.Should().Be(KnightSourceType.MicrosoftExchangeOnline);
        snapshot.Indicators.Should().OnlyContain(i => i.IndicatorId.StartsWith("AK-EXO-"));

        var model = KnightReportModelBuilder.Build(snapshot, true);
        model.Header.SourceType.Should().Be("MicrosoftExchangeOnline");
        model.Header.Provider.Should().Be("Microsoft");
        // O ESCOPO da exportação é identificado: é Exchange Online, não a consolidação do KNIGHT inteiro.
        model.ByService.Should().ContainSingle(r => r.Label == "Exchange Online");
        model.ByPlatform!.Should().ContainSingle(r => r.Label == "Microsoft 365");
        model.Controls.Should().OnlyContain(c => c.Service == "Exchange Online" && c.Platform == "Microsoft 365");
        model.Controls.Should().OnlyContain(c => c.Impact != null);
        model.Controls.Should().OnlyContain(c => c.References.Any(r => r.Framework.StartsWith("CIS ")));

        // A contagem de ocorrências é a soma dos afetados dos controles reprovados — e não outra.
        var occurrences = model.Controls.Where(c => c.Status is "Exposed" or "Mitigated")
            .Sum(c => c.Objects.Count(o => o.Relation == "Affected"));
        occurrences.Should().Be(model.Kpis.Occurrences);

        var exporter = new PostureSnapshotExporter(db);
        var html = Encoding.UTF8.GetString((await exporter.ExportAsync(snapshotId, PostureExportFormat.Html))!.Content);
        html.Should().Contain("Exchange Online").And.Contain("Impacto potencial");
        html.Should().NotContain("<script src").And.NotContain("<link", "o relatório abre offline, sem buscar nada");

        var csv = Encoding.UTF8.GetString((await exporter.ExportAsync(snapshotId, PostureExportFormat.Csv))!.Content).TrimStart('﻿');
        csv.Should().Contain("AK-EXO-016").And.Contain("faturamento@example.com");

        var pdf = (await exporter.ExportAsync(snapshotId, PostureExportFormat.Pdf))!.Content;
        Encoding.ASCII.GetString(pdf, 0, 4).Should().Be("%PDF");

        // HTML, CSV e PDF saem da MESMA fotografia: o número de controles não diverge entre eles.
        model.Controls.Should().HaveCount(snapshot.Indicators.Count);

        // Evidência de ENTREGA, opcional e fora do repositório: quando AEGIS_EXPORT_OUT aponta um diretório,
        // os três formatos saem em arquivo — os MESMOS bytes que as asserções acima acabaram de examinar,
        // pelo mesmo exportador. Sem a variável, o teste não escreve nada. O que se gera aqui são dados
        // SINTÉTICOS deste cenário, e o cabeçalho do relatório já os identifica como tal.
        var destino = Environment.GetEnvironmentVariable("AEGIS_EXPORT_OUT");
        if (!string.IsNullOrWhiteSpace(destino))
        {
            Directory.CreateDirectory(destino);
            var baseNome = Path.Combine(destino, "knight-exchange-online");
            await File.WriteAllBytesAsync(baseNome + ".html", Encoding.UTF8.GetBytes(html));
            await File.WriteAllBytesAsync(baseNome + ".csv", Encoding.UTF8.GetBytes(csv));
            await File.WriteAllBytesAsync(baseNome + ".pdf", pdf);
            _output.WriteLine($"artefatos: {baseNome}.html | {baseNome}.csv | {baseNome}.pdf");

            // Um segundo conjunto, de propósito, a partir de uma coleta PARCIAL: uma leitura recusada e uma
            // enumeração truncada. É o conjunto em que "não avaliado" e "parcial" existem para ser conferidos
            // entre os formatos — o cenário reprovado acima não tem nenhum dos dois, e conferir coerência só
            // onde tudo é igual não prova nada sobre os estados que diferenciam este produto.
            var parcial = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.MailboxesDenied);
            var pubParcial = await PostureFor(db, TenantA).PublishAsync(
                PostureSnapshotType.Knight, KnightSourceType.MicrosoftExchangeOnline, parcial.Id);
            var baseParcial = Path.Combine(destino, "knight-exchange-online-parcial");
            foreach (var (formato, extensao) in new[]
                     {
                         (PostureExportFormat.Html, ".html"),
                         (PostureExportFormat.Csv, ".csv"),
                         (PostureExportFormat.Pdf, ".pdf"),
                     })
            {
                var saida = (await exporter.ExportAsync(pubParcial.Summary.Id, formato))!;
                await File.WriteAllBytesAsync(baseParcial + extensao, saida.Content);
            }

            _output.WriteLine($"artefatos (coleta parcial): {baseParcial}.html | {baseParcial}.csv | {baseParcial}.pdf");
        }
    }

    /// <summary>
    /// A tabela de limitações do relatório nomeia a LEITURA que faltou. Sem rótulo, ela mostrava o símbolo do
    /// código — "ExchangeMailboxes" —, que não diz nada a quem lê o relatório, e o requisito de autorização
    /// vinha vazio. O texto do requisito diz as DUAS coisas que o Exchange exige, sem inventar consentimento
    /// por comando: no Exchange a permissão habilita a conexão e o papel de diretório autoriza cada leitura.
    /// </summary>
    [Fact]
    public async Task LimitacaoNoRelatorio_NomeiaALeituraEmPortugues_ENaoOSimboloDoCodigo()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.MailboxesDenied);

        var published = await PostureFor(db, TenantA).PublishAsync(
            PostureSnapshotType.Knight, KnightSourceType.MicrosoftExchangeOnline, run.Id);
        var snapshot = await db.PostureSnapshots.AsNoTracking()
            .Include(x => x.Controls).Include(x => x.Indicators).Include(x => x.ActionItems).Include(x => x.Objects)
            .SingleAsync(x => x.Id == published.Summary.Id);

        var limitacao = KnightReportModelBuilder.Build(snapshot, true).Limitations
            .Single(l => l.Capability == nameof(KnightCapability.ExchangeMailboxes));

        limitacao.CapabilityLabel.Should().Be("Caixas de correio");
        limitacao.CapabilityLabel.Should().NotBe(nameof(KnightCapability.ExchangeMailboxes),
            "o relatório é lido por quem não tem o código aberto ao lado");
        limitacao.RequiredPermission.Should().NotBeNullOrWhiteSpace()
            .And.Subject.Should().Contain("Exchange.ManageAsApp").And.Contain("papel de diretório");

        // O PDF lia a string CONGELADA da fotografia, que carrega o nome do símbolo do código. O mesmo fato
        // saía legível no HTML e técnico no PDF, para o mesmo leitor. A linha do PDF agora nasce da mesma
        // lista estruturada — e nomeia os controles que ficaram sem veredito por causa dela.
        var linha = PostureSnapshotPdfWriter.LimitationLine(limitacao);
        linha.Should().StartWith("Caixas de correio: Autorização recusada nesta leitura");
        linha.Should().NotContain(nameof(KnightCapability.ExchangeMailboxes));
        linha.Should().NotContain(nameof(KnightCapabilityOutcome.InsufficientPermission));
        linha.Should().Contain("sem veredito:").And.Contain("AK-EXO-001");
    }

    /// <summary>
    /// Nome de política com marcação e aspas. Ele atravessa coleta → ADM → avaliação → HTML e CSV como TEXTO:
    /// não vira marcação executável no relatório e não desaparece do conteúdo.
    /// </summary>
    [Fact]
    public async Task TextoHostilDaFonte_AtravessaComoTexto_NuncaComoMarcacao()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.HostileText);

        var published = await PostureFor(db, TenantA).PublishAsync(
            PostureSnapshotType.Knight, KnightSourceType.MicrosoftExchangeOnline, run.Id);
        var exporter = new PostureSnapshotExporter(db);
        var html = Encoding.UTF8.GetString((await exporter.ExportAsync(published.Summary.Id, PostureExportFormat.Html))!.Content);

        // O relatório do KNIGHT leva os dados num bloco JSON dentro da página. A defesa, portanto, é o
        // ESCAPE do codificador de JavaScript: o "<" do nome sai como sequência de escape e não pode fechar o
        // bloco nem abrir uma marcação. O que NÃO pode acontecer é o nome sair literal.
        html.Should().NotContain("<script>alert", "o nome vindo da fonte não pode virar marcação executável");
        html.Should().NotContain("</script> &", "nem pode fechar o bloco de dados da página");
        html.Should().ContainEquivalentOf(@"\u003Cscript\u003Ealert",
            "e também não pode sumir: ele é o nome do objeto que o operador vai procurar no ambiente");

        var csv = Encoding.UTF8.GetString((await exporter.ExportAsync(published.Summary.Id, PostureExportFormat.Csv))!.Content);
        csv.Should().Contain("alert", "o CSV preserva o texto observado");
    }

    // ======================================================================================================
    //  PRECEDÊNCIA E ALCANCE — a política padrão atender não é o ambiente atender
    // ======================================================================================================

    /// <summary>
    /// A política PADRÃO do Outlook na web e a de compartilhamento atendem o critério. Existem, porém, uma
    /// política NÃO padrão permissiva DECLARADA por uma caixa de correio e outra permissiva que nenhuma caixa
    /// declara. Ler só a padrão aprovaria o ambiente descrevendo a exceção que ninguém usa.
    ///
    /// O teste fixa as duas metades da regra: a violação existe nas duas políticas (o defeito de configuração
    /// é um fato), e o ALCANCE de cada uma é dito com o que a enumeração demonstra — "N caixas declaram esta
    /// política" para a alcançada, "nenhuma das caixas lidas declara" para a outra. Uma não vira a outra.
    /// </summary>
    [Fact]
    public async Task PoliticaNaoPadraoPermissiva_ReprovaMesmoComAPadraoAtendendo_ComOAlcanceDeCadaUma()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.PolicyReach);
        Dump(run);

        var svc = ServiceFor(db, TenantA, new ExchangeCollectionScenario(ExchangeCollectionScenario.Variant.PolicyReach));

        foreach (var id in new[] { "AK-EXO-012", "AK-EXO-015" })
        {
            var controle = run.Indicators.Single(i => i.IndicatorId == id);
            controle.Status.Should().Be(KnightIndicatorStatus.Exposed,
                $"{id}: a política padrão atende, mas duas políticas não padrão liberam o que o critério fecha");
            controle.Evidence.Should().NotContain("Como a política PADRÃO está entre elas",
                "a padrão NÃO está entre as reprovadas e o texto não pode dizer que está");

            var afetadas = (await svc.GetAffectedObjectsAsync(run.Id, id, 1, 50, null))!;
            afetadas.Items.Should().HaveCount(2);

            // A política ALCANÇADA: o alcance é demonstrado pela enumeração de caixas, com número.
            var alcancada = afetadas.Items.Single(o => o.DisplayName!.Contains(ExchangeCollectionScenario.OwaAlcancada));
            alcancada.Detail.Should().Contain("1 caixa(s) de correio declaram esta política")
                .And.Contain("de 2 caixa(s) lida(s)");

            // A política SEM alcance: nenhuma caixa a declara — e isso é dito, não convertido em "sem efeito".
            var semAlcance = afetadas.Items.Single(o => o.DisplayName!.Contains(ExchangeCollectionScenario.OwaSemAlcance));
            semAlcance.Detail.Should().Contain("nenhuma das caixas lidas declara esta política");

            // A padrão, que atende, permanece publicada como EVIDÊNCIA e identificada como padrão.
            var evidencias = (await svc.GetAffectedObjectsAsync(run.Id, id, 1, 50, null, default, KnightObjectRelation.Evidence))!;
            evidencias.Items.Should().Contain(o => o.DisplayName!.Contains("OwaMailboxPolicy-Default")
                && o.Detail!.Contains("É a política PADRÃO"));
        }

        // O mesmo vale para o compartilhamento de calendário, cuja declaração vem da própria caixa de correio.
        var calendario = run.Indicators.Single(i => i.IndicatorId == "AK-EXO-002");
        calendario.Status.Should().Be(KnightIndicatorStatus.Exposed);
        var calendarioAfetadas = (await svc.GetAffectedObjectsAsync(run.Id, "AK-EXO-002", 1, 50, null))!;
        calendarioAfetadas.Items.Should().ContainSingle(o => o.DisplayName!.Contains(ExchangeCollectionScenario.CompartilhamentoAlcancado));
        calendarioAfetadas.Items.Single().Detail.Should().Contain("1 caixa(s) de correio declaram esta política");

        // A política desligada com o mesmo domínio anônimo continua fora da exposição: existe e não concede.
        calendarioAfetadas.Items.Should().NotContain(o => o.DisplayName!.Contains("desativada"));

        // E a precedência não contamina o que não vive em política: os critérios da organização seguem concluindo.
        run.Indicators.Single(i => i.IndicatorId == "AK-EXO-003").Status.Should().Be(KnightIndicatorStatus.Passed);
        run.Indicators.Single(i => i.IndicatorId == "AK-EXO-017").Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    // ======================================================================================================
    //  AUSÊNCIA DE REGISTROS — não é recusa, não é omissão, e não é aprovação
    // ======================================================================================================

    /// <summary>
    /// As doze leituras CONCLUÍRAM e voltaram vazias. O produto precisa distinguir isso de três coisas
    /// diferentes: recusa por autorização, leitura omitida e ambiente configurado corretamente. Nenhuma
    /// configuração que depende de um documento que não veio pode ser aprovada — e o motivo publicado não pode
    /// falar em permissão, porque permissão não faltou.
    /// </summary>
    [Fact]
    public async Task ColetaSemNenhumRegistro_NaoEhRecusaNemAprovacao_EDizOQueFaltou()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.NoRecords);
        Dump(run);

        // A coleta CONCLUIU: nenhuma capacidade pode ser marcada como recusada nem como não tentada.
        run.SourceState.Should().Be(KnightSourceState.Completed);
        run.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.Collected,
            "“nenhum registro encontrado” é leitura concluída — confundi-la com falta de permissão mente sobre a causa");

        var exo = run.Indicators.Where(i => ExchangeControlIds.Contains(i.IndicatorId)).ToList();
        exo.Should().NotContain(i => i.Status == KnightIndicatorStatus.Exposed,
            "sem registro algum não há violação demonstrada — afirmar exposição seria inventar o achado");

        // Todo critério que vive num documento ÚNICO do locatário, ou numa política, fica sem veredito.
        foreach (var id in new[]
                 {
                     "AK-EXO-002", "AK-EXO-003", "AK-EXO-004", "AK-EXO-005", "AK-EXO-010",
                     "AK-EXO-011", "AK-EXO-012", "AK-EXO-013", "AK-EXO-014", "AK-EXO-015",
                     "AK-EXO-016", "AK-EXO-017",
                 })
        {
            var controle = run.Indicators.Single(i => i.IndicatorId == id);
            controle.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, id);
            controle.NotEvaluatedReason.Should().Contain("sem devolver", id);
        }

        // E o motivo NUNCA atribui a ausência a permissão — a permissão existia e a leitura terminou.
        var motivos = string.Join(" ", exo.Select(i => i.NotEvaluatedReason));
        motivos.Should().NotContainEquivalentOf("permissão").And.NotContainEquivalentOf("recusad");
    }

    // ======================================================================================================
    //  Isolamento, preservação das outras fontes e ausência de coleta na leitura
    // ======================================================================================================

    [Fact]
    public async Task IsolamentoEntreClientes_ConfiguracaoDoExchangeNaoVazaParaOutroTenant()
    {
        await SeedAsync(TenantA);
        await SeedAsync(TenantB);
        await using (var db = NewContext(TenantA))
            await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.NonCompliant);

        await using var dbB = NewContext(TenantB);
        (await dbB.IdentityConfigurationObservations.CountAsync()).Should().Be(0);
        (await dbB.IdentityAcquisitions.CountAsync()).Should().Be(0);
        (await dbB.KnightAssessmentRuns.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// Sincronizar o Exchange NÃO apaga nem substitui a avaliação das outras fontes: cada uma tem a própria
    /// aquisição, a própria execução e o próprio veredito.
    /// </summary>
    [Fact]
    public async Task SincronizarExchange_PreservaAsAquisicoesEAsAvaliacoesDasOutrasFontes()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);

        var entra = await ServiceFor(db, TenantA, new ExchangeCollectionScenario(ExchangeCollectionScenario.Variant.Compliant))
            .RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);
        var exchange = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.Compliant);

        var aquisicoes = await db.IdentityAcquisitions.AsNoTracking().Select(a => a.Provider).ToListAsync();
        aquisicoes.Should().BeEquivalentTo(new[] { KnightSourceType.MicrosoftEntraId, KnightSourceType.MicrosoftExchangeOnline });

        var execucoes = await db.KnightAssessmentRuns.AsNoTracking().ToListAsync();
        execucoes.Should().HaveCount(2);
        execucoes.Single(r => r.Id == entra.Id).SourceType.Should().Be(KnightSourceType.MicrosoftEntraId);
        var doExchangeRun = execucoes.Single(r => r.Id == exchange.Id);
        doExchangeRun.SourceType.Should().Be(KnightSourceType.MicrosoftExchangeOnline);

        // E os documentos de configuração de cada aquisição continuam separados por contrato.
        var aquisicaoExchange = doExchangeRun.IdentityAcquisitionId;
        var doExchange = await db.IdentityConfigurationObservations.AsNoTracking()
            .Where(c => c.AcquisitionId == aquisicaoExchange)
            .Select(c => c.SchemaVersion).ToListAsync();
        doExchange.Should().OnlyContain(s => s.StartsWith("aegis-config-exo-")).And.NotBeEmpty();
    }

    [Fact]
    public async Task AbrirALeituraPorFonte_NaoDisparaColeta()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var reader = new ExchangeCollectionScenario(ExchangeCollectionScenario.Variant.Compliant);
        var svc = ServiceFor(db, TenantA, reader);

        await svc.RunAssessmentAsync(KnightSourceType.MicrosoftExchangeOnline);
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
        await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.Compliant);

        var connector = await db.Connectors.SingleAsync();
        connector.Enabled = false;
        await db.SaveChangesAsync();

        await FluentActions.Awaiting(() => RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.Compliant))
            .Should().ThrowAsync<KnightSourceNotConfiguredException>();

        (await db.IdentityAcquisitions.CountAsync(a => a.Provider == KnightSourceType.MicrosoftExchangeOnline))
            .Should().Be(1, "a coleta recusada não destrói a evidência anterior");
    }

    // ======================================================================================================
    //  Autorização recusada e diagnóstico sem segredo
    // ======================================================================================================

    /// <summary>
    /// Uma recusa de autorização é AMBÍGUA: consentimento ausente, papel de diretório ausente, papel sem
    /// alcance, domínio errado ou o próprio método de autenticação não ser aceito produzem o mesmo sintoma.
    /// A mensagem publicada tem de enumerar as verificações — e NÃO pode eleger uma causa, porque mandar o
    /// operador consertar o item errado esconde o certo. Este teste prova as duas metades: a lista está lá, e
    /// a afirmação de causa não está.
    /// </summary>
    [Fact]
    public async Task ConexaoRecusadaPorAutorizacao_ListaAsVerificacoes_SemEleger_Causa_ENaoAvaliaNada()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, ExchangeCollectionScenario.Variant.NotConnected);
        Dump(run);

        run.SourceState.Should().Be(KnightSourceState.InsufficientPermission);
        run.Indicators.Where(i => ExchangeControlIds.Contains(i.IndicatorId))
            .Should().OnlyContain(i => i.Status == KnightIndicatorStatus.NotEvaluated);

        var detalhe = string.Join(" ", run.Capabilities.Select(c => c.Detail));
        detalhe.Should().Contain("Exchange.ManageAsApp").And.Contain("papel de diretório");
        detalhe.Should().Contain("Leitor Global");
        detalhe.Should().Contain("Microsoft Teams não vale aqui",
            "copiar o papel do Teams é o erro mais fácil de cometer aqui, e a mensagem precisa preveni-lo");

        // O método de autenticação entra na lista: ele é a hipótese que o AEGIS não conseguiu confirmar em
        // documentação, e omiti-lo deixaria o operador procurando para sempre no lugar errado.
        detalhe.Should().Contain("segredo de cliente");

        // E a mensagem se declara incapaz de apontar a causa. Sem esta linha, a enumeração seria lida como
        // diagnóstico — "faltam estas coisas" — que é precisamente o que a recusa NÃO informa.
        detalhe.Should().Contain("não identifica a causa");
        detalhe.Should().NotContain("São necessárias DUAS",
            "o texto antigo declarava o que faltava a partir de um sintoma que não distingue as causas");
    }

    [Fact]
    public async Task SegredoNoDiagnostico_NaoChegaAoAdm_NemAoRelatorio()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);

        var run = await ServiceFor(db, TenantA, new ConexaoComSegredoNoDiagnostico())
            .RunAssessmentAsync(KnightSourceType.MicrosoftExchangeOnline);
        Dump(run);

        var published = await PostureFor(db, TenantA).PublishAsync(
            PostureSnapshotType.Knight, KnightSourceType.MicrosoftExchangeOnline, run.Id);
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
        tudo.Should().Contain("Exchange.ManageAsApp", "a orientação sobre a causa é preservada");
    }

    // ---- Infraestrutura -----------------------------------------------------------------------------

    /// <summary>
    /// Adaptador que devolve, no diagnóstico da conexão, marcadores SINTÉTICOS com forma de segredo. Nenhuma
    /// credencial real: são cadeias inventadas para este teste.
    /// </summary>
    private sealed class ConexaoComSegredoNoDiagnostico : IExchangeAdminReader
    {
        internal const string MarcadorDeToken = "eyJhZWdpcyI6ImV4byJ9.QUVHSVMtRVhPLVNJTlRFVElDTw.YXNzaW5hdHVyYQ";
        internal const string MarcadorDeSegredo = "client_secret=AEGIS-SEGREDO-SINTETICO-do-exchange-0001";

        public Task<ExchangeAdminOutput> ReadAsync(ExchangeAdminCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(PowerShellExchangeAdminReader.Parse(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    runtime = new { powerShell = "7.4.7", module = "3.9.2", platform = "Unix" },
                    connected = false,
                    connectionErrorCategory = "InsufficientPermission",
                    connectionErrorId = MarcadorDeToken + " " + MarcadorDeSegredo,
                    enumerationLimit = 5000,
                    reads = Array.Empty<object>(),
                })));

        public Task<ExchangeAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) => ReadAsync(null!, ct);
    }

    private static async Task<KnightTenantConfiguration> ReadConfigurationAsync(AegisScoreDbContext db, Guid acquisitionId)
    {
        var docs = await db.IdentityConfigurationObservations.AsNoTracking()
            .Where(c => c.AcquisitionId == acquisitionId).ToListAsync();
        return KnightTenantConfiguration.FromObserved(
            docs.Select(d => new AegisScore.Application.Identity.Adm.IdentityObservedConfiguration(
                d.Kind, d.ExternalId, d.DisplayName, d.SchemaVersion, d.ConfigurationJson)),
            ExchangeConfigurationCapabilities.Select(c => new KnightCapabilityStatus(c, KnightCapabilityOutcome.Collected)));
    }

    private static IReadOnlyList<KnightCapability> ExchangeConfigurationCapabilities =>
        AegisScore.Application.Knight.Reference.KnightCollectorCapabilities
            .Produces(KnightSourceType.MicrosoftExchangeOnline).ToList();

    private Task<KnightAssessment> RunAsync(AegisScoreDbContext db, Guid tenant, ExchangeCollectionScenario.Variant variant) =>
        ServiceFor(db, tenant, new ExchangeCollectionScenario(variant))
            .RunAssessmentAsync(KnightSourceType.MicrosoftExchangeOnline);

    internal static IAegisKnightAssessmentService ServiceFor(AegisScoreDbContext db, Guid tenantId, IExchangeAdminReader reader)
    {
        var tenant = new SystemTenantContext(tenantId);
        var registry = new KnightCollectorRegistry(new IKnightCollector[]
        {
            new EntraIdKnightCollector(new EntraGraphClient(new System.Net.Http.HttpClient(new KnightGraphScenario.StubHandler()))),
            new ExchangeKnightCollector(new StubTokens(), reader),
        });
        var config = new MicrosoftConfig();
        var store = new IdentityAcquisitionStore(db, tenant, TimeProvider.System);
        var evidence = new IdentityEvidenceService(db, registry, config, store, tenant);
        return new AegisKnightAssessmentService(db, registry, config, new KnightMulticloudReportTests.SemIa(), evidence, store, tenant);
    }

    private static IPostureSnapshotService PostureFor(AegisScoreDbContext db, Guid tenantId) =>
        new PostureSnapshotService(db, new SystemTenantContext(tenantId), new AegisScore.Infrastructure.Connectors.NistSignalMapper(db));

    /// <summary>O MESMO conector Microsoft resolve as três fontes — é o mesmo registro de aplicação.</summary>
    private sealed class MicrosoftConfig : IKnightSourceConfigurationProvider
    {
        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default) =>
            Task.FromResult<KnightSourceConfiguration>(source switch
            {
                KnightSourceType.MicrosoftEntraId => new KnightEntraIdConfiguration("dir-demo-0001", "client", "secret"),
                KnightSourceType.MicrosoftExchangeOnline => new KnightExchangeOnlineConfiguration("dir-demo-0001", "client", "secret"),
                _ => new KnightSourceNotConfigured(source),
            });

        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }

    /// <summary>
    /// O domínio da organização é sintético e o token também. O cliente real resolve o domínio inicial pelo
    /// Microsoft Graph na mesma aquisição — aqui o que se exercita é o que vem DEPOIS dele.
    /// </summary>
    private sealed class StubTokens : IExchangeTokenClient
    {
        public Task<ExchangeAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(new ExchangeAdminCredentials("contoso.onmicrosoft.example.com", "token-sintetico"));
    }

    private void Dump(KnightAssessment run)
    {
        foreach (var i in run.Indicators.Where(i => i.IndicatorId.StartsWith("AK-EXO-")).OrderBy(i => i.IndicatorId, StringComparer.Ordinal))
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
