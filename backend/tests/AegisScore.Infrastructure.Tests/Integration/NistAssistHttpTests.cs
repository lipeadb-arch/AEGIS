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
/// [AEGIS-NIST-AI-ASSIST-01] A assistência de IA da jornada NIST pelo PIPELINE HTTP REAL (login, JWT, papéis, tenant), sobre
/// PostgreSQL real e o motor SIMULADO configurado no host de teste (nenhuma chamada externa):
///   • Analyst confere fontes, mas não gera nem incorpora (403); outro tenant recebe 404 em todas as rotas novas;
///   • gerar não grava a avaliação; incorporar passa pela gravação normal com a referência da sugestão; sugestão envelhecida
///     é 409 (a edição continua no cliente);
///   • resumo executivo: gerar → aceitar → revisão por OUTRA pessoa → publicação → HTML com a interpretação e a procedência;
///   • o Auditor Virtual recebe a seleção NIST da tela; seleção de outro tenant é recusada (404).
/// </summary>
public sealed class NistAssistHttpTests : IClassFixture<AegisApiFixture>
{
    private const string Base = "/api/v1/nist/assessments";

    private readonly AegisApiHarness? _api;
    private readonly ITestOutputHelper _output;

    public NistAssistHttpTests(AegisApiFixture fixture, ITestOutputHelper output)
    {
        _api = fixture.Api;
        _output = output;
    }

    [Fact]
    public async Task Assistencia_PapeisIsolamento_Incorporacao_ResumoExecutivo_Publicacao_EAuditor()
    {
        if (_api is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var t = await _api.SeedTenantAsync("Cliente Assistido");
        var other = await _api.SeedTenantAsync("Cliente Vizinho");

        string a, c, s;
        using (var manager = _api.As(t.Manager))
        {
            using (var r = await manager.PostAsync(Base, AegisApiHarness.JsonBody(new { name = "Avaliação assistida", initialScopeName = "Matriz" })))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.Created, text);
                using var doc = JsonDocument.Parse(text);
                a = doc.RootElement.GetProperty("id").GetString()!;
                s = doc.RootElement.GetProperty("scopes")[0].GetProperty("id").GetString()!;
                c = doc.RootElement.GetProperty("cycles")[0].GetProperty("id").GetString()!;
            }
            using (var r = await manager.PutAsync($"{Base}/{a}/cycles/{c}/scopes/{s}/subcategories/ID.AM-01",
                       AegisApiHarness.JsonBody(new { currentLevel = 2, targetLevel = 4, gaps = "Planilha sem dono por ativo.", rationale = "Inventário parcial.", expectedVersion = 0 })))
                r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            using var availability = await GetJsonAsync(manager, $"{Base}/assist/availability");
            availability.RootElement.GetProperty("canGenerate").GetBoolean().Should().BeTrue();
            availability.RootElement.GetProperty("state").GetString().Should().NotBe("Real", "o host de teste não tem provedor externo");
        }

        var sub = $"{Base}/{a}/cycles/{c}/scopes/{s}/subcategories/ID.AM-01";
        using (var analyst = _api.As(t.Analyst))
        {
            using (var ctx = await GetJsonAsync(analyst, $"{sub}/assist/context"))
                ctx.RootElement.GetProperty("sources").EnumerateArray().Select(x => x.GetProperty("kind").GetString()).Should().Contain("Evaluation");
            using (var r = await analyst.PostAsync($"{sub}/assist", AegisApiHarness.JsonBody(new { })))
                r.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Analyst confere, mas não gera");
        }

