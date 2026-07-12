using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Workers;

/// <summary>
/// GOVERN → ingestão AGNÓSTICA de políticas. Fecha o Provider Pattern: descobre os tenants com uma
/// integração de documentos habilitada, RESOLVE a estratégia da fonte via
/// <see cref="IDocumentIntegrationFactory"/> e executa <c>FetchPoliciesAsync</c> — sem NUNCA conhecer a
/// API do fornecedor (SharePoint, Google…). Cada documento puxado vira um <c>GovernanceDocument</c>
/// (Source = Integracao), tem o binário guardado e é enfileirado para a leitura da IA — daí em diante é o
/// MESMO pipeline do upload manual (<see cref="DocumentAnalysisWorker"/> → ledger, com teto documental de 50%).
///
/// DOIS disparos, UM executor idempotente:
/// <list type="bullet">
/// <item>ciclo PERIÓDICO (PeriodicTimer) — varre todos os tenants de tempos em tempos;</item>
/// <item>gatilho SOB DEMANDA (<see cref="IPolicySyncTrigger"/>) — o executivo aciona <c>POST /governance/documents/sync</c>
/// e o worker sincroniza aquele tenant AGORA, sem esperar o timer.</item>
/// </list>
/// Ambos convergem para <see cref="SyncTenantAsync"/>. O dedupe de documento é um read-then-write, mas a
/// idempotência é imposta pelo BANCO: o índice ÚNICO (TenantId, Sha256) rejeita a duplicata, então dois
/// syncs concorrentes do MESMO tenant não geram documentos repetidos — a 2ª inserção vira no-op (o
/// <see cref="DbUpdateException"/> é tratado como idempotente). Dispensa serialização em memória.
///
/// Sendo Singleton, abre um escopo por operação e constrói o DbContext à mão sob um
/// <see cref="SystemTenantContext"/> — mesmo padrão dos demais workers (sem tenant HTTP, o stamping
/// fail-closed exige tenant explícito por iteração).
/// </summary>
public sealed class PolicyIngestionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IPolicySyncTrigger _trigger;
    private readonly ILogger<PolicyIngestionWorker> _log;
    private readonly TimeSpan _period;

    public PolicyIngestionWorker(
        IServiceScopeFactory scopes, IPolicySyncTrigger trigger, IConfiguration config,
        ILogger<PolicyIngestionWorker> log)
    {
        _scopes = scopes;
        _trigger = trigger;
        _log = log;
        var minutes = int.TryParse(config["PolicyIngestion:IntervalMinutes"], out var m) ? m : 60;
        _period = TimeSpan.FromMinutes(Math.Max(1, minutes));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Duas fontes de disparo rodando em paralelo; a idempotência da ingestão vem do índice único no banco.
        await Task.WhenAll(
            RunPeriodicAsync(ct),
            RunOnDemandAsync(ct));
    }

    /// <summary>Ciclo periódico: um sync no boot (popula a demo) e depois a cada intervalo configurado.</summary>
    private async Task RunPeriodicAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_period);
            do
            {
                try
                {
                    await SyncAllTenantsAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Um ciclo com falha NUNCA derruba o worker; tenta de novo no próximo tick.
                    _log.LogError(ex, "Ciclo de ingestão de políticas falhou; retomará no próximo tick.");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Encerramento do host durante a espera — saída limpa, sem ruído de erro no log.
        }
    }

    /// <summary>Gatilho sob demanda: consome o canal e sincroniza o tenant pedido pelo endpoint /sync.</summary>
    private async Task RunOnDemandAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var tenantId in _trigger.DequeueAllAsync(ct))
            {
                try
                {
                    await SyncTenantOnDemandAsync(tenantId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Isola a falha de um pedido: o consumidor segue vivo para os próximos.
                    _log.LogError(ex, "Sync sob demanda do tenant {Tenant} falhou.", tenantId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento do host — o canal foi cancelado. Saída limpa.
        }
    }

    /// <summary>
    /// Passada PERIÓDICA: enumera (cross-tenant) as integrações de documentos habilitadas e processa cada
    /// uma. Sem tenant ambiente (<c>SystemTenantContext(null)</c>), a varredura usa IgnoreQueryFilters —
    /// senão o filtro fail-closed zera o SELECT.
    /// </summary>
    private async Task SyncAllTenantsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var options = sp.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        var factory = sp.GetRequiredService<IDocumentIntegrationFactory>();

        List<TenantIntegration> integrations;
        await using (var probe = new AegisScoreDbContext(options, new SystemTenantContext(null)))
        {
            integrations = await probe.Connectors.IgnoreQueryFilters()
                .Where(c => c.Capability == ConnectorCapability.PolicyDocuments && c.Enabled)
                .Select(c => new TenantIntegration(c.TenantId, c.Provider))
                .ToListAsync(ct);
        }

        await ProcessIntegrationsAsync(sp, options, factory, integrations, ct);
    }

    /// <summary>
    /// Passada SOB DEMANDA para UM tenant (gatilho do endpoint /sync): processa só as integrações daquele
    /// tenant. Mesmo caminho da varredura periódica — um clique repetido pode correr em paralelo com o
    /// ciclo do timer sobre o mesmo tenant, mas o índice único (TenantId, Sha256) mantém a ingestão idempotente.
    /// </summary>
    private async Task SyncTenantOnDemandAsync(Guid tenantId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var options = sp.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        var factory = sp.GetRequiredService<IDocumentIntegrationFactory>();

        List<TenantIntegration> integrations;
        await using (var probe = new AegisScoreDbContext(options, new SystemTenantContext(null)))
        {
            integrations = await probe.Connectors.IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId && c.Capability == ConnectorCapability.PolicyDocuments && c.Enabled)
                .Select(c => new TenantIntegration(c.TenantId, c.Provider))
                .ToListAsync(ct);
        }

        if (integrations.Count == 0)
        {
            _log.LogInformation(
                "Sync sob demanda ignorado: tenant {Tenant} não tem integração de documentos habilitada.", tenantId);
            return;
        }

        await ProcessIntegrationsAsync(sp, options, factory, integrations, ct);
    }

    /// <summary>
    /// Resolve o provedor de cada integração pela fábrica e o sincroniza sob o gate de concorrência,
    /// isolando a falha por tenant (os demais seguem). Um provedor não implantado (fábrica devolve null)
    /// é registrado e ignorado — não quebra os outros.
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

            try
            {
                await SyncTenantAsync(sp, options, integration.TenantId, provider, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // propaga o shutdown para encerrar o ciclo
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Falha ao sincronizar políticas do tenant {Tenant} via {Provider}.",
                    integration.TenantId, integration.Provider);
            }
        }
    }

    /// <summary>
    /// Puxa as políticas do provedor (agnóstico) e as materializa no hub: dedupe por SHA-256, cria o
    /// <c>GovernanceDocument</c> (Integracao), guarda o binário e enfileira a leitura da IA. Opera sob um
    /// <see cref="SystemTenantContext"/> do tenant — query filter na leitura, stamping fail-closed na escrita.
    /// A idempotência sob concorrência é imposta pelo índice único (TenantId, Sha256): o AnyAsync é o
    /// fast-path e o catch de <see cref="DbUpdateException"/> fecha a corrida (a 2ª passada vira no-op).
    /// </summary>
    private async Task SyncTenantAsync(
        IServiceProvider sp, DbContextOptions<AegisScoreDbContext> options,
        Guid tenantId, IDocumentIntegrationProvider provider, CancellationToken ct)
    {
        // O CORAÇÃO do Provider Pattern: o worker só conhece a PORTA; a fonte real é intercambiável.
        var policies = await provider.FetchPoliciesAsync(tenantId, ct);

        var storage = sp.GetRequiredService<IDocumentStorage>();
        var queue = sp.GetRequiredService<IDocumentAnalysisQueue>();
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
                AnalysisStatus = AiAnalysisStatus.Queued,
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
            await db.SaveChangesAsync(ct);

            await queue.EnqueueAsync(doc.Id, ct);   // o DocumentAnalysisWorker cuida da análise → ledger
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
