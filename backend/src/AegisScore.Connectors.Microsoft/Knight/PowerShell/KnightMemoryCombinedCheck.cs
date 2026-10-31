using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight.Exchange;
using AegisScore.Connectors.Microsoft.Knight.Protection;
using AegisScore.Connectors.Microsoft.Knight.Teams;
using AegisScore.Domain;
using Microsoft.Extensions.Logging;

namespace AegisScore.Connectors.Microsoft.Knight.PowerShell;

/// <summary>
/// [AEGIS-KNIGHT-MEMORY-BUDGET] Reprodução COMBINADA dentro da API real, executada pelo CI num contêiner limitado:
/// <c>dotnet AegisScore.Api.dll --knight-memory-combined-check</c> sobe o host inteiro e, quando o arquivo de disparo aparece (a API já
/// aquecida por requisições HTTP com dados sintéticos), roda em sequência, NESTE processo e com o monitor real de memória:
///   1. rodadas da coleta sintética de 5000 caixas, para o heap da API chegar ao estado que uma sincronização real deixa — as fases seguintes
///      começam com a API já carregada, como a importação do Teams depois da coleta do Entra ID em produção;
///   2. os quatro carregamentos do módulo OFICIAL da imagem, na ordem da sincronização (Teams, Exchange, Defender e Purview);
///   3. o percurso funcional dos coletores reais com os módulos de FIXTURE, que importam o oficial antes dos substitutos.
/// Não abre rota nova, não autentica e não fala com locatário. O que ela não prova está em <see cref="NotProven"/>.
/// </summary>
public static class KnightMemoryCombinedCheck
{
    public const string Argument = "--knight-memory-combined-check";
    public const string SuccessMarker = "memoria-combinada=OK";
    public const string FailureMarker = "memoria-combinada=FALHA";

    public const string NotProven =
        "NÃO PROVADO: consumo das sessões reais da Microsoft (Connect-*, comandos de sessão), volume e formato reais do locatário, e o Render em si.";

    private static readonly Guid Tenant = Guid.Parse("00000000-0000-4000-8000-00000000c0de");

