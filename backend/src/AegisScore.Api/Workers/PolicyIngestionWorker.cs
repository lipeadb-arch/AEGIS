using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Documents;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Workers;

/// <summary>
/// [AEGIS-AUD-050] GOVERN → ingestão AGNÓSTICA de políticas sobre fila DURÁVEL. Fecha o Provider Pattern:
/// descobre os tenants com integração de documentos, RESOLVE a estratégia da fonte via
/// <see cref="IDocumentIntegrationFactory"/> e executa <c>FetchPoliciesAsync</c> — sem conhecer a API do
/// fornecedor. Cada documento puxado vira um <c>GovernanceDocument</c> (Source = Integracao) em
/// <c>AnalysisStatus.Queued</c>, o que já É a entrada na fila durável de análise (o
/// <see cref="DocumentAnalysisWorker"/> o adquire de lá).
///
/// DUAS engrenagens, sem canal em memória:
/// <list type="bullet">
/// <item>o ciclo PERIÓDICO (<c>PeriodicTimer</c>) apenas ENFILEIRA — persiste uma
/// <see cref="PolicySyncRequest"/> por tenant elegível (idempotente). O timer é agendador, jamais transporte;</item>
/// <item>o CONSUMIDOR sonda a fila durável (<see cref="IPolicySyncQueue"/>), ADQUIRE cada pedido com lease
/// atômico e o processa. O gatilho sob demanda (<c>POST /governance/documents/sync</c>) alimenta a MESMA fila.</item>
/// </list>
/// Sobrevive a reinício, encerramento no meio e múltiplas réplicas; entrega at-least-once com retry, limite
/// de tentativas e recuperação de lease vencido. A idempotência da ingestão vem do índice único
/// (TenantId, Sha256) de <c>GovernanceDocument</c>.
/// </summary>
public sealed class PolicyIngestionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IPolicySyncQueue _queue;
    private readonly TimeProvider _clock;
    private readonly ILogger<PolicyIngestionWorker> _log;
    private readonly TimeSpan _periodicInterval;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _heartbeatInterval;
    private readonly int _maxAttempts;

    public PolicyIngestionWorker(
        IServiceScopeFactory scopes, IPolicySyncQueue queue, TimeProvider clock,
        IOptions<PolicySyncQueueOptions> options, ILogger<PolicyIngestionWorker> log)
    {
        _scopes = scopes;
        _queue = queue;
        _clock = clock;
        _log = log;
        var opt = options.Value;
        _periodicInterval = TimeSpan.FromMinutes(Math.Max(1, opt.PeriodicIntervalMinutes));
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, opt.PollSeconds));
        _heartbeatInterval = TimeSpan.FromSeconds(Math.Max(1, opt.EffectiveHeartbeatSeconds));
        _maxAttempts = Math.Max(1, opt.MaxAttempts);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Duas engrenagens em paralelo: o timer PERIÓDICO só ENFILEIRA (persiste pedidos); o CONSUMIDOR sonda
        // a fila durável, adquire com lease e processa. O timer nunca é o transporte nem a memória do pedido.
        await Task.WhenAll(
            RunPeriodicEnqueueAsync(ct),
            RunConsumerAsync(ct));
    }

    /// <summary>Ciclo periódico: enfileira (persiste) um pedido de sync por tenant elegível — no boot e a cada intervalo.</summary>
    private async Task RunPeriodicEnqueueAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_periodicInterval);
            do
            {
                try
                {
                    await EnqueueEligibleTenantsAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Um ciclo com falha NUNCA derruba o worker; tenta de novo no próximo tick.
                    _log.LogError(ex, "Enfileiramento periódico de sync de políticas falhou; retomará no próximo tick.");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Encerramento do host durante a espera — saída limpa, sem ruído de erro no log.
        }
    }

    /// <summary>Consumidor: sonda a fila durável, adquire cada pedido com lease e o processa até esvaziar.</summary>
    private async Task RunConsumerAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_pollInterval);
            do
            {
                try
                {
                    await DrainAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;   // desligamento gracioso durante o dreno
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Consumo de sync de políticas falhou; retomará no próximo tick.");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Encerramento do host durante a espera — saída limpa.
        }
    }

    /// <summary>
    /// Enfileira um pedido de sync por tenant com integração de documentos habilitada. A varredura é
    /// cross-tenant (<c>SystemTenantContext(null)</c> + IgnoreQueryFilters); <see cref="IPolicySyncQueue.EnqueueAsync"/>
    /// é idempotente (um único pedido ATIVO por tenant), então re-enfileirar a cada tick não acumula pedidos.
    /// </summary>
    private async Task EnqueueEligibleTenantsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();

        List<Guid> tenantIds;
        await using (var probe = new AegisScoreDbContext(options, new SystemTenantContext(null)))
        {
            tenantIds = await probe.Connectors.IgnoreQueryFilters()
                .Where(c => c.Capability == ConnectorCapability.PolicyDocuments && c.Enabled)
                .Select(c => c.TenantId)
                .Distinct()
                .ToListAsync(ct);
        }

        foreach (var tenantId in tenantIds)
            await _queue.EnqueueAsync(tenantId, ct);
    }

    /// <summary>Adquire e processa pedidos em sequência até a fila esvaziar; então aguarda o próximo tick.</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var lease = await _queue.TryClaimNextAsync(ct);
            if (lease is null) return;
            await ProcessLeasedAsync(lease, ct);
        }
    }

    private async Task ProcessLeasedAsync(PolicySyncLease lease, CancellationToken ct)
    {
        // Poison reclamado além do limite (crash repetido antes do catch): encerra terminal sem reprocessar.
        if (lease.Attempts > _maxAttempts)
        {
            await _queue.FailAsync(lease.RequestId, lease.LeaseId, "AttemptsExhausted", CancellationToken.None);
            _log.LogWarning(
                "Sync do tenant {Tenant} excedeu o limite de tentativas ({Max}); marcado Failed.",
                lease.TenantId, _maxAttempts);
            return;
        }

        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var options = sp.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        var factory = sp.GetRequiredService<IDocumentIntegrationFactory>();

        // BATIMENTO DE LEASE: um fetch/ingestão de conector lento não pode deixar outra réplica adquirir o
        // mesmo pedido. O heartbeat renova o lease; se ele for perdido, `leaseCts` cancela e o trabalho aborta.
        using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var heartbeat = LeaseHeartbeat.Start(
            c => _queue.RenewAsync(lease.RequestId, lease.LeaseId, c),
            _heartbeatInterval, _clock, leaseCts, _log);
        var workCt = leaseCts.Token;

        try
        {
            // Integrações de documentos DESTE tenant. SystemTenantContext(lease.TenantId): leitura
            // query-filtered ao tenant dono — a varredura cross-tenant ficou só na aquisição.
            List<TenantIntegration> integrations;
            await using (var probe = new AegisScoreDbContext(options, new SystemTenantContext(lease.TenantId)))
            {
                integrations = await probe.Connectors
                    .Where(c => c.Capability == ConnectorCapability.PolicyDocuments && c.Enabled)
                    .Select(c => new TenantIntegration(c.TenantId, c.Provider))
                    .ToListAsync(workCt);
            }

            await ProcessIntegrationsAsync(sp, options, factory, integrations, workCt);

            // Confirmação com CancellationToken.None (o trabalho acabou); guardada pelo lease — se ele foi
            // perdido no fio final, completed=false DETECTA a perda.
            var completed = await _queue.CompleteAsync(lease.RequestId, lease.LeaseId, CancellationToken.None);
            if (!completed)
                _log.LogWarning(
                    "Sync do tenant {Tenant}: lease não era mais o vigente ao confirmar; outra réplica assumiu.",
                    lease.TenantId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Desligamento: devolve à fila SEM custar tentativa; o próximo boot / outra réplica retoma.
            await _queue.ReleaseAsync(lease.RequestId, lease.LeaseId, CancellationToken.None);
            _log.LogInformation("Sync do tenant {Tenant} devolvido à fila pelo desligamento do serviço.", lease.TenantId);
            throw;
        }
        catch (OperationCanceledException) when (leaseCts.IsCancellationRequested)
        {
            // Lease perdido no meio: abandona silenciosamente — a outra réplica é a dona agora.
            _log.LogWarning(
                "Sync do tenant {Tenant}: lease perdido durante o processamento; abandonando (outra réplica assumiu).",
                lease.TenantId);
        }
        catch (Exception ex)
        {
            var category = ex.GetType().Name;   // categoria SANITIZADA, nunca a mensagem bruta (AEGIS-AUD-054)
            if (lease.Attempts >= _maxAttempts)
            {
                await _queue.FailAsync(lease.RequestId, lease.LeaseId, category, CancellationToken.None);
                _log.LogWarning(ex,
                    "Sync do tenant {Tenant} falhou na tentativa {Attempt}/{Max}; marcado Failed (terminal).",
                    lease.TenantId, lease.Attempts, _maxAttempts);
            }
            else
            {
                await _queue.ScheduleRetryAsync(lease.RequestId, lease.LeaseId, category, CancellationToken.None);
                _log.LogWarning(ex,
                    "Sync do tenant {Tenant} falhou na tentativa {Attempt}/{Max}; reagendado para retry.",
                    lease.TenantId, lease.Attempts, _maxAttempts);
            }
        }
    }

    /// <summary>
    /// Resolve o provedor de cada integração pela fábrica e o sincroniza, isolando a falha por integração.
    /// Um provedor não implantado (fábrica devolve null) é registrado e ignorado. Uma falha de integração
    /// PROPAGA (para o pedido inteiro entrar em retry), exceto o cancelamento, que sobe como desligamento.
    /// </summary>
    private async Task ProcessIntegrationsAsync(
        IServiceProvider sp, DbContextOptions<AegisScoreDbContext> options,
        IDocumentIntegrationFactory factory, IReadOnlyList<TenantIntegration> integrations, CancellationToken ct)
    {
        foreach (var integration in integrations)
        {
            var provider = factory.GetProvider(integration.Provider);
            if (provider is null)
            {
                _log.LogWarning(
                    "Ingestão de políticas: sem provedor para {Provider} (tenant {Tenant}); ignorando.",
                    integration.Provider, integration.TenantId);
                continue;
            }

            await SyncTenantAsync(sp, options, integration.TenantId, provider, ct);
        }
    }

    /// <summary>
    /// Puxa as políticas do provedor (agnóstico) e as materializa no hub: dedupe por SHA-256, cria o
    /// <c>GovernanceDocument</c> (Integracao) em <c>Queued</c> — que já É a entrada na fila durável de análise —
    /// e guarda o binário. Opera sob um <see cref="SystemTenantContext"/> do tenant: query filter na leitura,
    /// stamping fail-closed na escrita. A idempotência sob concorrência é imposta pelo índice único
    /// (TenantId, Sha256): o <c>AnyAsync</c> é fast-path e o catch de <see cref="DbUpdateException"/> fecha a corrida.
    /// </summary>
    private async Task SyncTenantAsync(
        IServiceProvider sp, DbContextOptions<AegisScoreDbContext> options,
        Guid tenantId, IDocumentIntegrationProvider provider, CancellationToken ct)
    {
        // O CORAÇÃO do Provider Pattern: o worker só conhece a PORTA; a fonte real é intercambiável.
        var policies = await provider.FetchPoliciesAsync(tenantId, ct);

        var storage = sp.GetRequiredService<IDocumentStorage>();
        await using var db = new AegisScoreDbContext(options, new SystemTenantContext(tenantId));

        var ingested = 0;
        foreach (var policy in policies)
        {
            ct.ThrowIfCancellationRequested();

            var sha = Convert.ToHexString(SHA256.HashData(policy.Content)).ToLowerInvariant();
            // Idempotência: sync repetido não reingere o mesmo documento (o query filter escopa ao tenant).
            // Fast-path — a corrida entre ciclo × on-demand é fechada pelo índice único no SaveChanges.
            if (await db.GovernanceDocuments.AnyAsync(d => d.Sha256 == sha, ct))
                continue;

            var doc = new GovernanceDocument
            {
                // Sem TenantId — carimbado no SaveChanges (fail-closed).
                Title = policy.Title,
                Type = policy.Type,
                Source = DocumentSource.Integracao,
                SourceReference = policy.SourceReference,
                FileName = policy.FileName,
                ContentType = policy.ContentType,
                FileSizeBytes = policy.Content.LongLength,
                Sha256 = sha,
                AnalysisStatus = AiAnalysisStatus.Queued,   // entra JÁ na fila durável de análise
                AnalysisQueuedAt = DateTimeOffset.UtcNow,
            };
            db.GovernanceDocuments.Add(doc);
            try
            {
                await db.SaveChangesAsync(ct);   // carimba TenantId e materializa doc.Id
            }
            catch (DbUpdateException ex)
            {
                // Rede de segurança: outra passada (ciclo × on-demand) ingeriu o mesmo hash entre o nosso
                // AnyAsync e este INSERT. O índice único (TenantId, Sha256) rejeitou a duplicata — idempotente.
                // Descarta a entidade pendente (o db é REUSADO no loop) para não reprocessá-la e segue.
                db.Entry(doc).State = EntityState.Detached;
                _log.LogWarning(ex,
                    "Política com hash {Sha} do tenant {Tenant} já ingerida (índice único) — tratada como idempotente.",
                    sha, tenantId);
                continue;
            }

            await using (var buffer = new MemoryStream(policy.Content, writable: false))
                doc.StorageUri = await storage.SaveAsync(doc.TenantId, doc.Id, policy.FileName, buffer, ct);
            await db.SaveChangesAsync(ct);   // grava o StorageUri → o documento fica elegível à aquisição

            ingested++;
        }

        if (ingested > 0)
            _log.LogInformation(
                "Ingestão de políticas: {Count} novo(s) documento(s) do tenant {Tenant} via {Provider}.",
                ingested, tenantId, provider.Provider);
    }

    /// <summary>Projeção mínima da integração de documentos de um tenant (par tenant × fornecedor configurado).</summary>
    private readonly record struct TenantIntegration(Guid TenantId, ConnectorProvider Provider);
}
