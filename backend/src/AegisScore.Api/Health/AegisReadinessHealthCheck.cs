using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Health;

/// <summary>
/// [AEGIS-AUD-048] Readiness real da API: confirma que a aplicação está APTA a servir o AEGIS.
///
/// Verifica, SOMENTE LEITURA e sem contatar fornecedor externo:
///  (1) conexão com o PostgreSQL;
///  (2) as migrations esperadas nos dois contextos e a integridade do catálogo/regras — reusando o
///      <see cref="SchemaReadinessGuard"/>, o MESMO mecanismo de prontidão que o boot já emprega.
///
/// Deliberadamente NÃO checa Azure OpenAI, Entra ID, SIEM/EDR ou qualquer conector: a ausência dessas
/// dependências externas não pode tornar a API indisponível para a demonstração sintética. Nunca altera
/// banco, migra, semeia nem repara — apenas constata.
///
/// Fail-safe de exposição: qualquer falha vira <see cref="HealthStatus.Unhealthy"/> com um rótulo GENÉRICO;
/// connection string, nomes sensíveis, stack traces e a lista de pendências ficam APENAS no log do
/// servidor, nunca na resposta HTTP (ver <see cref="HealthResponseWriter"/>).
/// </summary>
public sealed class AegisReadinessHealthCheck : IHealthCheck
{
    private readonly AegisScoreDbContext _db;
    private readonly IServiceProvider _services;
    private readonly ILogger<AegisReadinessHealthCheck> _logger;

    public AegisReadinessHealthCheck(
        AegisScoreDbContext db,
        IServiceProvider services,
        ILogger<AegisReadinessHealthCheck> logger)
    {
        _db = db;
        _services = services;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _db.Database.CanConnectAsync(cancellationToken))
            {
                _logger.LogWarning("Readiness: PostgreSQL inacessível.");
                return HealthCheckResult.Unhealthy("database-unavailable");
            }

            // Ausente apenas com key ring efêmero (configuração de teste) — resolvido opcionalmente,
            // como no boot (Program.cs).
            var keyRing = _services.GetService(typeof(DataProtectionKeyDbContext)) as DataProtectionKeyDbContext;

            var result = await SchemaReadinessGuard.CheckAsync(_db, keyRing, cancellationToken);
            if (!result.IsReady)
            {
                // As pendências vão para o LOG do servidor — NUNCA para a resposta HTTP.
                _logger.LogWarning("Readiness: schema não preparado. Pendências: {Problems}", result.Describe());
                return HealthCheckResult.Unhealthy("schema-not-ready");
            }

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            // Banco instável/indisponível no meio da verificação: o detalhe (mensagem, stack) fica só no
            // log; a resposta HTTP recebe um rótulo genérico.
            _logger.LogWarning(ex, "Readiness: falha ao verificar a prontidão do banco.");
            return HealthCheckResult.Unhealthy("dependency-unavailable");
        }
    }
}