        string assistanceId, rationale;
        using (var manager = _api.As(t.Manager))
        {
            using (var r = await manager.PostAsync($"{sub}/assist", AegisApiHarness.JsonBody(new { reuse = true })))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.OK, text);
                using var doc = JsonDocument.Parse(text);
                doc.RootElement.GetProperty("mode").GetString().Should().Be("Simulated");
                doc.RootElement.GetProperty("disclaimer").GetString().Should().Contain("DEMONSTRAÇÃO");
                assistanceId = doc.RootElement.GetProperty("id").GetString()!;
                rationale = doc.RootElement.GetProperty("applicable").GetProperty("rationale").GetString()!;
            }
            using (var d = await GetJsonAsync(manager, sub))
                d.RootElement.GetProperty("evaluation").GetProperty("version").GetInt32().Should().Be(1, "gerar não grava a avaliação");

            var incorporate = new
            {
                currentLevel = 2, targetLevel = 4, gaps = "Planilha sem dono por ativo.", rationale = rationale + "\nRevisado pela gestora.", expectedVersion = 1,
                assistance = new { assistanceId, fields = new[] { "rationale" } },
            };
            using (var r = await manager.PutAsync(sub, AegisApiHarness.JsonBody(incorporate)))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.OK, text);
                using var doc = JsonDocument.Parse(text);
                var field = doc.RootElement.GetProperty("evaluation").GetProperty("assistedFields")[0];
                (field.GetProperty("field").GetString(), field.GetProperty("edited").GetBoolean(), field.GetProperty("mode").GetString())
                    .Should().Be(("rationale", true, "Simulated"));
            }
            using (var r = await manager.PutAsync(sub, AegisApiHarness.JsonBody(incorporate with { expectedVersion = 2 })))
                r.StatusCode.Should().Be(HttpStatusCode.Conflict, "a sugestão foi gerada sobre a versão anterior da avaliação");

            using (var r = await manager.PostAsync($"{Base}/{a}/cycles/{c}/scopes/{s}/executive-summary/assist", AegisApiHarness.JsonBody(new { })))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.OK, text);
                using var doc = JsonDocument.Parse(text);
                var sections = doc.RootElement.GetProperty("applicable").EnumerateObject().Select(p => new { key = p.Name, text = p.Value.GetString() }).ToArray();
                using var put = await manager.PutAsync($"{Base}/{a}/cycles/{c}/scopes/{s}/executive-summary", AegisApiHarness.JsonBody(new
                {
                    sections, assistanceId = doc.RootElement.GetProperty("id").GetString(), expectedVersion = 0,
                }));
                put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
            }
        }

        using (var admin = _api.As(t.Admin))
        using (var r = await admin.PostAsync($"{Base}/{a}/cycles/{c}/scopes/{s}/executive-summary/review", AegisApiHarness.JsonBody(new { expectedVersion = 1, note = "Coerente." })))
            r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());

        string snapshotId;
        using (var manager = _api.As(t.Manager))
        {
            string fingerprint;
            using (var preview = await GetJsonAsync(manager, $"{Base}/{a}/cycles/{c}/scopes/{s}/publication-preview"))
            {
                fingerprint = preview.RootElement.GetProperty("contentFingerprint").GetString()!;
                preview.RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(w => w!.Contains("motor SIMULADO"));
            }
            using (var r = await manager.PostAsync($"{Base}/{a}/cycles/{c}/scopes/{s}/publications", AegisApiHarness.JsonBody(new { expectedFingerprint = fingerprint })))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.Created, text);
                using var doc = JsonDocument.Parse(text);
                snapshotId = doc.RootElement.GetProperty("snapshotId").GetString()!;
            }
            using (var r = await manager.GetAsync($"/api/v1/posture/snapshots/{snapshotId}/export?format=html"))
            {
                var html = await r.Content.ReadAsStringAsync();
                html.Should().Contain("Interpretação executiva").And.Contain("DEMONSTRAÇÃO").And.Contain("Conteúdo assistido por IA");
            }

            // O Auditor conversa sobre a seleção NIST da tela (montada no servidor); a resposta é sugestão, nada é gravado.
            using (var r = await manager.PostAsync("/api/v1/auditor/chat", AegisApiHarness.JsonBody(new
                   {
                       contextScope = "ID", message = "Explique a situação desta subcategoria.",
                       nist = new { assessmentId = a, cycleId = c, scopeId = s, code = "ID.AM-01" },
                   })))
                r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        }

        using (var outsider = _api.As(other.Manager))
        {
            foreach (var url in new[] { $"{sub}/assist/context", $"{Base}/{a}/cycles/{c}/scopes/{s}/executive-summary", $"{Base}/{a}/cycles/{c}/scopes/{s}/executive-summary/assist/context" })
            {
                using var r = await outsider.GetAsync(url);
                r.StatusCode.Should().Be(HttpStatusCode.NotFound, url);
            }
            using (var r = await outsider.PostAsync($"{sub}/assist", AegisApiHarness.JsonBody(new { })))
                r.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using (var r = await outsider.PutAsync(sub, AegisApiHarness.JsonBody(new { currentLevel = 5, expectedVersion = 2, assistance = new { assistanceId, fields = new[] { "rationale" } } })))
                r.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using (var r = await outsider.PostAsync("/api/v1/auditor/chat", AegisApiHarness.JsonBody(new
                   {
                       contextScope = "GLOBAL", message = "E esta avaliação?", nist = new { assessmentId = a, cycleId = c, scopeId = s },
                   })))
                r.StatusCode.Should().Be(HttpStatusCode.NotFound, "[AEGIS-AUDITOR-CONTEXT-01] seleção de outro tenant é recusada, sem vazar nada");
        }

        using (var manager = _api.As(t.Manager))
        {
            using (var r = await manager.DeleteAsync($"{Base}/{a}/cycles/{c}/scopes/{s}/executive-summary?expectedVersion=2"))
                r.StatusCode.Should().Be(HttpStatusCode.NoContent, await r.Content.ReadAsStringAsync());
            using (var r = await manager.GetAsync($"{Base}/{a}/cycles/{c}/scopes/{s}/executive-summary"))
                r.StatusCode.Should().Be(HttpStatusCode.NoContent);
            using var audit = await GetJsonAsync(manager, $"{Base}/{a}/audit?cycleId={c}");
            audit.RootElement.EnumerateArray().Select(e => e.GetProperty("subject").GetString())
                .Should().Contain(new[] { "Assistance", "ExecutiveSummary" });
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
