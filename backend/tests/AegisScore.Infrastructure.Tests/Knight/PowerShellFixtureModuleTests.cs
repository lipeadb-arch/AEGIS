using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Exchange;
using AegisScore.Connectors.Microsoft.Knight.PowerShell;
using AegisScore.Connectors.Microsoft.Knight.Protection;
using AegisScore.Connectors.Microsoft.Knight.Services;
using AegisScore.Connectors.Microsoft.Knight.Teams;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] O processo COMPLETO com o PowerShell REAL: pedido na entrada padrão → processo → importação →
/// conexão → comandos de leitura do script EMBUTIDO → quadros na saída → interpretação → coletor. Só o módulo do fornecedor é
/// substituído por um módulo de FIXTURE (Knight/Fixtures/psmodules), que responde no formato documentado e escolhe o cenário pelo
/// token recebido. Prova o código do AEGIS — não prova autenticação em locatário real nem o comportamento do módulo oficial (isso é
/// do gate da imagem e da homologação).
///
/// Precisa de <c>pwsh</c> 7: o executável vem de <c>AEGIS_TEST_PWSH</c> ou do PATH. Sem ele os fatos não rodam — exceto quando
/// <c>AEGIS_PWSH_GATE_REQUIRED</c> está ativo (job do CI), em que a ausência FALHA em vez de virar verde vazio.
/// </summary>
public sealed class PowerShellFixtureModuleTests
{
    private static readonly Guid Tenant = Guid.Parse("aa000000-0000-0000-0000-0000000000e1");