    public static bool Requested(string[]? args) =>
        args is not null && args.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));

    public static async Task<bool> RunAsync(
        TeamsPowerShellOptions teams, ExchangePowerShellOptions exchange, ProtectionPowerShellOptions protection, string fixtureModules,
        int warmRounds, ILoggerFactory logs, TextWriter output, CancellationToken ct)
    {
        var ok = true;
        void Line(string name, PowerShellRunReport? report, bool success, string detail = "")
        {
            output.WriteLine(
                $"combinado {name}: {(success ? "ok" : "FALHA")} "
                + (report is null ? detail : $"terminação={report.Termination} estágios={string.Join(">", report.Stages)}"));
            output.Flush();
            ok &= success;
        }

        var teamsReader = new PowerShellTeamsAdminReader(teams, logs.CreateLogger<PowerShellTeamsAdminReader>());
        var exchangeReader = new PowerShellExchangeAdminReader(exchange, logs.CreateLogger<PowerShellExchangeAdminReader>());
        var protectionReader = new PowerShellProtectionAdminReader(protection, logs.CreateLogger<PowerShellProtectionAdminReader>());

        var fixtureTeams = new TeamsPowerShellOptions { Executable = teams.Executable, ModulePath = fixtureModules, Timeout = teams.Timeout };
        var fixtureExchange = new ExchangePowerShellOptions { Executable = exchange.Executable, ModulePath = fixtureModules, Timeout = exchange.Timeout };
        var fixtureProtection = new ProtectionPowerShellOptions { Executable = exchange.Executable, ModulePath = fixtureModules, Timeout = exchange.Timeout };

        Task<KnightCollectionResult> Mailboxes() =>
            new ExchangeKnightCollector(new FixtureExchangeTokens("cenario-volume"),
                    new PowerShellExchangeAdminReader(fixtureExchange, logs.CreateLogger<PowerShellExchangeAdminReader>()))
                .CollectAsync(new KnightCollectionContext(Tenant, new KnightExchangeOnlineConfiguration("dir-sintetico", "client", "secret")), ct);

        // Método à parte: o resultado sai de escopo a cada rodada, em vez de ficar preso no estado desta execução e contar como memória da API.
        async Task Round(string name) => Report(Line, name, await Mailboxes());
        for (var round = 1; round <= warmRounds; round++)
            await Round($"aquecimento-5000-{round}");
        output.WriteLine($"combinado api-carregada: heapApi={GC.GetTotalMemory(false)} comprometido={GC.GetGCMemoryInfo().TotalCommittedBytes}");

        // A mesma ordem sequencial da sincronização: cada etapa aguarda a anterior.
        var teamsImport = (await teamsReader.CheckRuntimeAsync(ct)).Process;
        Line("oficial-teams", teamsImport, Imported(teamsImport));
        var exchangeImport = (await exchangeReader.CheckRuntimeAsync(ct)).Process;
        Line("oficial-exchange", exchangeImport, Imported(exchangeImport));
        var defenderImport = (await protectionReader.CheckRuntimeAsync(ct)).Exchange.Process;
        Line("oficial-defender", defenderImport, Imported(defenderImport));
        var purviewImport = (await protectionReader.CheckRuntimeAsync(ct)).Exchange.Process;
        Line("oficial-purview", purviewImport, Imported(purviewImport));

        Report(Line, "fixture-teams", await new TeamsKnightCollector(new FixtureTeamsTokens(),
                new PowerShellTeamsAdminReader(fixtureTeams, logs.CreateLogger<PowerShellTeamsAdminReader>()))
            .CollectAsync(new KnightCollectionContext(Tenant, new KnightTeamsConfiguration("dir-sintetico", "client", "secret")), ct));

        await Round("fixture-exchange-5000");

        Report(Line, "fixture-defender", await new DefenderForOffice365KnightCollector(new FixtureExchangeTokens("cenario-sucesso"),
                new PowerShellProtectionAdminReader(fixtureProtection, logs.CreateLogger<PowerShellProtectionAdminReader>()), new NoDns())
            .CollectAsync(new KnightCollectionContext(Tenant,
                new KnightMicrosoftServiceConfiguration(KnightSourceType.MicrosoftDefenderForOffice365, "dir-sintetico", "client", "secret")), ct));

        Report(Line, "fixture-purview", await new PurviewKnightCollector(new FixtureExchangeTokens("cenario-sucesso"),
                new PowerShellProtectionAdminReader(fixtureProtection, logs.CreateLogger<PowerShellProtectionAdminReader>()), new NoAppTokens())
            .CollectAsync(new KnightCollectionContext(Tenant,
                new KnightMicrosoftServiceConfiguration(KnightSourceType.MicrosoftPurview, "dir-sintetico", "client", "secret")), ct));

        output.WriteLine("alcance: API real + módulo oficial importado no mesmo contêiner, sem locatário.");
        output.WriteLine(NotProven);
        output.WriteLine(ok ? SuccessMarker : FailureMarker);
        output.Flush();
        return ok;
    }

    private static bool Imported(PowerShellRunReport? report) =>
        report is { Clean: true, Termination: PowerShellTermination.Completed } && report.Reached("imported");

    /// <summary>Nenhuma capacidade pode ter sido interrompida por memória ou pelo processo; o resto é a resposta da fixture.</summary>
    private static void Report(Action<string, PowerShellRunReport?, bool, string> line, string name, KnightCollectionResult result)
    {
        var processFailures = result.Capabilities.Where(c => c.Diagnostic?.Kind is KnightFailureKind.MemoryLimit
            or KnightFailureKind.ProcessExit or KnightFailureKind.ProcessNoResult or KnightFailureKind.Incomplete
            or KnightFailureKind.RuntimeUnavailable or KnightFailureKind.Timeout).ToList();
        var collected = result.Capabilities.Count(c => c.Outcome == KnightCapabilityOutcome.Collected);
        var causes = string.Join(",", processFailures.Select(c => c.Diagnostic!.Kind.ToString()).Distinct());
        line(name, null, collected > 0 && processFailures.Count == 0,
            $"estado={result.State} coletadas={collected}/{result.Capabilities.Count}{(causes.Length == 0 ? "" : " causas=" + causes)}");
    }

    private sealed class FixtureTeamsTokens : ITeamsTokenClient
    {
        public Task<TeamsAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(new TeamsAdminCredentials("dir-sintetico", "graph-token-sintetico", "cenario-sucesso"));
    }

    private sealed class FixtureExchangeTokens(string scenario) : IExchangeTokenClient
    {
        public Task<ExchangeAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(new ExchangeAdminCredentials("demo.example.com", scenario));
    }

    private sealed class NoAppTokens : IMicrosoftAppTokenClient
    {
        public Task<string> AcquireAsync(IMicrosoftGraphCredentials credentials, string scope, CancellationToken ct = default) =>
            Task.FromResult("token-sintetico");
    }

    private sealed class NoDns : IDnsTxtResolver
    {
        public Task<DnsTxtResult> ResolveTxtAsync(string name, CancellationToken ct) =>
            throw new IOException("Sem DNS na reprodução combinada.");
    }
}
