using System.Threading.Tasks;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Integration;

/// <summary>
/// [AEGIS-MVP-PRE-ADM-01] Ciclo de vida do <see cref="AegisApiHarness"/> por CLASSE de teste.
///
/// Subir o host da API e preparar um banco descartável (migrations + seed do catálogo NIST + verificação)
/// custa caro; repetir isso a cada caso multiplicaria o tempo do job de CI sem provar nada além. O isolamento
/// entre os casos não vem do banco: vem de cada teste semear o PRÓPRIO ambiente (tenant e identidades).
///
/// <see cref="Api"/> é <c>null</c> quando <c>AEGIS_TEST_PG</c> não está definido — na máquina de
/// desenvolvimento, onde não há PostgreSQL acessível. Os testes então saem cedo, de forma declarada. O gate
/// anti-falso-verde do CI (<c>AEGIS_PG_GATE_REQUIRED</c>) continua valendo dentro do <c>PostgresProbe</c>: no
/// job de PostgreSQL, a ausência da variável FALHA em vez de virar no-op silencioso.
/// </summary>
public sealed class AegisApiFixture : IAsyncLifetime
{
    internal AegisApiHarness? Api { get; private set; }

    public async Task InitializeAsync() => Api = await AegisApiHarness.TryCreateAsync();

    public async Task DisposeAsync()
    {
        if (Api is not null) await Api.DisposeAsync();
    }
}
