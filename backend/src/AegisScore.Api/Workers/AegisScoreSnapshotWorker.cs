using Microsoft.EntityFrameworkCore;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Workers;

/// <summary>
/// Inteligência temporal do Aegis Score: à meia-noite (UTC) grava UMA foto agregada por tenant em
/// <see cref="TenantScoreSnapshot"/> — o numerador (SUM CurrentScore) e o denominador
/// (SUM MaxScorePoints), consolidados a partir do <see cref="TenantControlState"/>. É essa série
/// diária que alimenta o gráfico de tendência de postura (modelo Microsoft Secure Score).
///
/// Motor NATIVO (BackgroundService + PeriodicTimer), sem agendador externo (YAGNI). Sendo um
/// Singleton, NUNCA injeta o AegisScoreDbContext (Scoped) direto: abre um escopo por ciclo via
/// <see cref="IServiceScopeFactory"/> e constrói o contexto à mão sob um <see cref="SystemTenantContext"/>
/// — o mesmo padrão do <see cref="DocumentAnalysisWorker"/>. Sem tenant HTTP aqui, o stamping
/// fail-closed exige um tenant resolvido explicitamente a cada iteração.
/// </summary>
public sealed class AegisScoreSnapshotWorker : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AegisScoreSnapshotWorker> _log;

    public AegisScoreSnapshotWorker(IServiceScopeFactory scopes, ILogger<AegisScoreSnapshotWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // Alinha o primeiro disparo à próxima meia-noite UTC; depois, de 24 em 24h.
            await Task.Delay(DelayUntilNextMidnightUtc(), ct);

            using var timer = new PeriodicTimer(Period);
            do
            {
                try
                {
                    await CaptureAllTenantsAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Um ciclo com falha NUNCA derruba o worker; tenta de novo no próximo tick.
                    _log.LogError(ex, "Ciclo de snapshot do Aegis Score falhou; retomará no próximo tick.");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Encerramento do host durante a espera — saída limpa, sem ruído de erro no log.
        }
    }

    /// <summary>Uma passada diária: descobre os tenants e grava/atualiza a foto de hoje para cada um.</summary>
    private async Task CaptureAllTenantsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Tenants distintos com postura registrada. Sem tenant ambiente (SystemTenantContext(null)),
        // a varredura cross-tenant exige IgnoreQueryFilters — senão o filtro fail-closed zera o SELECT.
        List<Guid> tenantIds;
        await using (var probe = new AegisScoreDbContext(options, new SystemTenantContext(null)))
        {
            tenantIds = await probe.TenantControlStates.IgnoreQueryFilters()
                .Select(x => x.TenantId)
                .Distinct()
                .ToListAsync(ct);
        }

        _log.LogInformation(
            "Snapshot do Aegis Score {Date}: {Count} tenant(s) a fotografar.", today, tenantIds.Count);

        foreach (var tenantId in tenantIds)
        {
            try
            {
                await CaptureTenantAsync(options, tenantId, today, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // propaga o shutdown para encerrar o ciclo
            }
            catch (Exception ex)
            {
                // Isola a falha por tenant: os demais seguem sendo fotografados.
                _log.LogError(ex, "Falha ao gravar o snapshot do tenant {TenantId}.", tenantId);
            }
        }
    }

    /// <summary>
    /// Agrega o Aegis Score do tenant e faz o UPSERT idempotente da foto de <paramref name="today"/>.
    /// Opera sob um <see cref="SystemTenantContext"/> do tenant: o query filter restringe a leitura
    /// e o stamping fail-closed carimba o TenantId na escrita.
    /// </summary>
    private async Task CaptureTenantAsync(
        DbContextOptions<AegisScoreDbContext> options, Guid tenantId, DateOnly today, CancellationToken ct)
    {
        await using var db = new AegisScoreDbContext(options, new SystemTenantContext(tenantId));

        // Group By de soma sobre o catálogo: SUM(CurrentScore) / SUM(MaxScorePoints). O cast p/ int?
        // cobre o SUM de zero linhas (NULL no SQL) sem estourar em conjunto vazio.
        var achieved = await db.TenantControlStates.SumAsync(x => (int?)x.CurrentScore, ct) ?? 0;
        var max = await db.TenantControlStates.SumAsync(x => (int?)x.Subcategory!.MaxScorePoints, ct) ?? 0;

        // Upsert idempotente da foto de hoje. O read-then-write cobre o caso normal (1 execução/dia)
        // sem exceção; o catch de DbUpdateException é a rede contra corrida no índice único composto.
        var snapshot = await db.TenantScoreSnapshots
            .FirstOrDefaultAsync(s => s.SnapshotDate == today, ct);

        if (snapshot is null)
        {
            db.TenantScoreSnapshots.Add(new TenantScoreSnapshot
            {
                SnapshotDate = today,
                TotalAchievedScore = achieved,
                TotalMaxScore = max,
                // TenantId é carimbado no SaveChanges (fail-closed) — nunca confiar em valor do cliente.
            });
        }
        else
        {
            snapshot.TotalAchievedScore = achieved;
            snapshot.TotalMaxScore = max;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Rede de segurança: outra execução gravou a foto de hoje entre o nosso SELECT e o INSERT.
            // O índice único (TenantId, SnapshotDate) rejeitou a duplicata — idempotente, segue.
            _log.LogWarning(ex,
                "Foto de {Date} do tenant {TenantId} já existia (índice único) — tratado como idempotente.",
                today, tenantId);
        }
    }

    /// <summary>Tempo até a próxima meia-noite UTC (00:00), para alinhar o primeiro disparo do ciclo.</summary>
    private static TimeSpan DelayUntilNextMidnightUtc()
    {
        var now = DateTime.UtcNow;
        return now.Date.AddDays(1) - now;   // 00:00 UTC do próximo dia
    }
}