    private static string? Pwsh()
    {
        var explicitPath = Environment.GetEnvironmentVariable("AEGIS_TEST_PWSH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;
        var names = OperatingSystem.IsWindows() ? new[] { "pwsh.exe" } : new[] { "pwsh" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
                if (File.Exists(Path.Combine(dir, n))) return Path.Combine(dir, n);

        var gate = Environment.GetEnvironmentVariable("AEGIS_PWSH_GATE_REQUIRED");
        if (gate is "1" || string.Equals(gate, "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "AEGIS_PWSH_GATE_REQUIRED está ativo, mas não há pwsh no PATH nem em AEGIS_TEST_PWSH: o processo real NÃO pode ser pulado (falso verde).");
        return null;
    }

    private static string Modules() => Path.Combine(AppContext.BaseDirectory, "Knight", "Fixtures", "psmodules");

    private static T Options<T>(string pwsh) where T : PowerShellAdapterOptions, new() =>
        new() { Executable = pwsh, ModulePath = Modules(), Timeout = TimeSpan.FromSeconds(120) };

    private sealed class TeamsTokens(string teamsToken) : ITeamsTokenClient
    {
        public Task<TeamsAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(new TeamsAdminCredentials("dir-demo-0001", "graph-token-sintetico", teamsToken));
    }

    private sealed class ExchangeTokens(string token) : IExchangeTokenClient
    {
        public Task<ExchangeAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(new ExchangeAdminCredentials("demo.example.com", token));
    }

    private sealed class AppTokens : IMicrosoftAppTokenClient
    {
        public int Calls;

        public Task<string> AcquireAsync(IMicrosoftGraphCredentials credentials, string scope, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult("token-security-compliance-sintetico");
        }
    }

    private static Task<KnightCollectionResult> Teams(string pwsh, string scenario) =>
        new TeamsKnightCollector(new TeamsTokens(scenario), new PowerShellTeamsAdminReader(Options<TeamsPowerShellOptions>(pwsh)))
            .CollectAsync(new KnightCollectionContext(Tenant, new KnightTeamsConfiguration("dir-demo-0001", "client", "secret")));

    private static Task<KnightCollectionResult> Exchange(string pwsh, string scenario) =>
        new ExchangeKnightCollector(new ExchangeTokens(scenario), new PowerShellExchangeAdminReader(Options<ExchangePowerShellOptions>(pwsh)))
            .CollectAsync(new KnightCollectionContext(Tenant, new KnightExchangeOnlineConfiguration("dir-demo-0001", "client", "secret")));

    private static KnightCapabilityStatus Cap(KnightCollectionResult r, KnightCapability c) => r.Capabilities.Single(x => x.Capability == c);

    [Fact]
    public async Task Teams_SucessoControlado_PassaPorTodosOsComandosDeLeitura()
    {
        if (Pwsh() is not { } pwsh) return;

        var result = await Teams(pwsh, "cenario-sucesso");

        result.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.Collected, result.Detail);
        result.State.Should().Be(KnightSourceState.Completed);
        result.TenantConfiguration!.Read<TeamsClientConfiguration>().Items.Should().ContainSingle(c => c.AllowGuestUser == true);
        result.TenantConfiguration.Read<TeamsFederationConfiguration>().Items.Should()
            .ContainSingle(f => f.AllowedDomainsKind == "AllowAllKnownDomains");
        result.TenantConfiguration.Read<TeamsMeetingPolicyConfiguration>().Items.Should().HaveCount(2, "leitura com VÁRIOS objetos");
    }

    [Fact]
    public async Task Teams_CredencialInvalida_RecusaDeAutenticacao_ComODiagnosticoDoScript()
    {
        if (Pwsh() is not { } pwsh) return;

        var result = await Teams(pwsh, "credencial-invalida");

        result.State.Should().Be(KnightSourceState.AuthenticationFailure);
        result.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.AuthenticationFailure);
        var d = result.Capabilities.First().Diagnostic!;
        d.Kind.Should().Be(KnightFailureKind.Connection);
        d.Code.Should().NotBeNullOrWhiteSpace("o identificador técnico sanitizado do script é preservado");
        result.Capabilities.First().Detail.Should().NotContain("AADSTS7000215", "o texto bruto da exceção não atravessa a fronteira");
    }

    /// <summary>Avisos e texto de host do módulo saem na saída padrão do pwsh real — e não invalidam mais o resultado.</summary>
    [Fact]
    public async Task Exchange_AvisosDoModuloNaSaidaPadrao_NaoInvalidamAColeta()
    {
        if (Pwsh() is not { } pwsh) return;

        var output = await new PowerShellExchangeAdminReader(Options<ExchangePowerShellOptions>(pwsh))
            .ReadAsync(new ExchangeAdminCredentials("demo.example.com", "cenario-aviso"));

        output.Process!.Clean.Should().BeTrue();
        output.Process.NoiseLines.Should().BeGreaterThan(0, "o pwsh escreve WARNING e Write-Host na saída padrão quando redirecionada");
        output.Connected.Should().BeTrue();
        output.Reads.Should().HaveCount(12).And.OnlyContain(r => r.Ok);
    }

    [Fact]
    public async Task Exchange_SucessoControlado_PassaPorTodosOsComandos_ComODiretorioPrivadoDaExecucao()
    {
        if (Pwsh() is not { } pwsh) return;

        var result = await Exchange(pwsh, "cenario-sucesso");

        result.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.Collected, result.Detail);
        result.State.Should().Be(KnightSourceState.Completed);
        var config = result.TenantConfiguration!;
        config.Read<ExchangeOrganizationConfiguration>().Items.Should().ContainSingle(o => o.CustomerLockBoxEnabled == true,
            "leitura de UM objeto chega como lista de um item (antes o pipeline desembrulhava e o AEGIS lia lista vazia)");
        config.Read<ExchangeRoleAssignmentPolicyConfiguration>().Items.Should().HaveCount(2, "leitura com VÁRIOS objetos");
        var rules = config.Read<ExchangeTransportRuleConfiguration>();
        rules.Collected.Should().BeTrue();
        rules.Items.Should().BeEmpty("leitura com ZERO objetos é lista vazia coletada — não falha");
    }

    private sealed class Dns : IDnsTxtResolver
    {
        public Task<DnsTxtResult> ResolveTxtAsync(string name, CancellationToken ct) =>
            Task.FromResult(DnsTxtResult.Found(new[] { "v=spf1 -all" }));
    }

    /// <summary>
    /// Defender para Office 365 pelo adaptador de proteção REAL: todos os comandos de leitura do perfil, com respostas de zero,
    /// um e vários objetos. As leituras de objeto único (política ATP, contas prioritárias) eram as afetadas pelo desembrulho.
    /// </summary>
    [Fact]
    public async Task Defender_SucessoControlado_ZeroUmEVariosObjetos()
    {
        if (Pwsh() is not { } pwsh) return;
        var collector = new DefenderForOffice365KnightCollector(new ExchangeTokens("cenario-sucesso"),
            new PowerShellProtectionAdminReader(Options<ProtectionPowerShellOptions>(pwsh)), new Dns());

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant,
            new KnightMicrosoftServiceConfiguration(KnightSourceType.MicrosoftDefenderForOffice365, "dir-demo-0001", "client", "secret")));

