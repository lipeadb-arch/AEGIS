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

    /// <summary>
    /// [AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] Recusa no COMANDO, com a sessão aberta: o script real extrai o status HTTP e a
    /// correlação de forma estruturada (número e GUID) e nada mais da mensagem — nem o marcador com forma de segredo.
    /// </summary>
    [Fact]
    public async Task Teams_ComandoRecusado_StatusECorrelacaoEstruturados_SemAMensagem()
    {
        if (Pwsh() is not { } pwsh) return;

        var result = await Teams(pwsh, "comando-recusado");

        result.State.Should().Be(KnightSourceState.InsufficientPermission);
        result.Capabilities.Should().HaveCount(TeamsKnightCollector.Capabilities.Count).And.OnlyContain(c =>
            c.Outcome == KnightCapabilityOutcome.InsufficientPermission
            && c.Diagnostic!.Kind == KnightFailureKind.Command
            && c.Diagnostic.HttpStatus == 403
            && c.Diagnostic.ServiceRequestId == "0b7a6c1e-0000-4000-8000-0000000000c1");
        result.Capabilities.Should().OnlyContain(c => c.Detail!.Contains("Autorização recusada no comando") && c.Detail.Contains("HTTP 403"));
        var tudo = string.Join(" ", result.Capabilities.Select(c => c.Detail + " " + c.Diagnostic!.Code));
        tudo.Should().NotContain("AEGIS-MARCADOR-FIXTURE-0003").And.NotContain("Bearer");
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

        // [AEGIS-KNIGHT-RUNTIME-DIAGNOSIS] As caixas vêm de Get-EXOMailbox com seleção explícita, e as distinções chegam ao ADM:
        // lista de auditoria vazia ≠ completa, encaminhamento nulo ≠ configurado, falso ≠ verdadeiro, política nula ≠ declarada.
        var audit = config.Read<ExchangeMailboxAuditInventory>().Items.Single();
        audit.MailboxTotal.Should().Be(2, "usuário e compartilhada; a sala fica fora do universo da auditoria");
        audit.MailboxesWithGaps.Should().ContainSingle(r => r.UserPrincipalName == "financeiro@demo.example.com"
            && r.MissingOwnerActions.Count == ExchangeMailboxAuditInventory.RequiredOwnerActions.Count && r.MissingDelegateActions.Count == 0);
        var forwarding = config.Read<ExchangeMailboxForwardingInventory>().Items.Single();
        forwarding.MailboxTotal.Should().Be(3);
        forwarding.MailboxesWithForwarding.Should().ContainSingle(r => r.ForwardingSmtpAddress == "smtp:arquivo@example.net"
            && r.ForwardingAddress == null && r.DeliverToMailboxAndForward == true);
        config.Read<ExchangeSharedMailboxInventory>().Items.Single().Mailboxes.Should()
            .ContainSingle(m => m.UserPrincipalName == "financeiro@demo.example.com" && m.SignInBlocked == true);
        var reach = config.Read<ExchangePolicyReachInventory>().Items.Single();
        reach.MailboxesWithoutDeclaration.Should().Be(1, "a compartilhada não declara política de compartilhamento — nula, não distribuída");
        reach.Entries.Should().Contain(e => e.PolicyName == "Default Sharing Policy" && e.MailboxCount == 2);
        var smtp = config.Read<ExchangeSmtpAuthOverrideInventory>().Items.Single();
        (smtp.InheritingOrganizationCount, smtp.ExplicitlyDisabledCount).Should().Be((1, 1), "nulo herda; verdadeiro desabilita");
        smtp.ExplicitlyEnabled.Should().ContainSingle(r => r.UserPrincipalName == "financeiro@demo.example.com", "falso HABILITA na caixa");
        Cap(result, KnightCapability.ExchangeMailboxes).Detail.Should().BeNull("a enumeração cabe no teto: não é truncada");
    }

    /// <summary>
    /// [AEGIS-KNIGHT-RUNTIME-DIAGNOSIS] Enumeração GRANDE (6000 caixas, contas e configurações de acesso no módulo de fixture, teto 5000):
    /// as leituras chegam inteiras até o teto pelo processo real, são marcadas truncadas e nenhuma lista truncada aprova a organização.
    /// </summary>
    [Fact]
    public async Task Exchange_EnumeracaoGrande_ChegaAteOTeto_TruncadaENuncaCompleta()
    {
        if (Pwsh() is not { } pwsh) return;

        var result = await Exchange(pwsh, "cenario-volume");

        foreach (var c in new[] { KnightCapability.ExchangeMailboxes, KnightCapability.ExchangeMailboxSignIn, KnightCapability.ExchangeCasMailboxes })
        {
            Cap(result, c).Outcome.Should().Be(KnightCapabilityOutcome.Collected, c.ToString());
            Cap(result, c).Detail.Should().Contain("atingiu o teto", c.ToString());
        }
        Cap(result, KnightCapability.ExchangeMailboxes).Detail.Should().Contain("Get-EXOMailbox");
        var config = result.TenantConfiguration!;
        var forwarding = config.Read<ExchangeMailboxForwardingInventory>().Items.Single();
        forwarding.MailboxTotal.Should().Be(5000, "o teto explícito da enumeração — nada além, nada aquém");
        forwarding.ListComplete.Should().BeFalse("lista truncada sem encaminhamento não é organização sem encaminhamento");
        config.Read<ExchangeMailboxAuditInventory>().Items.Single().ListComplete.Should().BeFalse();
        config.Read<ExchangePolicyReachInventory>().Items.Single().ListComplete.Should().BeFalse();
        config.Read<ExchangeSmtpAuthOverrideInventory>().Items.Single().Should()
            .Match<ExchangeSmtpAuthOverrideInventory>(s => s.MailboxTotal == 5000 && s.InheritingOrganizationCount == 5000 && !s.ListComplete);
        result.Detail.Should().Contain("teto de 5000");
    }

    /// <summary>
    /// [AEGIS-KNIGHT-RUNTIME-DIAGNOSIS] O processo real morre DEPOIS da enumeração das caixas: sem a lista que o script guardava, a
    /// leitura grande continua preservada — o quadro dela já tinha saído — e chega ao ADM.
    /// </summary>
    [Fact]
    public async Task Exchange_QuedaDepoisDaEnumeracaoDasCaixas_PreservaAsCaixas()
    {
        if (Pwsh() is not { } pwsh) return;

        var result = await Exchange(pwsh, "cenario-queda-tardia");

        Cap(result, KnightCapability.ExchangeMailboxes).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        var signIn = Cap(result, KnightCapability.ExchangeMailboxSignIn);
        signIn.Outcome.Should().Be(KnightCapabilityOutcome.Error);
        signIn.Detail.Should().Contain("iniciada").And.Contain("código 137");
        Cap(result, KnightCapability.ExchangeCasMailboxes).Outcome.Should().Be(KnightCapabilityOutcome.NotAttempted);
        result.State.Should().Be(KnightSourceState.PartialCollection);
        result.TenantConfiguration!.Read<ExchangeMailboxForwardingInventory>().Items.Single().MailboxesWithForwarding.Should().ContainSingle();
        result.TenantConfiguration.Read<ExchangeSharedMailboxInventory>().Items.Single().SignInStateResolved
            .Should().BeFalse("a leitura das contas não concluiu: o estado de entrada fica desconhecido, nunca bloqueado");
    }

    /// <summary>
    /// [AEGIS-KNIGHT-RUNTIME-DIAGNOSIS] Get-EXOMailbox devolve o objeto SEM uma propriedade pedida: o script não a converte em nulo
    /// (o que leria "sem encaminhamento"); a leitura falha como erro de CONTRATO, sem inventário de caixas, e as demais seguem.
    /// </summary>
    [Fact]
    public async Task Exchange_PropriedadePedidaAusente_ErroDeContrato_SemInventarioPorOmissao()
    {
        if (Pwsh() is not { } pwsh) return;

        var result = await Exchange(pwsh, "cenario-projecao-incompleta");

        var mailboxes = Cap(result, KnightCapability.ExchangeMailboxes);
        mailboxes.Outcome.Should().Be(KnightCapabilityOutcome.Error);
        mailboxes.Diagnostic!.Kind.Should().Be(KnightFailureKind.Contract);
        mailboxes.Diagnostic.Code.Should().StartWith("AegisContractPropertyMissing.ForwardingSmtpAddress");
        mailboxes.Detail.Should().Contain("ForwardingSmtpAddress").And.Contain("não falta de permissão");
        result.TenantConfiguration!.Read<ExchangeMailboxForwardingInventory>().Collected.Should().BeFalse();
        Cap(result, KnightCapability.ExchangeCasMailboxes).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        result.State.Should().Be(KnightSourceState.PartialCollection);
    }

    private sealed class StageLogger : Microsoft.Extensions.Logging.ILogger<PowerShellExchangeAdminReader>
    {
        private int _mailboxes;
        public bool ReadingMailboxes => Volatile.Read(ref _mailboxes) != 0;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state,
            Exception? error, Func<TState, Exception?, string> format)
        {
            if (format(state, error).Contains("Estagio=read ExchangeMailboxes", StringComparison.Ordinal)) Interlocked.Exchange(ref _mailboxes, 1);
        }
    }

    /// <summary>
    /// [AEGIS-KNIGHT-RUNTIME-DIAGNOSIS] Pressão de memória durante a enumeração das caixas, com o pwsh real: o AEGIS encerra o filho,
    /// as leituras anteriores ficam, e a caixa interrompida é MemoryLimit — nunca falta de permissão nem coleta concluída.
    /// </summary>
    [Fact]
    public async Task Exchange_PressaoDeMemoriaNaEnumeracao_MemoryLimitSemPermissaoInventada()
    {
        if (Pwsh() is not { } pwsh) return;
        var log = new StageLogger();
        var options = Options<ExchangePowerShellOptions>(pwsh);
        options.MemoryProbe = () => new ContainerMemorySample(log.ReadingMailboxes ? 500 : 100, 512);
        var collector = new ExchangeKnightCollector(new ExchangeTokens("cenario-volume"), new PowerShellExchangeAdminReader(options, log));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, new KnightExchangeOnlineConfiguration("dir-demo-0001", "client", "secret")));

        Cap(result, KnightCapability.ExchangeOrganizationConfig).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        Cap(result, KnightCapability.ExchangeOutboundSpamFilterPolicies).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        var mailboxes = Cap(result, KnightCapability.ExchangeMailboxes);
        mailboxes.Outcome.Should().Be(KnightCapabilityOutcome.Error);
        mailboxes.Diagnostic!.Kind.Should().Be(KnightFailureKind.MemoryLimit);
        mailboxes.Detail.Should().Contain("memória").And.Contain("não indica falta de permissão");
        result.Capabilities.Should().NotContain(c => c.Outcome == KnightCapabilityOutcome.InsufficientPermission);
        result.State.Should().Be(KnightSourceState.PartialCollection);
    }

    /// <summary>
    /// [AEGIS-KNIGHT-MEMORY-BUDGET] O desconto de arquivo mapeado vale amostra por amostra: com o mesmo consumo, a coleta com PSI válido termina;
    /// se o PSI some durante a enumeração das caixas, o filho é encerrado por memória, com as leituras anteriores preservadas.
    /// </summary>
    [Fact]
    public async Task Exchange_PsiPerdidoDuranteAColeta_RetiraODescontoMapeado_MemoryLimitComLeiturasPreservadas()
    {
        if (Pwsh() is not { } pwsh) return;
        const string stat = "anon 1000\nfile 300\nshmem 0\nfile_mapped 250\nactive_file 300\ninactive_file 0\nfile_dirty 0\nfile_writeback 0\nkernel 5\n";
        const string psi = "full avg10=0.00 avg60=0.00 avg300=0.00 total=0\n";

        async Task<KnightCollectionResult> Collect(Func<StageLogger, string?> pressure)
        {
            var log = new StageLogger();
            var options = Options<ExchangePowerShellOptions>(pwsh);
            options.MemoryProbe = () => ContainerMemorySample.Parse("500", "512", stat, pressure(log));
            return await new ExchangeKnightCollector(new ExchangeTokens("cenario-volume"), new PowerShellExchangeAdminReader(options, log))
                .CollectAsync(new KnightCollectionContext(Tenant, new KnightExchangeOnlineConfiguration("dir-demo-0001", "client", "secret")));
        }

        var valid = await Collect(_ => psi);
        Cap(valid, KnightCapability.ExchangeMailboxes).Outcome.Should().Be(KnightCapabilityOutcome.Collected);

        var lost = await Collect(log => log.ReadingMailboxes ? null : psi);
        Cap(lost, KnightCapability.ExchangeOrganizationConfig).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        Cap(lost, KnightCapability.ExchangeMailboxes).Diagnostic!.Kind.Should().Be(KnightFailureKind.MemoryLimit);
        lost.State.Should().Be(KnightSourceState.PartialCollection);
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
