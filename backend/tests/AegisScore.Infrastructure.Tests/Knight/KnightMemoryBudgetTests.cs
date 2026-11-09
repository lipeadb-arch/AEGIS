using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Exchange;
using AegisScore.Connectors.Microsoft.Knight.PowerShell;
using AegisScore.Connectors.Microsoft.Knight.Protection;
using AegisScore.Connectors.Microsoft.Knight.Teams;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-MEMORY-BUDGET] Regressões da evidência real de 30/10/2026: processo encerrado por memória DURANTE a importação, sem conexão
/// e sem leitura. A causa conhecida não pode virar "runtime indisponível", uma decisão por capacidade não pode ser sobrescrita, e o resumo
/// determinístico não pode priorizar achados que não existem nem repetir a mesma causa por controle.
/// </summary>
public sealed class KnightMemoryBudgetTests
{
    private static readonly Guid Tenant = Guid.Parse("aa000000-0000-0000-0000-0000000000e1");

    /// <summary>O documento e o relatório como o adaptador real os monta quando o monitor encerra o filho no estágio de importação.</summary>
    private static PowerShellAdapterRun KilledDuringImport(PowerShellTermination termination = PowerShellTermination.MemoryLimit) =>
        new("""{"runtime":{},"connected":false,"reads":[]}""",
            new PowerShellRunReport(termination, 137, new[] { "start", "input", "import" }, Array.Empty<string>(), Array.Empty<string>(),
                ResultEmitted: false, OutputComplete: true, DurationMs: 1800, NoiseLines: 0, MalformedFrames: 0, SafeDiagnostics: ""));

    private sealed class ExchangeTokens : IExchangeTokenClient
    {
        public Task<ExchangeAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(new ExchangeAdminCredentials("demo.example.com", "token-sintetico"));
    }

    private sealed class TeamsTokens : ITeamsTokenClient
    {
        public Task<TeamsAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(new TeamsAdminCredentials("dir", "graph-sintetico", "teams-sintetico"));
    }

    private sealed class AppTokens : IMicrosoftAppTokenClient
    {
        public Task<string> AcquireAsync(IMicrosoftGraphCredentials credentials, string scope, CancellationToken ct = default) =>
            Task.FromResult("token-sintetico");
    }

    private sealed class NoDns : IDnsTxtResolver
    {
        public Task<DnsTxtResult> ResolveTxtAsync(string name, CancellationToken ct) => throw new InvalidOperationException("não deveria consultar DNS");
    }

