using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Integration;

/// <summary>
/// [AEGIS-KNIGHT-DURABLE-01] Contratos de leitura da ÚLTIMA avaliação do KNIGHT atravessando o PIPELINE HTTP
/// REAL (JWT real, <c>TenantConsistencyMiddleware</c>, roteamento, serialização) sobre PostgreSQL real.
///
///   • <c>GET /api/v1/knight/assessments/latest</c> mantém o formato público anterior: o corpo É um
///     <c>KnightAssessmentDto</c> (sem envelope), e 204 quando não há resultado concluído;
///   • <c>GET /api/v1/knight/assessments/latest-state</c> é a leitura COMPOSTA: o último resultado concluído e,
///     à parte, a tentativa não concluída que o sucede — sempre 200, com os dois campos explícitos;
///   • as duas rotas saem da MESMA autoridade de aplicação e respeitam o isolamento por tenant.
///
/// As execuções não concluídas são semeadas diretamente, na forma das encontradas no ambiente real
/// (<c>Running</c>, sem <c>CompletedAt</c>). Nenhuma é reparada. A fonte das avaliações concluídas é a
/// DEMONSTRAÇÃO (sintética): nenhuma chamada externa.
/// </summary>
public sealed class KnightLatestHttpTests : IClassFixture<AegisApiFixture>
{
    private const string Latest = "/api/v1/knight/assessments/latest";
    private const string LatestState = "/api/v1/knight/assessments/latest-state";

    private readonly AegisApiHarness? _api;
    private readonly ITestOutputHelper _output;

    public KnightLatestHttpTests(AegisApiFixture fixture, ITestOutputHelper output)
    {
        _api = fixture.Api;
        _output = output;
    }

