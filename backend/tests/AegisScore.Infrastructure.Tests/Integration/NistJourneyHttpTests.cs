using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Integration;

/// <summary>
/// [AEGIS-NIST-JOURNEY-01] A jornada do AEGIS NIST pelo PIPELINE HTTP REAL (login, JWT, papéis, isolamento por tenant), sobre
/// PostgreSQL real — exatamente as chamadas que a tela faz:
///   • Analyst lê, mas não cria avaliação nem grava subcategoria (403);
///   • Manager cria avaliação com escopo, navega uma função com o catálogo completo e grava atual × alvo; o autor vem do token;
///   • a releitura traz o que foi gravado (recarregamento); versão desatualizada é 409; nível fora da escala é 400;
///   • outro tenant recebe 404 para avaliação, escopo e gravação, e lista vazia;
///   • a sugestão da IA nunca vira avaliação; a evolução mensal responde mesmo sem fotografias.
/// </summary>
public sealed class NistJourneyHttpTests : IClassFixture<AegisApiFixture>
{
    private const string Base = "/api/v1/nist/assessments";

    private readonly AegisApiHarness? _api;
    private readonly ITestOutputHelper _output;

    public NistJourneyHttpTests(AegisApiFixture fixture, ITestOutputHelper output)
    {
        _api = fixture.Api;
        _output = output;
    }

    [Fact]
    public async Task Jornada_PapeisIsolamentoPersistenciaEConcorrencia()
    {
        if (_api is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var a = await _api.SeedTenantAsync("Cliente Demo A");
        var b = await _api.SeedTenantAsync("Cliente Demo B");
        var create = new { name = "Avaliação NIST 2026", description = "Diagnóstico organizacional", initialScopeName = "Matriz e nuvem" };

        using (var analyst = _api.As(a.Analyst))
        {
            using (var doc = await GetJsonAsync(analyst, Base))
                doc.RootElement.GetArrayLength().Should().Be(0);
            using var r = await analyst.PostAsync(Base, AegisApiHarness.JsonBody(create));
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Analyst lê, mas não cria avaliação");
        }

        string assessmentId, scopeId;
        using (var manager = _api.As(a.Manager))
        {
            using (var r = await manager.PostAsync(Base, AegisApiHarness.JsonBody(create)))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.Created, text);
                using var doc = JsonDocument.Parse(text);
                assessmentId = doc.RootElement.GetProperty("id").GetString()!;
                scopeId = doc.RootElement.GetProperty("scopes")[0].GetProperty("id").GetString()!;
                doc.RootElement.GetProperty("methodologyVersion").GetString().Should().Be("aegis-methodology-v1");
            }

            using (var fn = await GetJsonAsync(manager, $"{Base}/{assessmentId}/scopes/{scopeId}/functions/id"))
            {
                var categories = fn.RootElement.GetProperty("categories").EnumerateArray().ToList();
                categories.Select(c => c.GetProperty("code").GetString()).Should().Equal("ID.AM", "ID.IM", "ID.RA");
                categories.SelectMany(c => c.GetProperty("subcategories").EnumerateArray())
                    .Should().OnlyContain(s => s.GetProperty("state").GetString() == "NotEvaluated");
                fn.RootElement.GetProperty("profile").GetProperty("current").ValueKind.Should().Be(JsonValueKind.Null, "sem nota não é zero");
            }

            var save = new { currentLevel = 2, targetLevel = 4, notApplicable = false, ownerName = "Diretoria de TI", expectedVersion = 0 };
            using (var r = await manager.PutAsync($"{Base}/{assessmentId}/scopes/{scopeId}/subcategories/ID.AM-01", AegisApiHarness.JsonBody(save)))
                r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            using (var r = await manager.PutAsync($"{Base}/{assessmentId}/scopes/{scopeId}/subcategories/ID.AM-01", AegisApiHarness.JsonBody(save)))
                r.StatusCode.Should().Be(HttpStatusCode.Conflict, "a versão 0 já foi superada");
            using (var r = await manager.PutAsync($"{Base}/{assessmentId}/scopes/{scopeId}/subcategories/ID.AM-02",
                       AegisApiHarness.JsonBody(save with { currentLevel = 7 })))
                r.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            using (var r = await manager.PostAsync($"{Base}/{assessmentId}/scopes/{scopeId}/subcategories/ID.AM-03/ai-suggestion", AegisApiHarness.JsonBody(new { })))
                new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable }.Should().Contain(r.StatusCode, "a IA pode estar simulada ou desativada");
            using (var d = await GetJsonAsync(manager, $"{Base}/{assessmentId}/scopes/{scopeId}/subcategories/ID.AM-03"))
                d.RootElement.GetProperty("evaluation").ValueKind.Should().Be(JsonValueKind.Null, "a sugestão nunca é gravada");
        }

        using (var analyst = _api.As(a.Analyst))
        {
            using (var r = await analyst.PutAsync($"{Base}/{assessmentId}/scopes/{scopeId}/subcategories/ID.AM-01",
                       AegisApiHarness.JsonBody(new { currentLevel = 5, targetLevel = 5, expectedVersion = 1 })))
                r.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Analyst não grava avaliação");

            // Releitura (o equivalente HTTP do recarregamento da tela).
            using var d = await GetJsonAsync(analyst, $"{Base}/{assessmentId}/scopes/{scopeId}/subcategories/ID.AM-01");
            var e = d.RootElement.GetProperty("evaluation");
            (e.GetProperty("currentLevel").GetInt32(), e.GetProperty("targetLevel").GetInt32(), e.GetProperty("gap").GetInt32()).Should().Be((2, 4, 2));
            e.GetProperty("version").GetInt32().Should().Be(1);
            e.GetProperty("ownerName").GetString().Should().Be("Diretoria de TI");
            e.GetProperty("reviewedByName").GetString().Should().Be("Manager Cliente Demo A", "o autor vem do token, nunca do corpo");

            using var p = await GetJsonAsync(analyst, $"{Base}/{assessmentId}/scopes/{scopeId}/profile");
            p.RootElement.GetProperty("overall").GetProperty("current").GetDouble().Should().Be(2);
        }

        using (var other = _api.As(b.Manager))
        {
            using (var doc = await GetJsonAsync(other, Base))
                doc.RootElement.GetArrayLength().Should().Be(0, "o tenant B não vê a avaliação de A");
            foreach (var url in new[] { $"{Base}/{assessmentId}", $"{Base}/{assessmentId}/scopes/{scopeId}/functions/GV",
                         $"{Base}/{assessmentId}/scopes/{scopeId}/subcategories/ID.AM-01" })
            {
                using var r = await other.GetAsync(url);
                r.StatusCode.Should().Be(HttpStatusCode.NotFound, url);
            }
            using (var r = await other.PutAsync($"{Base}/{assessmentId}/scopes/{scopeId}/subcategories/ID.AM-01",
                       AegisApiHarness.JsonBody(new { currentLevel = 5, targetLevel = 5, expectedVersion = 1 })))
                r.StatusCode.Should().Be(HttpStatusCode.NotFound, "a gravação cruzada não encontra a avaliação");

            using var monthly = await GetJsonAsync(other, "/api/v1/posture/snapshots/monthly?months=12");
            monthly.RootElement.GetProperty("months").GetArrayLength().Should().Be(12);
            monthly.RootElement.GetProperty("series").GetArrayLength().Should().Be(0, "sem fotografia publicada, nenhum ponto é inventado");
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url);
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text);
    }
}
