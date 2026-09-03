using System;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Identity;

namespace AegisScore.Infrastructure.Tests.Identity;

/// <summary>
/// [AEGIS-MVP-EVIDENCE-FABRIC-01] Fake de LEITURA da Evidence Fabric de identidade para os testes de projeção do
/// HUD vivo (<c>ControlStateDashboardQuery</c>): devolve uma projeção fixa e NUNCA coleta (o dashboard jamais
/// dispara aquisição do Graph). Sem projeção informada, comporta-se como "conector não configurado" — inerte
/// para os controles que não são de identidade.
/// </summary>
public sealed class FakeIdentityEvidenceService : IIdentityEvidenceService
{
    private readonly IdentityEvidenceProjection _projection;

    public FakeIdentityEvidenceService(IdentityEvidenceProjection? projection = null)
        => _projection = projection
            ?? IdentityEvidenceProjection.Build(IdentityEvidenceConnectorState.NotConfigured, null);

    public Task<IdentityEvidenceAcquisition> CollectAsync(CancellationToken ct = default)
        => throw new NotSupportedException("fake de leitura — o dashboard nunca dispara coleta do Graph");

    public Task<IdentityEvidenceProjection> GetLatestProjectionAsync(CancellationToken ct = default)
        => Task.FromResult(_projection);
}
