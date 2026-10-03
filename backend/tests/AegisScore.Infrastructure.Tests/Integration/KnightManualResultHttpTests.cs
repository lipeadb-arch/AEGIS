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
/// [AEGIS-KNIGHT-CLOSURE-01] O percurso do resultado MANUAL pelo PIPELINE HTTP REAL (login, JWT, papéis, isolamento por
/// tenant), sobre PostgreSQL real — exatamente as chamadas que a tela faz:
///   • a cobertura do catálogo diz quais referências aceitam resultado manual;
///   • Analyst lê, mas não registra (403); controle com avaliação automatizada é recusado (400) com o motivo;
///   • Manager registra (201) e o autor vem do token; a cobertura e o histórico passam a mostrar o resultado;
///   • outro tenant não vê nada;
///   • a publicação congela o resultado e o CSV o traz como linha própria, separada dos controles.
/// </summary>
public sealed class KnightManualResultHttpTests : IClassFixture<AegisApiFixture>
{
    private const string Coverage = "/api/v1/knight/assessments/reference-coverage";
    private const string Manual = "/api/v1/knight/assessments/manual-results";

    private readonly AegisApiHarness? _api;
    private readonly ITestOutputHelper _output;

    public KnightManualResultHttpTests(AegisApiFixture fixture, ITestOutputHelper output)
    {
        _api = fixture.Api;
        _output = output;
    }

    [Fact]
    public async Task ResultadoManual_RegistroPorPapel_ConsultaIsolada_EPublicacaoSeparada()
    {
        if (_api is null) return;   // AEGIS_TEST_PG ausente — pulado honestamente
        var a = await _api.SeedTenantAsync("Cliente Demo A");
        var b = await _api.SeedTenantAsync("Cliente Demo B");

        string eligibleKey, automatedKey;
        using (var analyst = _api.As(a.Analyst))
        using (var doc = await GetJsonAsync(analyst, Coverage))
        {
            var controls = doc.RootElement.GetProperty("controls").EnumerateArray().ToList();
            controls.Should().HaveCount(457);
            var eligible = controls.Where(c => c.GetProperty("manualEligible").GetBoolean()).ToList();
            eligible.Should().NotBeEmpty();
            var manualDispositions = new[] { "ManualOnly", "ApiLimitation", "RequiresAccess" };
            eligible.Select(c => c.GetProperty("disposition").GetString()).Should().OnlyContain(d => manualDispositions.Contains(d));
            eligibleKey = eligible.First(c => c.GetProperty("disposition").GetString() == "ManualOnly").GetProperty("key").GetString()!;
            automatedKey = controls.First(c => c.GetProperty("disposition").GetString() == "Implemented").GetProperty("key").GetString()!;
            controls.Should().OnlyContain(c => c.GetProperty("manualResult").ValueKind == JsonValueKind.Null);
            controls.Should().Contain(c => c.GetProperty("previewApis").GetArrayLength() > 0, "a cobertura identifica as leituras em versão preview");
        }

        var body = new
        {
            referenceKey = eligibleKey, result = "Compliant",
            justification = "Verificado no portal de administracao: configuracao conforme a referencia.",
            responsibleName = "Responsavel Demo", evidenceReference = "CHG-0001 (chamado sintetico)",
        };

        using (var analyst = _api.As(a.Analyst))
        using (var r = await analyst.PostAsync(Manual, AegisApiHarness.JsonBody(body)))
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Analyst lê, mas não registra resultado manual");

        using (var manager = _api.As(a.Manager))
        {
            using (var r = await manager.PostAsync(Manual, AegisApiHarness.JsonBody(body with { referenceKey = automatedKey })))
            {
                r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await r.Content.ReadAsStringAsync()).Should().Contain("avaliação automatizada");
            }
            using (var r = await manager.PostAsync(Manual, AegisApiHarness.JsonBody(body with { evidenceReference = (string?)null })))
                r.StatusCode.Should().Be(HttpStatusCode.BadRequest, "sem evidência vinculada o resultado é recusado");

            using (var r = await manager.PostAsync(Manual, AegisApiHarness.JsonBody(body)))
            {
                r.StatusCode.Should().Be(HttpStatusCode.Created);
                using var saved = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                saved.RootElement.GetProperty("result").GetString().Should().Be("Compliant");
                saved.RootElement.GetProperty("recordedByName").GetString().Should().NotBeNullOrWhiteSpace("o autor vem do token, não do corpo");
                saved.RootElement.GetProperty("resultLabel").GetString().Should().Contain("verificação manual");
            }
        }

