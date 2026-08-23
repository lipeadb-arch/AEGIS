using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Health;

/// <summary>
/// [AEGIS-AUD-048 / AEGIS-MVP-OPS-01] Readiness RECORRENTE da API: barato o bastante para ser sondado em
/// laço por um orquestrador (o Render usa <c>/health/ready</c> como probe). Verifica apenas, SOMENTE
/// LEITURA e sem contatar fornecedor externo:
///  (1) que o processo concluiu com sucesso a validação estrutural COMPLETA do arranque
///      (<see cref="StartupReadinessState"/>, marcado após o <see cref="SchemaReadinessGuard"/> completo);
///  (2) que o PostgreSQL continua acessível, por uma verificação LEVE de conectividade
///      (<c>Database.CanConnectAsync</c>).
///
/// Deliberadamente NÃO reexecuta o <see cref="SchemaReadinessGuard"/> completo a cada probe: catálogo NIST,
/// metodologia, regras, mapeamentos, proveniência e fingerprints já foram validados UMA vez no arranque,
/// fail-closed, antes de a API servir. Repetir essa leitura pesada a cada sondagem gera transferência
/// recorrente desnecessária do PostgreSQL externo. Migrar/semear/reparar é do AegisScore.DbMigrator, nunca
/// daqui — este health check não é um monitor contínuo de adulteração do pacote. Também não checa Azure
/// OpenAI, Entra ID, SIEM/EDR nem conector: a ausência dessas dependências externas não pode tornar a API
/// indisponível para a demonstração sintética.
///
/// Fail-safe de exposição: qualquer falha vira <see cref="HealthStatus.Unhealthy"/> com um rótulo GENÉRICO;
/// connection string, nomes sensíveis e stack traces ficam APENAS no log do servidor, nunca na resposta
/// HTTP (ver <see cref="HealthResponseWriter"/>).
/// </summary>
public sealed class AegisReadinessHealthCheck : IHealthCheck
{
    private readonly AegisScoreDbContext _db;
    private readonly StartupReadinessState _startup;
    private readonly ILogger<AegisReadinessHealthCheck> _logger;

    public AegisReadinessHealthCheck(
        AegisScoreDbContext db,
        StartupReadinessState startup,
        ILogger<AegisReadinessHealthCheck> logger)
    {
        _db = db;
        _startup = startup;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // (1) Enquanto o arranque não foi aprovado, NÃO toca o banco (curto-circuito): o guard completo
        //     ainda pode estar rodando ou ter falhado, e nesse caso a API sequer chega a servir tráfego.
        if (!_startup.IsReady)
            return HealthCheckResult.Unhealthy("startup-not-ready");

        // (2) Arranque aprovado: verificação LEVE de conectividade. A validação integral do pacote já
        //     rodou uma vez no arranque — aqui só constatamos que o PostgreSQL continua respondendo.
        try
        {
            if (!await _db.Database.CanConnectAsync(cancellationToken))
            {
                _logger.LogWarning("Readiness: PostgreSQL inacessível.");
                return HealthCheckResult.Unhealthy("database-unavailable");
            }

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            // Banco instável/indisponível no meio da verificação: o detalhe (mensagem, stack) fica só no
            // log; a resposta HTTP recebe um rótulo genérico.
            _logger.LogWarning(ex, "Readiness: falha ao verificar a conectividade do banco.");
            return HealthCheckResult.Unhealthy("dependency-unavailable");
        }
    }
}
