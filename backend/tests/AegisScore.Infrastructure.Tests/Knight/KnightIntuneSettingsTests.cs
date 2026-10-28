using System;
using System.Linq;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-GAPS-01] Configuração de conformidade do Intune (<c>deviceManagement.settings.secureByDefault</c>)
/// pelo percurso inteiro: resposta do Graph → coletor → ADM → releitura → AK-INT-001. Antes, a capacidade terminava como
/// COLETADA mesmo quando o objeto ou a propriedade não vinham, e o controle ficava "a fonte não informou" sem dizer o porquê.
/// Agora: true e false são lidos; objeto ausente, propriedade ausente e tipo incompatível são falhas de contrato distintas,
/// cada uma com o próprio diagnóstico — nunca convertidas em false, conformidade ou sucesso. AK-INT-002 não é afetado.
/// </summary>
public sealed class KnightIntuneSettingsTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0505-0505-0505-0000000000a1");

    private readonly SqliteConnection _connection;
    private readonly ITestOutputHelper _output;

    public KnightIntuneSettingsTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Theory]
    [InlineData("""{"settings":{"secureByDefault":true,"deviceComplianceCheckinThresholdDays":30}}""", KnightIndicatorStatus.Passed)]
    [InlineData("""{"settings":{"secureByDefault":false,"deviceComplianceCheckinThresholdDays":30}}""", KnightIndicatorStatus.Exposed)]
    public async Task ValorBooleano_EhLidoEAvaliado(string body, KnightIndicatorStatus expected)
    {
        var (run, scenario) = await RunAsync(body);

        run.Capabilities.Single(c => c.Capability == KnightCapability.IntuneServiceSettings).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        run.Indicators.Single(i => i.IndicatorId == "AK-INT-001").Status.Should().Be(expected);
        run.Indicators.Single(i => i.IndicatorId == "AK-INT-002").Status.Should().NotBe(KnightIndicatorStatus.NotEvaluated);
        scenario.Requests.Select(r => r.RequestUri!.AbsoluteUri)
            .Should().Contain(u => u.EndsWith("/v1.0/deviceManagement", StringComparison.Ordinal),
                "a leitura usa o GET documentado do deviceManagement, que devolve settings sem depender de $select");
    }

    [Theory]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000000"}""", "settings ausente")]
    [InlineData("""{"settings":null}""", "settings ausente")]
    [InlineData("""{"settings":"texto"}""", "settings com tipo incompatível")]
    [InlineData("""{"settings":{"deviceComplianceCheckinThresholdDays":30}}""", "secureByDefault ausente")]
    [InlineData("""{"settings":{"secureByDefault":null}}""", "secureByDefault ausente")]
    [InlineData("""{"settings":{"secureByDefault":"true"}}""", "secureByDefault com tipo incompatível")]
    public async Task DadoAusenteOuIncompativel_NaoEhColetado_ComDiagnosticoProprio(string body, string expected)
    {
        var (run, _) = await RunAsync(body);

        var cap = run.Capabilities.Single(c => c.Capability == KnightCapability.IntuneServiceSettings);
        cap.Outcome.Should().Be(KnightCapabilityOutcome.Error, "ausência nunca é false, conformidade nem sucesso");
        cap.Diagnostic!.Kind.Should().Be(KnightFailureKind.Contract);
        cap.Diagnostic.Endpoint.Should().Be("/v1.0/deviceManagement");
        cap.Detail.Should().Contain(expected);

        var ind = run.Indicators.Single(i => i.IndicatorId == "AK-INT-001");
        ind.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        ind.NotEvaluatedReason.Should().Contain(expected);
        run.Indicators.Single(i => i.IndicatorId == "AK-INT-002").Status.Should().NotBe(KnightIndicatorStatus.NotEvaluated,
            "a restrição de registro é outra capacidade e continua avaliada");
        run.SourceState.Should().Be(KnightSourceState.PartialCollection);
    }

    // ---- Infra ---------------------------------------------------------------------------------------------

    private async Task<(KnightAssessment Run, M365ServicesScenario Scenario)> RunAsync(string body)
    {
        await SeedAsync();
        await using var db = NewContext(TenantA);
        var scenario = new M365ServicesScenario(M365ServicesScenario.Variant.Compliant) { IntuneSettingsBody = body };
        var run = await KnightM365ServicesFlowTests.ServiceFor(db, TenantA, scenario).RunAssessmentAsync(KnightSourceType.MicrosoftIntune);
        foreach (var c in run.Capabilities) _output.WriteLine($"cap {c.Capability} {c.Outcome} {c.Detail}");
        foreach (var i in run.Indicators) _output.WriteLine($"{i.IndicatorId} {i.Status} {i.NotEvaluatedReason ?? i.Evidence}");
        return (run, scenario);
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private async Task SeedAsync()
    {
        await using (var db = NewContext(null))
        {
            if (await db.Tenants.AnyAsync(t => t.Id == TenantA)) return;
            db.Tenants.Add(new Tenant { Id = TenantA, Name = "Cliente Demo", Slug = $"t-{TenantA:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using var dbt = NewContext(TenantA);
        dbt.Connectors.Add(new ConnectorConfig
        {
            TenantId = TenantA, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
            DisplayName = "Microsoft 365", Enabled = true, EncryptedSettings = "cifrado",
        });
        await dbt.SaveChangesAsync();
    }
}