        using (var analyst = _api.As(a.Analyst))
        {
            using (var doc = await GetJsonAsync(analyst, Coverage))
            {
                var c = doc.RootElement.GetProperty("controls").EnumerateArray().Single(x => x.GetProperty("key").GetString() == eligibleKey);
                c.GetProperty("manualResult").GetProperty("result").GetString().Should().Be("Compliant");
                c.GetProperty("disposition").GetString().Should().Be("ManualOnly", "o resultado manual não muda a disposição automatizada");
                var total = doc.RootElement.GetProperty("total");
                total.GetProperty("total").GetInt32().Should().Be(457);
            }
            using (var doc = await GetJsonAsync(analyst, $"{Manual}/{Uri.EscapeDataString(eligibleKey)}"))
                doc.RootElement.GetArrayLength().Should().Be(1);
        }

        using (var other = _api.As(b.Admin))
        {
            using (var doc = await GetJsonAsync(other, Coverage))
                doc.RootElement.GetProperty("controls").EnumerateArray()
                    .Single(x => x.GetProperty("key").GetString() == eligibleKey).GetProperty("manualResult").ValueKind.Should().Be(JsonValueKind.Null);
            using (var doc = await GetJsonAsync(other, $"{Manual}/{Uri.EscapeDataString(eligibleKey)}"))
                doc.RootElement.GetArrayLength().Should().Be(0, "o histórico de outro tenant não aparece");
        }

        // Avaliação (demonstração sintética) → publicação → exportação: o resultado manual congelado vem em linha própria.
        Guid snapshotId;
        using (var manager = _api.As(a.Manager))
        {
            Guid runId;
            using (var post = await manager.PostAsync("/api/v1/knight/assessments/demo", null))
            {
                post.StatusCode.Should().Be(HttpStatusCode.OK);
                using var run = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
                runId = run.RootElement.GetProperty("id").GetGuid();
            }
            using (var pub = await manager.PostAsync("/api/v1/posture/snapshots", AegisApiHarness.JsonBody(new { type = "Knight", runId })))
            {
                pub.StatusCode.Should().Be(HttpStatusCode.Created);
                using var detail = JsonDocument.Parse(await pub.Content.ReadAsStringAsync());
                snapshotId = detail.RootElement.GetProperty("summary").GetProperty("id").GetGuid();
            }
        }

        using (var analyst = _api.As(a.Analyst))
        {
            var csv = await GetTextAsync(analyst, $"/api/v1/posture/snapshots/{snapshotId}/export?format=csv");
            var lines = csv.TrimStart('﻿').Split('\n').Where(l => l.Length > 0).ToList();
            lines.Count(l => l.Contains(";ResultadoManual;")).Should().Be(1);
            lines.Single(l => l.Contains(";ResultadoManual;")).Should().Contain(eligibleKey).And.Contain("CHG-0001");
            var html = await GetTextAsync(analyst, $"/api/v1/posture/snapshots/{snapshotId}/export?format=html");
            html.Should().Contain("manualResults").And.Contain("CHG-0001");
            using var pdf = await analyst.GetAsync($"/api/v1/posture/snapshots/{snapshotId}/export?format=pdf");
            pdf.StatusCode.Should().Be(HttpStatusCode.OK);
            (await pdf.Content.ReadAsByteArrayAsync()).Take(4).Should().Equal((byte)0x25, (byte)0x50, (byte)0x44, (byte)0x46);
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url);
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text);
    }

    private static async Task<string> GetTextAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        return Encoding.UTF8.GetString(await r.Content.ReadAsByteArrayAsync());
    }
}
