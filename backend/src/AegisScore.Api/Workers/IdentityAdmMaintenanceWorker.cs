using Microsoft.Extensions.Options;
using AegisScore.Application.Identity.Adm;

namespace AegisScore.Api.Workers;

/// <summary>
/// [AEGIS-ADM-02] Motor da manutenção do ADM de identidade: consolida os meses e — só quando explicitamente
/// configurado — aplica a retenção.
///
/// Motor NATIVO (<see cref="BackgroundService"/> + <see cref="PeriodicTimer"/>), sem agendador externo: o
/// mesmo idioma do <see cref="AegisScoreSnapshotWorker"/> e dos demais workers do projeto. Sendo Singleton,
/// NUNCA injeta serviços scoped direto — abre um escopo por ciclo e resolve o serviço de manutenção lá dentro.
///
/// O ciclo é DRENANTE: enquanto a passada terminar com trabalho pendente (lote esgotado), a seguinte retoma
/// pelo cursor imediatamente, sem esperar o próximo tique. É assim que um ambiente com anos de acúmulo
/// converge sem uma única transação gigante — e sem que o teto por passada vire um limite permanente.
///
/// E o cursor SOBREVIVE ao ciclo. Recomeçar do início a cada tique parece inofensivo e não é: atingido o teto
/// de passadas encadeadas, as primeiras origens voltariam a consumir todo o orçamento no tique seguinte, e as
/// últimas nunca seriam visitadas — o ambiente pareceria estar sendo mantido enquanto uma parte dele jamais
/// era tocada. Guardar a posição entre ciclos transforma isso em rodízio: cada tique continua de onde o
/// anterior parou, e o cursor só volta ao início quando a varredura tiver dado a volta inteira.
/// </summary>
public sealed class IdentityAdmMaintenanceWorker : BackgroundService
{
    /// <summary>
    /// Teto de passadas encadeadas por tique. Existe para que um erro de contagem (uma passada que sempre se
    /// declara incompleta) vire lentidão observável e não um laço infinito consumindo banco.
    /// </summary>
    private const int MaxPassesPerCycle = 20;

    private readonly IServiceScopeFactory _scopes;
    private readonly IdentityAdmMaintenanceOptions _options;
    private readonly ILogger<IdentityAdmMaintenanceWorker> _log;

    /// <summary>Onde a varredura parou — entre passadas E entre ciclos. <c>null</c> = do começo.</summary>
    private IdentityAdmCursor? _cursor;

    public IdentityAdmMaintenanceWorker(
        IServiceScopeFactory scopes,
        IOptions<IdentityAdmMaintenanceOptions> options,
        ILogger<IdentityAdmMaintenanceWorker> log)
    {
        _scopes = scopes;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("Manutenção do ADM de identidade DESABILITADA por configuração — nada será consolidado nem removido.");
            return;
        }

        if (!_options.TryValidate(out var erro))
        {
            // Configuração inválida NÃO vira padrão silencioso: seguir com outros números faria a manutenção
            // rodar com limites que ninguém escolheu.
            _log.LogError("Manutenção do ADM de identidade não iniciada: {Erro}", erro);
            return;
        }

        if (!_options.RemovalEnabled)
        {
            _log.LogInformation(
                "Retenção do ADM de identidade em modo CONSOLIDAÇÃO APENAS: a remoção automática exige "
                + "IdentityAdm:Maintenance:RemovalEnabled=true. Nada será apagado.");
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.InitialDelaySeconds), ct);

            using var timer = new PeriodicTimer(TimeSpan.FromHours(_options.PeriodHours));
            do
            {
                try
                {
                    await RunCycleAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Um ciclo com falha NUNCA derruba o worker; tenta de novo no próximo tique.
                    _log.LogError(ex, "Ciclo de manutenção do ADM de identidade falhou; retomará no próximo tique.");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Encerramento do host durante a espera — saída limpa, sem ruído de erro no log.
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        for (var passada = 0; passada < MaxPassesPerCycle; passada++)
        {
            if (ct.IsCancellationRequested) return;

            using var scope = _scopes.CreateScope();
            var manutencao = scope.ServiceProvider.GetRequiredService<IIdentityAdmMaintenanceService>();

            var relatorio = await manutencao.RunAsync(
                new IdentityAdmMaintenanceRequest(
                    Consolidate: _options.ConsolidationEnabled,
                    Remove: _options.RemovalEnabled,
                    MaxDirectories: _options.MaxDirectoriesPerCycle,
                    MaxAcquisitionsPerDirectory: _options.MaxAcquisitionsPerDirectory,
                    ResumeAfter: _cursor),
                ct);

            if (relatorio.Directories.Count > 0)
            {
                _log.LogInformation(
                    "Manutenção do ADM: {Origens} origem(ns), {Meses} mês(es) consolidado(s), "
                    + "{Removidas} aquisição(ões) removida(s), {Expirado} com detalhe expirado, "
                    + "{Protegidas} retida(s) por referência do produto.",
                    relatorio.Directories.Count,
                    relatorio.Directories.Sum(d => d.MonthsConsolidated),
                    relatorio.Directories.Sum(d => d.AcquisitionsRemoved),
                    relatorio.Directories.Sum(d => d.DetailExpired),
                    relatorio.Directories.Sum(d => d.ProtectedRetained));
            }

            // Origens que falharam JÁ ficaram para trás (o cursor avançou por cima delas) — o registro aqui
            // existe para que uma falha crônica seja visível, e não apenas silenciosamente pulada.
            foreach (var falha in relatorio.Failed)
            {
                _log.LogError(
                    "Manutenção do ADM não concluiu {Namespace} (tenant {TenantId}): {Motivo}. Nada foi "
                    + "escrito para esta origem; ela será tentada de novo na próxima varredura.",
                    falha.Directory.DirectoryNamespace, falha.Directory.TenantId, falha.Failure);
            }

            if (relatorio.Completed)
            {
                // Deu a volta inteira: a próxima varredura recomeça do início.
                _cursor = null;
                return;
            }

            // Sem cursor e sem conclusão significa que nem a primeira origem coube no lote — continuar
            // repetiria a mesma passada para sempre. Para e espera o próximo tique, SEM perder a posição.
            if (relatorio.NextCursor is null && relatorio.Directories.Count == 0) return;

            _cursor = relatorio.NextCursor;
        }

        _log.LogWarning(
            "Manutenção do ADM interrompeu o ciclo após {Max} passadas encadeadas — ainda há trabalho pendente. "
            + "O próximo tique CONTINUA de {Namespace} em diante, e não do começo.",
            MaxPassesPerCycle, _cursor?.Directory.DirectoryNamespace ?? "(início)");
    }
}
