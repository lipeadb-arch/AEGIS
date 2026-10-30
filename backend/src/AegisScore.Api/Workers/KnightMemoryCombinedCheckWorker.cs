using AegisScore.Connectors.Microsoft.Knight.Exchange;
using AegisScore.Connectors.Microsoft.Knight.PowerShell;
using AegisScore.Connectors.Microsoft.Knight.Protection;
using AegisScore.Connectors.Microsoft.Knight.Teams;

namespace AegisScore.Api.Workers;

/// <summary>
/// [AEGIS-KNIGHT-MEMORY-BUDGET] Só registrado com <see cref="KnightMemoryCombinedCheck.Argument"/> na linha de comando: nada aqui é
/// alcançável por requisição. Espera o arquivo de disparo (a API já aquecida pelo CI) e roda a reprodução combinada no processo da API.
/// </summary>
public sealed class KnightMemoryCombinedCheckWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _logs;

    public KnightMemoryCombinedCheckWorker(IServiceProvider services, IConfiguration configuration, ILoggerFactory logs)
    {
        _services = services;
        _configuration = configuration;
        _logs = logs;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var trigger = _configuration["Knight:MemoryCheck:Trigger"] ?? "/tmp/aegis-memory-check-go";
        var fixtures = _configuration["Knight:MemoryCheck:FixtureModules"]
            ?? throw new InvalidOperationException("Knight:MemoryCheck:FixtureModules é obrigatório na reprodução combinada.");
        while (!File.Exists(trigger))
            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);

        try
        {
            await KnightMemoryCombinedCheck.RunAsync(
                _services.GetRequiredService<TeamsPowerShellOptions>(),
                _services.GetRequiredService<ExchangePowerShellOptions>(),
                _services.GetRequiredService<ProtectionPowerShellOptions>(),
                fixtures, _logs, Console.Out, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Out.WriteLine($"{KnightMemoryCombinedCheck.FailureMarker} exceção={ex.GetType().Name}");
            Console.Out.Flush();
        }
    }
}
