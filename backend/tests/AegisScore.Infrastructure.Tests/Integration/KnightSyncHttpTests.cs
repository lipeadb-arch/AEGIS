using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Integration;

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] A jornada INTEIRA pelo pipeline HTTP real (JWT, middleware de consistência de
/// tenant, roteamento, serialização) sobre PostgreSQL real:
///
///   Configurações → Integrações → "Sincronizar agora" (202 com identificador, pedido DURÁVEL) → o worker
///   processa com a autoridade única de coleta → ADM → avaliação → a tela acompanha AQUELE pedido até
///   Concluído com o <c>runId</c> → a avaliação é lida → a fotografia é publicada → HTML e CSV baixados.
///
/// Só a fronteira de rede é fixture (o coletor roteirizado do Entra). E os caminhos de proteção: duplo clique
/// não coleta duas vezes, conector desabilitado recusa, uma tentativa que falha não apaga a avaliação anterior
/// (que continua identificada como a anterior), e outro tenant não lê o pedido.
/// </summary>
public sealed class KnightSyncHttpTests : IClassFixture<AegisApiFixture>
{
    private readonly AegisApiHarness? _api;
    private readonly ITestOutputHelper _output;

    public KnightSyncHttpTests(AegisApiFixture fixture, ITestOutputHelper output)
    {
        _api = fixture.Api;
        _output = output;
    }

    [Fact]
    public async Task Integracoes_SincronizarAgora_ColetaAvalia_EGeraORelatorioDaMesmaAvaliacao()
    {
        if (_api is null) return;   // AEGIS_TEST_PG ausente — pulado honestamente
        var t = await _api.SeedTenantAsync("Sync Alfa");
        var em = _api.OpenTimeWindow();
        var conector = await ConfigurarConectorAsync(t);
        _api.Entra.Observe(privilegedTotal: 12, withoutMfa: 2, collectedAt: em);
        var chamadasAntes = _api.Entra.Calls;

        using var admin = _api.As(t.Admin);

        Guid pedido;
        using (var r = await admin.PostAsync($"/api/v1/connectors/{conector}/sync", null))
        {
            r.StatusCode.Should().Be(HttpStatusCode.Accepted, await r.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            pedido = doc.RootElement.GetProperty("id").GetGuid();
            doc.RootElement.GetProperty("status").GetString().Should().Be("Pending");
            doc.RootElement.GetProperty("alreadyActive").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("source").GetString().Should().Be("MicrosoftEntraId");
        }

        // Duplo clique: o MESMO pedido, nenhuma coleta nova.
        using (var r = await admin.PostAsync($"/api/v1/connectors/{conector}/sync", null))
        {
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("id").GetGuid().Should().Be(pedido);
            doc.RootElement.GetProperty("alreadyActive").GetBoolean().Should().BeTrue();
        }

        using (var doc = await GetJsonAsync(admin, "/api/v1/connectors"))
            doc.RootElement.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == conector)
                .GetProperty("lastStatus").GetString().Should().Be("Syncing");

        (await _api.RunKnightSyncOnceAsync()).Should().BeTrue();
        _api.Entra.Calls.Should().Be(chamadasAntes + 1, "uma sincronização = UMA coleta na fonte");
        (await _api.RunKnightSyncOnceAsync()).Should().BeFalse("não restou pedido — o segundo clique não gerou outro");

        Guid runId;
        using (var doc = await GetJsonAsync(admin, $"/api/v1/connectors/{conector}/sync-requests/{pedido}"))
        {
            var root = doc.RootElement;
            root.GetProperty("status").GetString().Should().Be("Completed");
            root.GetProperty("resultSourceState").GetString().Should().Be("Completed");
            runId = root.GetProperty("runId").GetGuid();
            root.GetProperty("lastCompletedRunId").GetGuid().Should().Be(runId);
        }
        using (var doc = await GetJsonAsync(admin, $"/api/v1/connectors/{conector}/sync-requests/latest"))
            doc.RootElement.GetProperty("id").GetGuid().Should().Be(pedido);

        // A avaliação produzida é a mesma que /latest (contrato do PR #75) e traz o perfil de cada controle.
        using var analyst = _api.As(t.Analyst);
        using (var doc = await GetJsonAsync(analyst, "/api/v1/knight/assessments/latest"))
        {
            doc.RootElement.GetProperty("id").GetGuid().Should().Be(runId);
            doc.RootElement.GetProperty("status").GetString().Should().Be("Completed");
            var ind = doc.RootElement.GetProperty("indicators").EnumerateArray().First(i => i.GetProperty("indicatorId").GetString() == "AK-ENTRA-002");
            ind.GetProperty("presentation").GetProperty("service").GetString().Should().Be("Microsoft Entra ID");
            ind.GetProperty("presentation").GetProperty("weight").GetInt32().Should().Be(7);
        }
        using (var doc = await GetJsonAsync(analyst, $"/api/v1/knight/assessments/{runId}/affected-summary"))
            doc.RootElement.GetProperty("occurrences").GetInt32().Should().BeGreaterThan(0);

        // Relatório da MESMA avaliação (por Id — nunca "a mais recente" em silêncio).
        using var manager = _api.As(t.Manager);
        Guid snapshot;
        using (var r = await manager.PostAsync("/api/v1/posture/snapshots", AegisApiHarness.JsonBody(new { type = "knight", runId })))
        {
            r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            snapshot = doc.RootElement.GetProperty("summary").GetProperty("id").GetGuid();
            doc.RootElement.GetProperty("summary").GetProperty("sourceRunId").GetGuid().Should().Be(runId);
            doc.RootElement.GetProperty("summary").GetProperty("schemaVersion").GetString().Should().Be("posture-snapshot-v2");
        }

        using (var r = await analyst.GetAsync($"/api/v1/posture/snapshots/{snapshot}/export?format=html"))
        {
            r.StatusCode.Should().Be(HttpStatusCode.OK);
            r.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
            var html = await r.Content.ReadAsStringAsync();
            html.Should().Contain("default-src 'none'").And.Contain("Controles e findings").And.NotContain("<link");
            r.Content.Headers.ContentDisposition!.FileNameStar.Should().EndWith(".html");
        }
        using (var r = await analyst.GetAsync($"/api/v1/posture/snapshots/{snapshot}/export?format=csv"))
        {
            r.StatusCode.Should().Be(HttpStatusCode.OK);
            var csv = Encoding.UTF8.GetString(await r.Content.ReadAsByteArrayAsync());
            csv.Should().Contain("RowKind").And.Contain("ObjectRelation");
        }

        // Outro tenant não lê o pedido nem o conector.
        var beta = await _api.SeedTenantAsync("Sync Beta");
        using var betaAdmin = _api.As(beta.Admin);
        using (var r = await betaAdmin.GetAsync($"/api/v1/connectors/{conector}/sync-requests/{pedido}"))
            r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var r = await betaAdmin.PostAsync($"/api/v1/connectors/{conector}/sync", null))
            r.StatusCode.Should().Be(HttpStatusCode.NotFound);

        _output.WriteLine($"Pedido {pedido} → avaliação {runId} → fotografia {snapshot}.");
    }

    [Fact]
    public async Task TentativaQueFalha_NaoApagaAAnterior_EConectorDesabilitadoRecusa()
    {
        if (_api is null) return;
        var t = await _api.SeedTenantAsync("Sync Gama");
        var em = _api.OpenTimeWindow();
        var conector = await ConfigurarConectorAsync(t);
        _api.Entra.Observe(privilegedTotal: 12, withoutMfa: 1, collectedAt: em);
        using var admin = _api.As(t.Admin);

        // Uma sincronização bem-sucedida estabelece a avaliação ANTERIOR.
        using (var r = await admin.PostAsync($"/api/v1/connectors/{conector}/sync", null))
            r.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await _api.RunKnightSyncOnceAsync()).Should().BeTrue();
        Guid anterior;
        using (var doc = await GetJsonAsync(admin, $"/api/v1/connectors/{conector}/sync-requests/latest"))
            anterior = doc.RootElement.GetProperty("runId").GetGuid();

        // Novo pedido; o conector é desabilitado ANTES do processamento: o worker não coleta e registra por quê.
        Guid pedido;
        using (var r = await admin.PostAsync($"/api/v1/connectors/{conector}/sync", null))
        {
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            pedido = doc.RootElement.GetProperty("id").GetGuid();
        }
        using (var r = await admin.PostAsync($"/api/v1/connectors/{conector}/disable", null))
            r.StatusCode.Should().Be(HttpStatusCode.OK);

        var chamadas = _api.Entra.Calls;
        (await _api.RunKnightSyncOnceAsync()).Should().BeTrue();
        _api.Entra.Calls.Should().Be(chamadas, "conector desabilitado não coleta");

        using (var doc = await GetJsonAsync(admin, $"/api/v1/connectors/{conector}/sync-requests/{pedido}"))
        {
            var root = doc.RootElement;
            root.GetProperty("status").GetString().Should().Be("Failed");
            root.GetProperty("failureCategory").GetString().Should().Be("ConnectorNotConfigured");
            root.GetProperty("runId").ValueKind.Should().Be(JsonValueKind.Null);
            root.GetProperty("lastCompletedRunId").GetGuid().Should().Be(anterior, "a avaliação anterior continua disponível e identificada como anterior");
        }
        using (var doc = await GetJsonAsync(admin, "/api/v1/knight/assessments/latest"))
            doc.RootElement.GetProperty("id").GetGuid().Should().Be(anterior);

        // Com o conector desabilitado, um novo pedido é recusado já na borda.
        using (var r = await admin.PostAsync($"/api/v1/connectors/{conector}/sync", null))
            r.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- infraestrutura ---------------------------------------------------------------------------

    private async Task<Guid> ConfigurarConectorAsync(SeededTenant t)
    {
        using var admin = _api!.As(t.Admin);
        var settings = JsonSerializer.Serialize(new
        {
            tenantId = Guid.NewGuid().ToString(),
            clientId = Guid.NewGuid().ToString(),
            clientSecret = "segredo-sintetico-" + Guid.NewGuid().ToString("N"),
        });
        using (var response = await admin.PostAsync("/api/v1/tenants/connectors", AegisApiHarness.JsonBody(new
        {
            provider = 0, capability = 10, displayName = "Diretório sintético de validação", authType = 0, settings, syncIntervalMinutes = 360,
        })))
            response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());

        using var doc = await GetJsonAsync(admin, "/api/v1/connectors");
        return doc.RootElement.EnumerateArray().Single(c => c.GetProperty("capability").GetString() == "IdentityPosture").GetProperty("id").GetGuid();
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {route} → {body}");
        return JsonDocument.Parse(body);
    }
}
