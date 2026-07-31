using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Connectors;

/// <summary>
/// [AEGIS-AUD-020] Autentica o endpoint EXTERNO de ingestão pela CHAVE do conector — nunca por JWT/X-Tenant.
/// É o BOUNDARY cross-tenant controlado: faz o ÚNICO lookup <c>IgnoreQueryFilters</c> permitido fora da
/// camada de identidade, valida a chave, confirma habilitado + genérico de push, e devolve o tenant
/// PROPRIETÁRIO. Conector inexistente e chave inválida são INDISTINGUÍVEIS (mesmo <c>null</c>, tempo
/// ~constante via dummy hash) — o endpoint externo não vira oráculo de existência de conectores/tenants.
/// </summary>
public sealed class ConnectorIngestionAuthenticator : IConnectorIngestionAuthenticator
{
    // Hash-alvo de custo constante para o caso "conector inexistente / sem chave": a verificação roda
    // SEMPRE, igualando o tempo ao caso "existe, chave errada" — não vaza por timing se o connectorId existe.
    private static readonly string DummyHash = IngestionKey.Hash("aegis-ingestion-timing-guard");

    private readonly AegisScoreDbContext _db;

    public ConnectorIngestionAuthenticator(AegisScoreDbContext db) => _db = db;

    public async Task<AuthenticatedConnector?> AuthenticateAsync(
        Guid connectorId, string presentedKey, CancellationToken ct)
    {
        // O ÚNICO lookup cross-tenant permitido: sem tenant ambiente (endpoint anônimo), o conector é
        // localizado por id atravessando o query filter. O tenant SÓ pode sair daqui — nunca do chamador.
        var config = connectorId == Guid.Empty
            ? null
            : await _db.Connectors.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == connectorId, ct);

        // Verifica SEMPRE contra ALGUM hash (o dummy quando o conector não existe ou não tem chave) para não
        // vazar por timing se o connectorId existe. Só um conector COM chave e chave CORRETA segue adiante.
        var storedHash = config?.IngestionKeyHash;
        var keyOk = IngestionKey.Verify(presentedKey ?? "", storedHash ?? DummyHash);
        if (config is null || string.IsNullOrEmpty(storedHash) || !keyOk)
            return null;

        // Habilitado E genérico de PUSH (Generic/Siem ou Generic/Edr). Recusa silenciosa (mesmo null): não
        // distingue "chave errada" de "desabilitado/incompatível" para o chamador externo.
        if (!config.Enabled
            || config.Provider != ConnectorProvider.Generic
            || (config.Capability != ConnectorCapability.Siem && config.Capability != ConnectorCapability.Edr))
            return null;

        return new AuthenticatedConnector(config.Id, config.TenantId, config.Capability);
    }
}