    private sealed class StubTeams(PowerShellAdapterRun run) : ITeamsAdminReader
    {
        public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(PowerShellTeamsAdminReader.Interpret(run));
        public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TeamsAdminOutput> TestConnectionAsync(TeamsAdminCredentials credentials, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubExchange(PowerShellAdapterRun run) : IExchangeAdminReader
    {
        public Task<ExchangeAdminOutput> ReadAsync(ExchangeAdminCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(PowerShellExchangeAdminReader.Interpret(run));
        public Task<ExchangeAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExchangeAdminOutput> TestConnectionAsync(ExchangeAdminCredentials credentials, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubProtection(PowerShellAdapterRun run, bool complianceSupported) : IProtectionAdminReader
    {
        public Task<ProtectionAdminOutput> ReadAsync(ProtectionAdminRequest request, CancellationToken ct = default) =>
            Task.FromResult(PowerShellProtectionAdminReader.Interpret(run));
        public Task<ProtectionAdminOutput> TestConnectionAsync(ProtectionAdminRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public bool ComplianceSessionSupported => complianceSupported;
    }

    private static void ShouldKeepMemoryCause(IEnumerable<KnightCapabilityStatus> capabilities)
    {
        var all = capabilities.ToList();
        all.Should().NotBeEmpty();
        all.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.Error);
        all.Should().OnlyContain(c => c.Diagnostic!.Kind == KnightFailureKind.MemoryLimit, "a terminação conhecida não vira runtime indisponível");
        all.Should().OnlyContain(c => c.Diagnostic!.Stage == "import" && c.Diagnostic.Code == "MemoryLimit");
        all.Should().OnlyContain(c => c.Detail!.Contains("memória") && c.Detail.Contains("não indica falta de permissão"));
        all.Should().NotContain(c => c.Detail!.Contains("não carregou"), "o módulo foi interrompido, não recusado pelo ambiente");
    }

    [Fact]
    public async Task Teams_MemoryLimitNaImportacao_PreservaACausa()
    {
        var result = await new TeamsKnightCollector(new TeamsTokens(), new StubTeams(KilledDuringImport()))
            .CollectAsync(new KnightCollectionContext(Tenant, new KnightTeamsConfiguration("dir", "client", "secret")));

        ShouldKeepMemoryCause(result.Capabilities);
        result.State.Should().Be(KnightSourceState.Error);
    }

    [Fact]
    public async Task Exchange_MemoryLimitNaImportacao_PreservaACausa()
    {
        var result = await new ExchangeKnightCollector(new ExchangeTokens(), new StubExchange(KilledDuringImport()))
            .CollectAsync(new KnightCollectionContext(Tenant, new KnightExchangeOnlineConfiguration("dir", "client", "secret")));

        ShouldKeepMemoryCause(result.Capabilities);
        result.State.Should().Be(KnightSourceState.Error);
    }

    [Fact]
    public async Task Defender_MemoryLimitNaImportacao_PreservaACausa_ENaoViraRuntimeIndisponivel()
    {
        var result = await new DefenderForOffice365KnightCollector(new ExchangeTokens(), new StubProtection(KilledDuringImport(), true), new NoDns())
            .CollectAsync(new KnightCollectionContext(Tenant,
                new KnightMicrosoftServiceConfiguration(KnightSourceType.MicrosoftDefenderForOffice365, "dir", "client", "secret")));

        ShouldKeepMemoryCause(result.Capabilities.Where(c => c.Capability != KnightCapability.DefenderDnsRecords));
        var dns = result.Capabilities.Single(c => c.Capability == KnightCapability.DefenderDnsRecords);
        dns.Outcome.Should().Be(KnightCapabilityOutcome.NotAttempted, "sem a lista de domínios aceitos, o DNS não é consultado");
    }

    [Fact]
    public async Task Purview_MemoryLimitNaImportacao_NaoSobrescreveALimitacaoDePlataforma()
    {
        var collector = new PurviewKnightCollector(new ExchangeTokens(), new StubProtection(KilledDuringImport(), complianceSupported: false), new AppTokens());
        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant,
            new KnightMicrosoftServiceConfiguration(KnightSourceType.MicrosoftPurview, "dir", "client", "secret")));

        var platform = result.Capabilities.Where(c => c.Outcome == KnightCapabilityOutcome.PlatformUnsupported).ToList();
        platform.Should().NotBeEmpty("a sessão do Security & Compliance já estava decidida antes do processo");
        platform.Should().OnlyContain(c => c.Diagnostic!.Kind == KnightFailureKind.PlatformUnsupported);
        ShouldKeepMemoryCause(result.Capabilities.Where(c => c.Outcome != KnightCapabilityOutcome.PlatformUnsupported));
        result.Capabilities.Should().NotContain(c => c.Outcome == KnightCapabilityOutcome.InsufficientPermission);
    }

    [Theory]
    [InlineData(PowerShellTermination.TimedOut, KnightFailureKind.Timeout)]
    [InlineData(PowerShellTermination.OutputLimit, KnightFailureKind.OutputLimit)]
    [InlineData(PowerShellTermination.NonZeroExit, KnightFailureKind.ProcessExit)]
    public void AntesDaConexao_TerminacaoConhecida_NuncaViraModuloIndisponivel(PowerShellTermination termination, KnightFailureKind kind)
    {
        var before = PowerShellRunInterpretation.BeforeConnect("Serviço", KilledDuringImport(termination).Report, null, "corr");

        before.Diagnostic.Kind.Should().Be(kind);
        before.Detail.Should().NotContain("não carregou");
    }

    // ---- Orçamento de memória -----------------------------------------------------------------------------------------------------------

    private static string Stat(long file, long shmem, long mapped, long active, long inactive, long dirty = 0, long writeback = 0) =>
        $"anon 1000\nfile {file}\nshmem {shmem}\nfile_mapped {mapped}\nactive_file {active}\ninactive_file {inactive}\nfile_dirty {dirty}\nfile_writeback {writeback}\nkernel 5\n";

    [Fact]
    public void Pressao_DescontaCacheLimpo_NuncaMemoriaCompartilhadaTmpfsOuPaginaSuja()
    {
        // 100 de cache de arquivo, 20 mapeados: tudo volta, menos o código em uso.
        var clean = ContainerMemorySample.Parse("400", "512", Stat(file: 120, shmem: 0, mapped: 20, active: 60, inactive: 60))!;
        clean.ReclaimableBytes.Should().Be(100);
        clean.PressureBytes.Should().Be(300);

        // tmpfs e memória compartilhada ficam em file, mas sem swap não têm para onde ir.
        var tmpfs = ContainerMemorySample.Parse("460", "512", Stat(file: 200, shmem: 200, mapped: 200, active: 0, inactive: 0))!;
        tmpfs.ReclaimableBytes.Should().Be(0);
        tmpfs.UnderPressure.Should().BeTrue("tmpfs cheio não pode parecer memória recuperável");

        // Página suja não conta, e o piso de 1/8 do limite (64) segura o código mapeado em uso.
        var mapped = ContainerMemorySample.Parse("460", "512", Stat(file: 200, shmem: 0, mapped: 150, active: 200, inactive: 0, dirty: 50), Calm)!;
        mapped.ReclaimableBytes.Should().Be(86);
        mapped.PressureBytes.Should().Be(374);

        // PSI válido só libera arquivo limpo mapeado: memória compartilhada, tmpfs e página em escrita continuam fora.
        ContainerMemorySample.Parse("460", "512", Stat(file: 200, shmem: 200, mapped: 200, active: 0, inactive: 0), Calm)!.ReclaimableBytes.Should().Be(0);
        ContainerMemorySample.Parse("460", "512", Stat(file: 200, shmem: 0, mapped: 150, active: 200, inactive: 0, writeback: 50), Calm)!.ReclaimableBytes.Should().Be(86);

        // Código mapeado inteiro abaixo do piso: nada a descontar além do que não está mapeado.
        var code = ContainerMemorySample.Parse("460", "512", Stat(file: 60, shmem: 0, mapped: 60, active: 60, inactive: 0))!;
        code.ReclaimableBytes.Should().Be(0);
        code.UnderPressure.Should().BeTrue();

        ContainerMemorySample.Parse("460", "512")!.UnderPressure.Should().BeTrue("sem memory.stat vale o total");
        new ContainerMemorySample(447, 512).UnderPressure.Should().BeFalse("o gatilho continua em 87,5%");
        new ContainerMemorySample(448, 512).UnderPressure.Should().BeTrue("o gatilho continua em 87,5%");
    }

    private const string Calm = "some avg10=3.00 avg60=1.00 avg300=0.10 total=1\nfull avg10=0.50 avg60=0.10 avg300=0.00 total=1\n";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("linha sem o campo")]
    [InlineData("some avg10=1.00 avg60=0.00 avg300=0.00 total=1\n")]
    [InlineData("full avg10=NaN avg60=0.00 avg300=0.00 total=1\n")]
    [InlineData("full avg10=Infinity avg60=0.00 avg300=0.00 total=1\n")]
    [InlineData("full avg10=-1.00 avg60=0.00 avg300=0.00 total=1\n")]
    [InlineData("full avg10=100.01 avg60=0.00 avg300=0.00 total=1\n")]
    [InlineData("full avg10=abc avg60=0.00 avg300=0.00 total=1\n")]
    public void SemPsiValido_NaoDescontaArquivoMapeado_NemInventaTravamento(string? pressure)
    {
        // 200 de cache limpo, 150 mapeados: só os 50 não mapeados voltam com certeza.
        var sample = ContainerMemorySample.Parse("460", "512", Stat(file: 200, shmem: 0, mapped: 150, active: 200, inactive: 0), pressure)!;

        sample.FullStallAvg10.Should().BeNull();
        sample.ReclaimableBytes.Should().Be(50);
        sample.PressureBytes.Should().Be(410);
        ContainerMemorySample.Parse("100", "512", pressure: pressure)!.UnderPressure.Should().BeFalse("PSI ruim não prova travamento");

        // A mesma amostra com PSI válido desconta o que excede o piso de 64.
        ContainerMemorySample.Parse("460", "512", Stat(file: 200, shmem: 0, mapped: 150, active: 200, inactive: 0), Calm)!.ReclaimableBytes.Should().Be(136);
    }

    [Theory]
    [InlineData("shmem")]
    [InlineData("file_mapped")]
    [InlineData("file_dirty")]
    [InlineData("file_writeback")]
    [InlineData("active_file")]
    [InlineData("inactive_file")]
    public void MemoryStatIncompleto_ValeOTotal_NuncaDescontoComCampoPresumidoEmZero(string missing)
    {
        var lines = Stat(file: 200, shmem: 20, mapped: 150, active: 180, inactive: 0).Split('\n').Where(l => !l.StartsWith(missing + " ", StringComparison.Ordinal));
        var sample = ContainerMemorySample.Parse("460", "512", string.Join('\n', lines), Calm)!;

        sample.Stat.Should().BeNull();
        sample.PressureBytes.Should().Be(460);
        sample.UnderPressure.Should().BeTrue();
    }

    [Fact]
    public void TravamentoPorMemoria_EncerraMesmoAbaixoDoGatilho_ESemPsiNaoInventaPressao()
    {
        const string stalled = "some avg10=70.00 avg60=20.00 avg300=5.00 total=1\nfull avg10=41.50 avg60=10.00 avg300=2.00 total=1\n";

        ContainerMemorySample.Parse("100", "512", pressure: stalled)!.UnderPressure.Should().BeTrue("o kernel já está travando as tarefas para recuperar memória");
        ContainerMemorySample.Parse("100", "512", pressure: Calm)!.UnderPressure.Should().BeFalse();
        ContainerMemorySample.Parse("100", "512", pressure: null)!.UnderPressure.Should().BeFalse();
        ContainerMemorySample.ParseFullStall("linha sem o campo").Should().BeNull();
    }

    [Fact]
    public async Task SemFolgaParaOAdaptador_NaoIniciaOProcesso_EDizACausa()
    {
        // Executável inexistente: se o processo fosse iniciado, o transporte lançaria em vez de devolver o relatório.
        var reader = new PowerShellTeamsAdminReader(new TeamsPowerShellOptions
        {
            Executable = "aegis-executavel-que-nao-existe",
            MemoryProbe = () => new ContainerMemorySample(400, 512),
        });

        var result = await new TeamsKnightCollector(new TeamsTokens(), reader)
            .CollectAsync(new KnightCollectionContext(Tenant, new KnightTeamsConfiguration("dir", "client", "secret")));

        result.Capabilities.Should().NotBeEmpty();
        result.Capabilities.Should().OnlyContain(c => c.Diagnostic!.Kind == KnightFailureKind.MemoryLimit && c.Diagnostic.Stage == PowerShellAdapterProcess.MemoryPreflightStage);
        result.Capabilities.Should().OnlyContain(c => c.Detail!.Contains("não foi iniciado") && c.Detail.Contains("não indica falta de permissão"));
    }

    [Fact]
    public void AmbienteDoFilho_TemOrcamentoProprio_SemSobrescreverOOperador()
    {
        var env = new Dictionary<string, string?>();
        ContainerMemorySample.ConfigureChildGc(env);
        env["DOTNET_gcServer"].Should().Be("0");
        env["DOTNET_gcConcurrent"].Should().Be("0");
        env["DOTNET_TieredPGO"].Should().Be("0");
        env["DOTNET_TieredCompilation"].Should().Be("0");
        env["DOTNET_GCgen0MaxBudget"].Should().Be("1000000", "16 MiB em hexadecimal");

        env = new() { ["DOTNET_gcServer"] = "1" };
        ContainerMemorySample.ConfigureChildGc(env);
        env["DOTNET_gcServer"].Should().Be("1");
    }

    // ---- Interpretação consultiva determinística ----------------------------------------------------------------------------------------

    private static KnightAdvisoryIndicator Indicator(KnightIndicatorDefinition d, KnightIndicatorStatus status, string evidence) =>
        new(d.Id, d.Title, d.Category, d.Severity, status, evidence, 0, d.NistCodes, d.MitreTechniques);

    private static IReadOnlyList<KnightIndicatorDefinition> ControlsOf(KnightCapability capability) =>
        KnightCatalog.Indicators.Where(d => KnightControlProfiles.RequiredCapabilitiesOf(d.Id).Contains(capability)).Take(3).ToList();

    private static KnightCapabilityStatus MemoryKilled(KnightCapability capability) =>
        new(capability, KnightCapabilityOutcome.Error, "O adaptador foi encerrado por memória: parágrafo longo que antes se repetia em cada linha.",
            new KnightCapabilityDiagnostic("import", KnightFailureKind.MemoryLimit, 1800, Code: "MemoryLimit"));

    private const string RepeatedDetail = "parágrafo longo que antes se repetia";

    [Fact]
    public void SemNenhumControleAvaliado_PrioridadeEhRestabelecerAColeta_SemInventarRiscoNemConformidade()
    {
        var capabilities = new[] { MemoryKilled(KnightCapability.TeamsClientConfiguration), MemoryKilled(KnightCapability.TeamsMeetingPolicies) };
        var controls = capabilities.SelectMany(c => ControlsOf(c.Capability)).DistinctBy(d => d.Id).ToList();
        controls.Should().NotBeEmpty();
        var input = new KnightAdvisoryInput(KnightSourceType.MicrosoftTeams, KnightAssessmentMode.Live, null, 0,
            controls.Select(d => Indicator(d, KnightIndicatorStatus.NotEvaluated, RepeatedDetail)).ToList(),
            capabilities.Select(c => "Capacidade: " + c.Detail).ToList(), capabilities);

        var advisory = KnightAdvisoryFallback.Build(input);

        advisory.ExecutiveSummary.Should().StartWith("Interpretação determinística (sem IA)")
            .And.Contain("não conseguiu concluir a postura").And.Contain("restabelecer a coleta").And.Contain("não comprova risco nem conformidade");
        advisory.ExecutiveSummary.Should().NotContain("Priorize").And.NotContain("não conformes");
        advisory.PriorityRisks.Should().BeEmpty();

        var gap = advisory.CollectionGaps.Should().ContainSingle("a mesma causa e fonte formam UMA lacuna").Subject;
        gap.Should().Contain("Microsoft Teams").And.Contain("memória").And.Contain("2 capacidade(s)");
        controls.Should().OnlyContain(d => gap.Contains(d.Id), "os controles relacionados continuam acessíveis");
        string.Join(" ", advisory.CollectionGaps).Should().NotContain(RepeatedDetail);

        var action = advisory.RecommendedActions.Should().ContainSingle().Subject;
        action.Action.Should().Contain("Restabelecer a coleta");
    }

    [Fact]
    public void CausasDiferentes_FicamEmGruposDiferentes_PorDadoEstruturadoENaoPorTexto()
    {
        const string SameText = "Texto idêntico de propósito.";
        var capabilities = new[]
        {
            new KnightCapabilityStatus(KnightCapability.TeamsClientConfiguration, KnightCapabilityOutcome.Error, SameText,
                new KnightCapabilityDiagnostic("import", KnightFailureKind.MemoryLimit)),
            new KnightCapabilityStatus(KnightCapability.TeamsMeetingPolicies, KnightCapabilityOutcome.InsufficientPermission, SameText,
                new KnightCapabilityDiagnostic("read", KnightFailureKind.Command, HttpStatus: 403)),
            new KnightCapabilityStatus(KnightCapability.TeamsFederationConfiguration, KnightCapabilityOutcome.PlatformUnsupported, SameText,
                new KnightCapabilityDiagnostic("platform", KnightFailureKind.PlatformUnsupported)),
        };
        var input = new KnightAdvisoryInput(KnightSourceType.MicrosoftTeams, KnightAssessmentMode.Live, null, 0,
            capabilities.SelectMany(c => ControlsOf(c.Capability)).DistinctBy(d => d.Id)
                .Select(d => Indicator(d, KnightIndicatorStatus.NotEvaluated, "x")).ToList(),
            Array.Empty<string>(), capabilities);

        var advisory = KnightAdvisoryFallback.Build(input);

        advisory.CollectionGaps.Should().HaveCount(3);
        advisory.CollectionGaps.Should().ContainSingle(g => g.Contains("memória"));
        advisory.CollectionGaps.Should().ContainSingle(g => g.Contains("Autorização recusada"));
        advisory.CollectionGaps.Should().ContainSingle(g => g.Contains("plataforma"));
        advisory.RecommendedActions.Should().HaveCount(2, "limitação de plataforma não se resolve repetindo a coleta");
        advisory.RecommendedActions.Should().NotContain(a => a.Action.Contains("plataforma"));
        advisory.RecommendedActions.Single(a => a.Action.Contains("Autorização recusada")).Action.Should().Contain("não comprova, sozinha");
    }

    [Fact]
    public void AvaliacaoParcial_SeparaOComprovadoDoDesconhecido()
    {
        var teams = KnightCatalog.Indicators.Where(d => KnightControlProfiles.RequiredCapabilitiesOf(d.Id).Count > 0).Take(4).ToList();
        var input = new KnightAdvisoryInput(KnightSourceType.MicrosoftTeams, KnightAssessmentMode.Live, 40, 50, new[]
        {
            Indicator(teams[0], KnightIndicatorStatus.Exposed, "achado comprovado"),
            Indicator(teams[1], KnightIndicatorStatus.Passed, "ok"),
            Indicator(teams[2], KnightIndicatorStatus.NotEvaluated, "sem dado"),
            Indicator(teams[3], KnightIndicatorStatus.Error, "sem dado"),
        }, Array.Empty<string>(), new[] { MemoryKilled(KnightControlProfiles.RequiredCapabilitiesOf(teams[2].Id)[0]) });

        var advisory = KnightAdvisoryFallback.Build(input);

        advisory.ExecutiveSummary.Should().Contain("Comprovado pela coleta: 1 exposto(s), 0 mitigado(s) e 1 conforme(s)")
            .And.Contain("Sem conclusão: 2 controle(s)").And.Contain("não contam como risco nem como conformidade")
            .And.Contain("Priorize os expostos");
        advisory.PriorityRisks.Should().ContainSingle(r => r.IndicatorIds.Single() == teams[0].Id, "só o comprovado entra nos riscos");
        advisory.RecommendedActions.First().IndicatorIds.Should().Equal(teams[0].Id);
        advisory.RecommendedActions.Last().IndicatorIds.Should().BeEmpty("a ação de coleta aponta para a lacuna, sem repetir a lista");
        advisory.CollectionGaps.Should().Contain(g => g.Contains(teams[2].Id));
    }

    [Fact]
    public void CapacidadesEstruturadas_NaoEntramNoQueVaiParaAIa()
    {
        var input = new KnightAdvisoryInput(KnightSourceType.MicrosoftTeams, KnightAssessmentMode.Live, null, 0,
            Array.Empty<KnightAdvisoryIndicator>(), new[] { "limitação" }, new[] { MemoryKilled(KnightCapability.TeamsClientConfiguration) });

        var json = JsonSerializer.Serialize(input, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        json.Should().NotContain("capabilities").And.NotContain("MemoryLimit").And.Contain("limita");
    }

    [Fact]
    public void NarrativaJaGravada_ContinuaLegivelEIgual_PeloMesmoContrato()
    {
        const string stored = """{"ExecutiveSummary":"Resumo antigo.","PriorityRisks":[],"RecommendedActions":[{"Order":1,"Action":"Agir.","IndicatorIds":["AK-ENTRA-001"]}],"Correlations":[],"CollectionGaps":["AK-ENTRA-002: motivo antigo"]}""";

        var advisory = JsonSerializer.Deserialize<KnightAdvisory>(stored)!;

        advisory.ExecutiveSummary.Should().Be("Resumo antigo.");
        advisory.CollectionGaps.Should().Equal("AK-ENTRA-002: motivo antigo");
        JsonSerializer.Serialize(advisory).Should().Be(stored, "fotografias publicadas não mudam: o formato gravado é o mesmo");
    }
}