        foreach (var c in DefenderForOffice365KnightCollector.AdapterCapabilities)
            Cap(result, c).Outcome.Should().Be(KnightCapabilityOutcome.Collected, $"{c}: {Cap(result, c).Detail}");
        var config = result.TenantConfiguration!;
        config.Read<DefenderAtpPolicy>().Items.Should().ContainSingle(a => a.EnableSafeDocs == true, "UM objeto");
        config.Read<DefenderPriorityAccounts>().Items.Should().ContainSingle(p => p.EnablePriorityAccountProtection == true, "UM objeto");
        config.Read<DefenderAcceptedDomain>().Items.Should().HaveCount(2, "VÁRIOS objetos");
        config.Read<DefenderTeamsProtection>().Items.Should().BeEmpty("ZERO objetos");
        config.Read<DefenderTeamsProtection>().Collected.Should().BeTrue();
    }

    /// <summary>O processo real morre no meio (código 137): leituras concluídas preservadas, a iniciada falha, as seguintes não tentadas.</summary>
    [Fact]
    public async Task Exchange_ProcessoMorreNoMeio_PreservaLeiturasConcluidas()
    {
        if (Pwsh() is not { } pwsh) return;

        var result = await Exchange(pwsh, "cenario-queda");

        Cap(result, KnightCapability.ExchangeOrganizationConfig).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        Cap(result, KnightCapability.ExchangeTransportConfig).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        var sharing = Cap(result, KnightCapability.ExchangeSharingPolicies);
        sharing.Outcome.Should().Be(KnightCapabilityOutcome.Error);
        sharing.Detail.Should().Contain("iniciada").And.Contain("código 137");
        Cap(result, KnightCapability.ExchangeMailboxes).Outcome.Should().Be(KnightCapabilityOutcome.NotAttempted);
        result.State.Should().Be(KnightSourceState.PartialCollection);
        result.TenantConfiguration!.Read<ExchangeOrganizationConfiguration>().Items.Should().ContainSingle();
    }

    /// <summary>
    /// Purview: fora do Windows a sessão do Security &amp; Compliance NÃO é tentada (o módulo de fixture recusa a chamada fora do
    /// Windows) e nem o token dela é pedido; DLP e rótulos ficam como limitação de plataforma, e a auditoria segue coletada.
    /// No Windows, a sessão é tentada e as duas leituras passam pelos comandos.
    /// </summary>
    [Fact]
    public async Task Purview_SessaoDeConformidade_LimitacaoDetectadaAntesDaSessao_ForaDoWindows()
    {
        if (Pwsh() is not { } pwsh) return;
        var appTokens = new AppTokens();
        var collector = new PurviewKnightCollector(new ExchangeTokens("cenario-sucesso"),
            new PowerShellProtectionAdminReader(Options<ProtectionPowerShellOptions>(pwsh)), appTokens);

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant,
            new KnightMicrosoftServiceConfiguration(KnightSourceType.MicrosoftPurview, "dir-demo-0001", "client", "secret")));

        Cap(result, KnightCapability.PurviewAuditConfig).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        if (OperatingSystem.IsWindows())
        {
            Cap(result, KnightCapability.PurviewDlpPolicies).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
            Cap(result, KnightCapability.PurviewLabelPolicies).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
            return;
        }

        appTokens.Calls.Should().Be(0, "na plataforma sem Security & Compliance PowerShell o token dessa sessão nem é pedido");
        foreach (var c in new[] { KnightCapability.PurviewDlpPolicies, KnightCapability.PurviewLabelPolicies })
        {
            var cap = Cap(result, c);
            cap.Outcome.Should().Be(KnightCapabilityOutcome.PlatformUnsupported);
            cap.Detail.Should().Contain("Limitação de plataforma").And.Contain("Não é falta de permissão");
            cap.Diagnostic!.Kind.Should().Be(KnightFailureKind.PlatformUnsupported);
        }
        result.State.Should().Be(KnightSourceState.PartialCollection, "lacuna explícita — nunca \"coleta concluída\"");
    }
}
