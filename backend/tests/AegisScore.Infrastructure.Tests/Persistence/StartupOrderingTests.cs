using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-AUD-052] Prova da premissa que sustenta o posicionamento do guard no <c>Program.cs</c>.
///
/// O guard de prontidão roda entre <c>builder.Build()</c> e <c>app.Run()</c>. Isso só impede os
/// workers de processar trabalho se <c>Build()</c> realmente NÃO iniciar hosted services — caso
/// contrário, o <c>DocumentAnalysisWorker</c> (que varre órfãos no arranque) e o
/// <c>PolicyIngestionWorker</c> (que sincroniza no boot) já teriam escrito antes da verificação.
///
/// O teste trava esse comportamento em vez de confiar nele implicitamente.
/// </summary>
public sealed class StartupOrderingTests
{
    private sealed class SpyWorker : IHostedService
    {
        private readonly StartFlag _flag;
        public SpyWorker(StartFlag flag) => _flag = flag;

        public Task StartAsync(CancellationToken ct)
        {
            _flag.Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StartFlag
    {
        public bool Started { get; set; }
    }

    [Fact]
    public async Task Build_NaoIniciaHostedServices_MasStart_Inicia()
    {
        var flag = new StartFlag();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(flag);
        builder.Services.AddHostedService<SpyWorker>();

        using var host = builder.Build();

        flag.Started.Should().BeFalse(
            "é isto que permite ao guard de prontidão abortar o boot ANTES de qualquer worker " +
            "escrever no banco");

        // Só a partida efetiva do host aciona os hosted services — o que, no Program.cs da API,
        // acontece depois do guard.
        await host.StartAsync();
        flag.Started.Should().BeTrue();
        await host.StopAsync();
    }
}