    [Fact]
    public async Task SemExecucoes_LatestResponde204_ELatestStateDeclaraAsDuasAusencias()
    {
        if (_api is null) return;   // AEGIS_TEST_PG ausente — pulado honestamente
        var t = await _api.SeedTenantAsync("Sem execuções");
        using var client = _api.As(t.Analyst);

        using (var r = await client.GetAsync(Latest))
            r.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var doc = await GetJsonAsync(client, LatestState);
        doc.RootElement.GetProperty("assessment").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("unfinishedAttempt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Latest_PreservaOFormatoAnterior_EAComposta_SeparaResultadoDeTentativa()
    {
        if (_api is null) return;
        var t = await _api.SeedTenantAsync("Composta");

        Guid concluida;
        using (var manager = _api.As(t.Manager))
        using (var post = await manager.PostAsync("/api/v1/knight/assessments/demo", null))
        {
            post.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
            concluida = body.RootElement.GetProperty("id").GetGuid();
        }

        var orfa = await SeedUnfinishedAsync(t.Id, DateTimeOffset.UtcNow.AddMinutes(5));

        using var client = _api.As(t.Analyst);

        // Formato ANTERIOR: o próprio KnightAssessmentDto no topo — sem envelope.
        using (var doc = await GetJsonAsync(client, Latest))
        {
            var root = doc.RootElement;
            root.TryGetProperty("assessment", out _).Should().BeFalse("GET /latest não ganhou envelope");
            root.TryGetProperty("unfinishedAttempt", out _).Should().BeFalse();
            root.GetProperty("id").GetGuid().Should().Be(concluida, "o resultado é o último CONCLUÍDO");
            root.GetProperty("status").GetString().Should().Be("Completed");
            root.GetProperty("indicators").GetArrayLength().Should().BeGreaterThan(0);
            root.GetProperty("counts").ValueKind.Should().Be(JsonValueKind.Object);
        }

        // Leitura COMPOSTA: as duas coisas, separadas.
        using (var doc = await GetJsonAsync(client, LatestState))
        {
            var a = doc.RootElement.GetProperty("assessment");
            a.GetProperty("id").GetGuid().Should().Be(concluida);
            a.GetProperty("status").GetString().Should().Be("Completed");

            var u = doc.RootElement.GetProperty("unfinishedAttempt");
            u.GetProperty("id").GetGuid().Should().Be(orfa);
            u.GetProperty("status").GetString().Should().Be("Running");
            u.GetProperty("sourceType").GetString().Should().Be("MicrosoftEntraId");
            u.GetProperty("mode").GetString().Should().Be("Live");
            u.TryGetProperty("indicators", out _).Should().BeFalse("a tentativa é cabeçalho, não resultado");
        }

        // O acesso por Id continua alcançando a execução não finalizada, com o estado real.
        using (var doc = await GetJsonAsync(client, $"/api/v1/knight/assessments/{orfa}"))
        {
            doc.RootElement.GetProperty("status").GetString().Should().Be("Running");
            doc.RootElement.GetProperty("completedAt").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task SomenteTentativaIncompleta_LatestNaoAApresentaComoResultado_ELatestStateADeclara()
    {
        if (_api is null) return;
        var t = await _api.SeedTenantAsync("Só tentativa");
        var orfa = await SeedUnfinishedAsync(t.Id, DateTimeOffset.UtcNow);

        using var client = _api.As(t.Analyst);

        using (var r = await client.GetAsync(Latest))
            r.StatusCode.Should().Be(HttpStatusCode.NoContent,
                "uma execução em Running não é devolvida como a última avaliação");

        using var doc = await GetJsonAsync(client, LatestState);
        doc.RootElement.GetProperty("assessment").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("unfinishedAttempt").GetProperty("id").GetGuid().Should().Be(orfa);
    }

    [Fact]
    public async Task IsolamentoPorTenant_NasDuasRotas_ENoAcessoPorId()
    {
        if (_api is null) return;
        var alfa = await _api.SeedTenantAsync("Alfa");
        var beta = await _api.SeedTenantAsync("Beta");

        var orfaBeta = await SeedUnfinishedAsync(beta.Id, DateTimeOffset.UtcNow.AddMinutes(10));

        Guid concluidaBeta;
        using (var betaManager = _api.As(beta.Manager))
        using (var post = await betaManager.PostAsync("/api/v1/knight/assessments/demo", null))
        {
            using var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
            concluidaBeta = body.RootElement.GetProperty("id").GetGuid();
        }

        using var alfaClient = _api.As(alfa.Analyst);

        using (var r = await alfaClient.GetAsync(Latest))
            r.StatusCode.Should().Be(HttpStatusCode.NoContent, "Alfa não enxerga o resultado de Beta");

        using (var doc = await GetJsonAsync(alfaClient, LatestState))
        {
            doc.RootElement.GetProperty("assessment").ValueKind.Should().Be(JsonValueKind.Null);
            doc.RootElement.GetProperty("unfinishedAttempt").ValueKind.Should().Be(JsonValueKind.Null,
                "a tentativa abandonada é de Beta");
        }

        foreach (var id in new[] { orfaBeta, concluidaBeta })
            using (var r = await alfaClient.GetAsync($"/api/v1/knight/assessments/{id}"))
                r.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Cabeçalho de outro tenant: recusado antes do controller, nas duas rotas.
        using var cruzado = _api.As(alfa.Analyst.AccessToken, beta.Id);
        foreach (var rota in new[] { Latest, LatestState })
            using (var r = await cruzado.GetAsync(rota))
                r.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var anonimo = _api.Anonymous();
        using (var r = await anonimo.GetAsync(LatestState))
            r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        _output.WriteLine("Isolamento por tenant confirmado nas duas rotas e no acesso por Id.");
    }

    // ---- infraestrutura ---------------------------------------------------------------------------

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {route} → {body}");
        return JsonDocument.Parse(body);
    }

    /// <summary>Execução ABANDONADA na forma das encontradas no ambiente real — nunca reparada.</summary>
    private async Task<Guid> SeedUnfinishedAsync(Guid tenantId, DateTimeOffset startedAt)
    {
        await using var db = new AegisScoreDbContext(_api!.DbOptions(), new SystemTenantContext(tenantId));
        var run = new KnightAssessmentRun
        {
            TenantId = tenantId,
            Mode = KnightAssessmentMode.Live,
            SourceType = KnightSourceType.MicrosoftEntraId,
            SourceState = KnightSourceState.Completed,
            Source = "Microsoft Entra ID",
            Status = KnightRunStatus.Running,
            CatalogVersion = KnightCatalog.Version,
            ScoreFormulaVersion = "teste",
            StartedAt = startedAt,
            Coverage = 0,
        };
        db.KnightAssessmentRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }
}
