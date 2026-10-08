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
/// [AEGIS-AUDITOR-CONTEXT-01] O Auditor Virtual e a visão de evidências pelo PIPELINE HTTP REAL (login, JWT, papéis, tenant) sobre
/// PostgreSQL real e o motor SIMULADO do host de teste (nenhuma chamada externa):
///   • a resposta cita fontes do tenant (natureza, link com a seleção) e é marcada como demonstração;
///   • a conversa pertence ao tenant E à conta: outra pessoa do mesmo tenant e outro tenant recebem 404;
///   • seleção NIST de outro tenant e identificadores malformados são recusados (404/400), nunca ignorados;
///   • conversar não altera avaliação nem trilha; a visão de evidências é do tenant (outro tenant: 404).
/// </summary>
public sealed class AuditorHttpTests : IClassFixture<AegisApiFixture>
{
    private const string Base = "/api/v1/nist/assessments";
    private const string Chat = "/api/v1/auditor/chat";

    private readonly AegisApiHarness? _api;
    private readonly ITestOutputHelper _output;

    public AuditorHttpTests(AegisApiFixture fixture, ITestOutputHelper output)
    {
        _api = fixture.Api;
        _output = output;
    }

    [Fact]
    public async Task Auditor_FontesDoTenant_ConversaPorConta_IsolamentoEValidacao_SemAlterarResultados()
    {
        if (_api is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var t = await _api.SeedTenantAsync("Cliente Auditor");
        var other = await _api.SeedTenantAsync("Cliente Vizinho Auditor");

        string a, c, s;
        using (var manager = _api.As(t.Manager))
        {
            using (var r = await manager.PostAsync(Base, AegisApiHarness.JsonBody(new { name = "Avaliação do auditor", initialScopeName = "Matriz" })))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.Created, text);
                using var doc = JsonDocument.Parse(text);
                a = doc.RootElement.GetProperty("id").GetString()!;
                s = doc.RootElement.GetProperty("scopes")[0].GetProperty("id").GetString()!;
                c = doc.RootElement.GetProperty("cycles")[0].GetProperty("id").GetString()!;
            }
            using (var r = await manager.PutAsync($"{Base}/{a}/cycles/{c}/scopes/{s}/subcategories/GV.PO-01",
                       AegisApiHarness.JsonBody(new { currentLevel = 2, targetLevel = 4, gaps = "Política sem aprovação.", rationale = "Rascunho de política.", expectedVersion = 0 })))
                r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        }

        var selection = new { assessmentId = a, cycleId = c, scopeId = s, code = "GV.PO-01" };
        string conversationId;
        int auditBefore;
        using (var manager = _api.As(t.Manager))
        {
            auditBefore = await AuditCountAsync(manager, a, c);
            using (var r = await manager.PostAsync(Chat, AegisApiHarness.JsonBody(new { page = "nist", message = "O que falta comprovar nesta subcategoria?", nist = selection })))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.OK, text);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                root.GetProperty("mode").GetString().Should().Be("Simulated");
                root.GetProperty("intent").GetString().Should().Be("COPILOT");
                root.GetProperty("reply").GetString().Should().StartWith("[Demonstração").And.Contain("assessor");
                root.GetProperty("focusLabel").GetString().Should().Contain("GV.PO-01").And.Contain("Avaliação do auditor");
                var sources = root.GetProperty("sources").EnumerateArray().ToList();
                sources.Should().NotBeEmpty();
                sources.Should().Contain(x => x.GetProperty("key").GetString()!.StartsWith("N"));
                var linked = sources.First(x => x.GetProperty("link").ValueKind == JsonValueKind.Object && x.GetProperty("key").GetString()!.StartsWith("N"));
                linked.GetProperty("link").GetProperty("query").GetProperty("avaliacao").GetString().Should().Be(a);
                root.GetProperty("limitations").EnumerateArray().Should().NotBeEmpty();
                conversationId = root.GetProperty("conversationId").GetString()!;
            }
            using (var r = await manager.PostAsync(Chat, AegisApiHarness.JsonBody(new { page = "nist", message = "E o KNIGHT?", conversationId, nist = selection })))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.OK, text);
                using var doc = JsonDocument.Parse(text);
                doc.RootElement.GetProperty("conversationId").GetString().Should().Be(conversationId, "a mesma conversa continua");
            }

            // Identificadores malformados e mensagem longa → 400.
            using (var r = await manager.PostAsync(Chat, AegisApiHarness.JsonBody(new { page = "nist", message = "?", nist = new { assessmentId = a } })))
                r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using (var r = await manager.PostAsync(Chat, AegisApiHarness.JsonBody(new { message = new string('x', 4001) })))
                r.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // Conversar não grava nada na avaliação nem na trilha.
            using (var d = await GetJsonAsync(manager, $"{Base}/{a}/cycles/{c}/scopes/{s}/subcategories/GV.PO-01"))
                d.RootElement.GetProperty("evaluation").GetProperty("version").GetInt32().Should().Be(1);
            (await AuditCountAsync(manager, a, c)).Should().Be(auditBefore);

            using (var o = await GetJsonAsync(manager, $"{Base}/{a}/cycles/{c}/scopes/{s}/evidence-overview"))
                o.RootElement.GetProperty("summary").GetProperty("subcategoriesEvaluatedWithoutEvidence").GetInt32().Should().Be(1);
        }

        using (var analyst = _api.As(t.Analyst))
        {
            using (var r = await analyst.PostAsync(Chat, AegisApiHarness.JsonBody(new { message = "Continuar.", conversationId })))
                r.StatusCode.Should().Be(HttpStatusCode.NotFound, "a conversa é da conta que a iniciou");
            // As páginas de documentos e de ativos usam a gravação de sempre: quem só consulta não vincula.
            using (var r = await analyst.PostAsync($"{Base}/{a}/cycles/{c}/scopes/{s}/subcategories/ID.AM-01/evidence", AegisApiHarness.JsonBody(new { kind = "AssetInventory" })))
                r.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Analyst consulta, não vincula evidência");
        }

        using (var outsider = _api.As(other.Manager))
        {
            using (var r = await outsider.PostAsync(Chat, AegisApiHarness.JsonBody(new { message = "Continuar.", conversationId })))
                r.StatusCode.Should().Be(HttpStatusCode.NotFound, "conversa de outro tenant não é lida");
            using (var r = await outsider.PostAsync(Chat, AegisApiHarness.JsonBody(new { page = "nist", message = "E esta avaliação?", nist = selection })))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.NotFound, "seleção de outro tenant é recusada");
                text.Should().NotContain("Avaliação do auditor");
            }
            using (var r = await outsider.GetAsync($"{Base}/{a}/cycles/{c}/scopes/{s}/evidence-overview"))
                r.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using (var r = await outsider.PostAsync(Chat, AegisApiHarness.JsonBody(new { page = "dashboard", message = "Resuma meu ambiente." })))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.OK, text);
                text.Should().NotContain("Avaliação do auditor", "nada do tenant vizinho entra na resposta");
            }
        }
    }

    private static async Task<int> AuditCountAsync(HttpClient client, string a, string c)
    {
        using var audit = await GetJsonAsync(client, $"{Base}/{a}/audit?cycleId={c}");
        return audit.RootElement.GetArrayLength();
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url);
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text);
    }
}
