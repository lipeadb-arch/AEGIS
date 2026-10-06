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

        string assessmentId, cycleId, scopeId;
        using (var manager = _api.As(a.Manager))
        {
            using (var r = await manager.PostAsync(Base, AegisApiHarness.JsonBody(create)))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.Created, text);
                using var doc = JsonDocument.Parse(text);
                assessmentId = doc.RootElement.GetProperty("id").GetString()!;
                scopeId = doc.RootElement.GetProperty("scopes")[0].GetProperty("id").GetString()!;
                // [AEGIS-NIST-JOURNEY-02] Toda avaliação nasce com uma rodada; as rotas de trabalho a nomeiam.
                cycleId = doc.RootElement.GetProperty("cycles")[0].GetProperty("id").GetString()!;
                doc.RootElement.GetProperty("methodologyVersion").GetString().Should().Be("aegis-methodology-v1");
            }

            using (var fn = await GetJsonAsync(manager, $"{Base}/{assessmentId}/cycles/{cycleId}/scopes/{scopeId}/functions/id"))
            {
                var categories = fn.RootElement.GetProperty("categories").EnumerateArray().ToList();
                categories.Select(c => c.GetProperty("code").GetString()).Should().Equal(new[] { "ID.AM", "ID.RA", "ID.IM" }, "ordem oficial do CSF 2.0");
                categories.SelectMany(c => c.GetProperty("subcategories").EnumerateArray())
                    .Should().OnlyContain(s => s.GetProperty("state").GetString() == "NotEvaluated");
                fn.RootElement.GetProperty("profile").GetProperty("current").ValueKind.Should().Be(JsonValueKind.Null, "sem nota não é zero");
            }

            var save = new { currentLevel = 2, targetLevel = 4, notApplicable = false, ownerName = "Diretoria de TI", expectedVersion = 0 };
            using (var r = await manager.PutAsync($"{Base}/{assessmentId}/cycles/{cycleId}/scopes/{scopeId}/subcategories/ID.AM-01", AegisApiHarness.JsonBody(save)))
                r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            using (var r = await manager.PutAsync($"{Base}/{assessmentId}/cycles/{cycleId}/scopes/{scopeId}/subcategories/ID.AM-01", AegisApiHarness.JsonBody(save)))
                r.StatusCode.Should().Be(HttpStatusCode.Conflict, "a versão 0 já foi superada");
            using (var r = await manager.PutAsync($"{Base}/{assessmentId}/cycles/{cycleId}/scopes/{scopeId}/subcategories/ID.AM-02",
                       AegisApiHarness.JsonBody(save with { currentLevel = 7 })))
                r.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            using (var r = await manager.PostAsync($"{Base}/{assessmentId}/cycles/{cycleId}/scopes/{scopeId}/subcategories/ID.AM-03/ai-suggestion", AegisApiHarness.JsonBody(new { })))
                new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable }.Should().Contain(r.StatusCode, "a IA pode estar simulada ou desativada");
            using (var d = await GetJsonAsync(manager, $"{Base}/{assessmentId}/cycles/{cycleId}/scopes/{scopeId}/subcategories/ID.AM-03"))
                d.RootElement.GetProperty("evaluation").ValueKind.Should().Be(JsonValueKind.Null, "a sugestão nunca é gravada");
        }

        using (var analyst = _api.As(a.Analyst))
        {
            using (var r = await analyst.PutAsync($"{Base}/{assessmentId}/cycles/{cycleId}/scopes/{scopeId}/subcategories/ID.AM-01",
                       AegisApiHarness.JsonBody(new { currentLevel = 5, targetLevel = 5, expectedVersion = 1 })))
                r.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Analyst não grava avaliação");

            // Releitura (o equivalente HTTP do recarregamento da tela).
            using var d = await GetJsonAsync(analyst, $"{Base}/{assessmentId}/cycles/{cycleId}/scopes/{scopeId}/subcategories/ID.AM-01");
            var e = d.RootElement.GetProperty("evaluation");
            (e.GetProperty("currentLevel").GetInt32(), e.GetProperty("targetLevel").GetInt32(), e.GetProperty("gap").GetInt32()).Should().Be((2, 4, 2));
            e.GetProperty("version").GetInt32().Should().Be(1);
            e.GetProperty("ownerName").GetString().Should().Be("Diretoria de TI");
            e.GetProperty("reviewedByName").GetString().Should().Be("Manager Cliente Demo A", "o autor vem do token, nunca do corpo");

            using var p = await GetJsonAsync(analyst, $"{Base}/{assessmentId}/cycles/{cycleId}/scopes/{scopeId}/profile");
            p.RootElement.GetProperty("overall").GetProperty("current").GetDouble().Should().Be(2);
        }

        using (var other = _api.As(b.Manager))
        {
            using (var doc = await GetJsonAsync(other, Base))
                doc.RootElement.GetArrayLength().Should().Be(0, "o tenant B não vê a avaliação de A");
            foreach (var url in new[] { $"{Base}/{assessmentId}", $"{Base}/{assessmentId}/cycles/{cycleId}/scopes/{scopeId}/functions/GV",
                         $"{Base}/{assessmentId}/cycles/{cycleId}/scopes/{scopeId}/subcategories/ID.AM-01" })
            {
                using var r = await other.GetAsync(url);
                r.StatusCode.Should().Be(HttpStatusCode.NotFound, url);
            }
            using (var r = await other.PutAsync($"{Base}/{assessmentId}/cycles/{cycleId}/scopes/{scopeId}/subcategories/ID.AM-01",
                       AegisApiHarness.JsonBody(new { currentLevel = 5, targetLevel = 5, expectedVersion = 1 })))
                r.StatusCode.Should().Be(HttpStatusCode.NotFound, "a gravação cruzada não encontra a avaliação");

            using var monthly = await GetJsonAsync(other, "/api/v1/posture/snapshots/monthly?months=12");
            monthly.RootElement.GetProperty("months").GetArrayLength().Should().Be(12);
            monthly.RootElement.GetProperty("series").GetArrayLength().Should().Be(0, "sem fotografia publicada, nenhum ponto é inventado");
        }
    }

    /// <summary>
    /// [AEGIS-NIST-JOURNEY-02] A jornada COMPLETA pelo pipeline HTTP real: rodada → designação de pessoas reais do tenant →
    /// avaliação → revisão por OUTRA pessoa → procedimento com resultado → achado com plano (origem NIST) → execução,
    /// validação humana e encerramento → prévia e publicação com impressão digital → HTML, PDF e CSV da mesma fotografia
    /// (inclusive para Analyst) → CSV de trabalho e prévia de importação. Papéis: Analyst lê tudo e não grava nada novo;
    /// outro tenant recebe 404 em todas as rotas novas.
    /// </summary>
    [Fact]
    public async Task JornadaCompleta_RodadaAchadoPlanoPublicacaoEExportacoes_ComPapeisEIsolamento()
    {
        if (_api is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var t = await _api.SeedTenantAsync("Cliente Jornada");
        var other = await _api.SeedTenantAsync("Cliente Vizinho");

        string a, c, s, analystId, adminId;
        using (var manager = _api.As(t.Manager))
        {
            var create = new
            {
                name = "Avaliação NIST 2026", initialScopeName = "Matriz", initialCycleName = "T4 2026",
                initialCyclePeriodKind = "Quarterly", initialCyclePeriodStart = "2026-10-01", initialCyclePeriodEnd = "2026-12-31",
            };
            using (var r = await manager.PostAsync(Base, AegisApiHarness.JsonBody(create)))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.Created, text);
                using var doc = JsonDocument.Parse(text);
                a = doc.RootElement.GetProperty("id").GetString()!;
                s = doc.RootElement.GetProperty("scopes")[0].GetProperty("id").GetString()!;
                var cycle = doc.RootElement.GetProperty("cycles")[0];
                c = cycle.GetProperty("id").GetString()!;
                (cycle.GetProperty("name").GetString(), cycle.GetProperty("periodKind").GetString()).Should().Be(("T4 2026", "Quarterly"));
            }
        }

        using (var analyst = _api.As(t.Analyst))
        using (var people = await GetJsonAsync(analyst, $"{Base}/assignees"))
        {
            var list = people.RootElement.EnumerateArray().ToList();
            list.Should().HaveCount(3, "Analyst vê os usuários ativos do tenant para saber a quem o trabalho foi dado");
            list.Select(p => p.EnumerateObject().Select(x => x.Name).ToList())
                .Should().OnlyContain(names => !names.Contains("email"), "a lista não expõe e-mail");
            analystId = list.Single(p => p.GetProperty("role").GetString() == "Analyst").GetProperty("userId").GetString()!;
            adminId = list.Single(p => p.GetProperty("role").GetString() == "TenantAdmin").GetProperty("userId").GetString()!;
        }

        var sub = $"{Base}/{a}/cycles/{c}/scopes/{s}/subcategories/ID.AM-01";
        string findingId, planId;
        using (var manager = _api.As(t.Manager))
        {
            using (var r = await manager.PutAsync($"{sub}/assignment", AegisApiHarness.JsonBody(new { assessorUserId = analystId, reviewerUserId = adminId, expectedVersion = 0 })))
                r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            using (var r = await manager.PutAsync(sub, AegisApiHarness.JsonBody(new { currentLevel = 2, targetLevel = 4, gaps = "Planilha sem dono por ativo.", ownerUserId = analystId, expectedVersion = 1 })))
                r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            using (var r = await manager.PostAsync($"{sub}/review", AegisApiHarness.JsonBody(new { decision = "Approved", expectedVersion = 2 })))
                r.StatusCode.Should().Be(HttpStatusCode.BadRequest, "quem gravou a versão vigente não a revisa");

            using (var r = await manager.PostAsync($"{sub}/procedures", AegisApiHarness.JsonBody(new { method = "Examine", procedure = "Examinar a planilha de inventário." })))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.Created, text);
                using var doc = JsonDocument.Parse(text);
                var pid = doc.RootElement.GetProperty("id").GetString();
                using var done = await manager.PutAsync($"{sub}/procedures/{pid}", AegisApiHarness.JsonBody(new
                {
                    status = "Performed", outcome = "Unsatisfactory", observation = "30% dos servidores sem responsável.",
                    performedOn = _api.Clock.GetUtcNow().UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd"), expectedVersion = 1,
                }));
                done.StatusCode.Should().Be(HttpStatusCode.OK, await done.Content.ReadAsStringAsync());
            }

            using (var r = await manager.PostAsync($"{sub}/findings", AegisApiHarness.JsonBody(new
            {
                title = "Inventário sem dono", condition = "30% dos servidores sem responsável.", risk = "Ativo sem dono não é corrigido.",
                impact = "Vulnerabilidade sem tratamento.", severity = "High", severityRationale = "Servidores de produção.", priority = "High",
                priorityRationale = "Pré-requisito de outros controles.", recommendation = "Atribuir donos.",
                plan = new { title = "Atribuir donos", responsible = new { userId = analystId }, dueDate = "2026-12-15" },
            })))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.Created, text);
                using var doc = JsonDocument.Parse(text);
                findingId = doc.RootElement.GetProperty("id").GetString()!;
                var plan = doc.RootElement.GetProperty("plan");
                planId = plan.GetProperty("id").GetString()!;
                plan.GetProperty("originKind").GetString().Should().Be("NistFinding");
                plan.GetProperty("responsiblePerson").GetString().Should().Be("Analyst Cliente Jornada");
            }

            var planUrl = $"{Base}/{a}/cycles/{c}/scopes/{s}/findings/{findingId}/plans/{planId}";
            using (var r = await manager.PostAsync($"{planUrl}/execution", AegisApiHarness.JsonBody(new { expectedVersion = 1, notes = "Donos atribuídos.", evidenceReference = "CHG-1" })))
                r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            using (var r = await manager.PostAsync($"{planUrl}/validations", AegisApiHarness.JsonBody(new { expectedVersion = 2, evidenceReference = "Planilha revisada (DOC-9)." })))
                r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        }

        using (var admin = _api.As(t.Admin))
        using (var r = await admin.PostAsync($"{sub}/review", AegisApiHarness.JsonBody(new { decision = "Approved", note = "Coerente.", expectedVersion = 2 })))
            r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());

        using (var analyst = _api.As(t.Analyst))
        {
            foreach (var (url, body) in new (string, object)[]
                     {
                         ($"{sub}/procedures", new { method = "Test", procedure = "Testar o que quer que seja." }),
                         ($"{Base}/{a}/cycles", new { name = "Nova", periodKind = "Other", periodStart = "2027-01-01", periodEnd = "2027-01-31", seedMode = "None" }),
                         ($"{Base}/{a}/cycles/{c}/scopes/{s}/publications", new { expectedFingerprint = new string('0', 64) }),
                         ($"{Base}/{a}/cycles/{c}/scopes/{s}/import/preview", new { csv = "x" }),
                     })
            {
                using var r = await analyst.PostAsync(url, AegisApiHarness.JsonBody(body));
                r.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"Analyst não grava ({url}); ser designado não concede privilégio");
            }
            using var findings = await GetJsonAsync(analyst, $"{Base}/{a}/findings");
            findings.RootElement.GetArrayLength().Should().Be(1, "Analyst lê os achados");
        }

        string snapshotId;
        using (var manager = _api.As(t.Manager))
        {
            string fingerprint;
            using (var preview = await GetJsonAsync(manager, $"{Base}/{a}/cycles/{c}/scopes/{s}/publication-preview"))
                fingerprint = preview.RootElement.GetProperty("contentFingerprint").GetString()!;
            using (var r = await manager.PostAsync($"{Base}/{a}/cycles/{c}/scopes/{s}/publications", AegisApiHarness.JsonBody(new { expectedFingerprint = new string('a', 64) })))
                r.StatusCode.Should().Be(HttpStatusCode.Conflict, "conteúdo diferente do revisado não é publicado");
            using (var r = await manager.PostAsync($"{Base}/{a}/cycles/{c}/scopes/{s}/publications", AegisApiHarness.JsonBody(new { expectedFingerprint = fingerprint })))
            {
                var text = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.Created, text);
                using var doc = JsonDocument.Parse(text);
                snapshotId = doc.RootElement.GetProperty("snapshotId").GetString()!;
            }
            using (var r = await manager.PostAsync("/api/v1/posture/snapshots", AegisApiHarness.JsonBody(new { type = "NistMaturity" })))
                r.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a maturidade só é publicada pela jornada NIST");
        }

        using (var analyst = _api.As(t.Analyst))
        {
            foreach (var (format, type) in new[] { ("html", "text/html"), ("pdf", "application/pdf"), ("csv", "text/csv") })
            {
                using var r = await analyst.GetAsync($"/api/v1/posture/snapshots/{snapshotId}/export?format={format}");
                r.StatusCode.Should().Be(HttpStatusCode.OK, format);
                r.Content.Headers.ContentType!.MediaType.Should().Be(type);
            }
            using (var csv = await analyst.GetAsync($"{Base}/{a}/cycles/{c}/scopes/{s}/working-csv"))
            {
                csv.StatusCode.Should().Be(HttpStatusCode.OK);
                (await csv.Content.ReadAsStringAsync()).Should().Contain("ID.AM-01");
            }
            using var monthly = await GetJsonAsync(analyst, "/api/v1/posture/snapshots/monthly?months=12");
            monthly.RootElement.GetProperty("series").EnumerateArray().Select(x => x.GetProperty("type").GetString())
                .Should().Contain("NistMaturity", "a maturidade entra na evolução mensal, em série própria");
            using var audit = await GetJsonAsync(analyst, $"{Base}/{a}/audit?cycleId={c}");
            audit.RootElement.EnumerateArray().Select(e => e.GetProperty("subject").GetString())
                .Should().Contain(new[] { "Evaluation", "Assignment", "Review", "Procedure", "Finding", "Plan", "Publication" });
        }

        using (var outsider = _api.As(other.Manager))
        {
            foreach (var url in new[]
                     {
                         $"{Base}/{a}/findings", $"{Base}/{a}/audit", $"{Base}/{a}/publications", $"{Base}/{a}/cycles/{c}/scopes/{s}/profile",
                         $"{Base}/{a}/cycles/{c}/scopes/{s}/findings/{findingId}", $"{Base}/{a}/cycles/{c}/scopes/{s}/working-csv",
                         $"/api/v1/posture/snapshots/{snapshotId}/export?format=html",
                     })
            {
                using var r = await outsider.GetAsync(url);
                r.StatusCode.Should().Be(HttpStatusCode.NotFound, url);
            }
            using (var r = await outsider.PutAsync($"{sub}/assignment", AegisApiHarness.JsonBody(new { assessorUserId = analystId, expectedVersion = 3 })))
                r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// [AEGIS-NIST-JOURNEY-02] A superfície LEGADA <c>api/v1/assessments</c> não nomeia a rodada nem recebe a versão-base do
    /// cliente: gravava níveis e carimbo de revisão direto, inclusive em rodada ENCERRADA, sem autoria nem trilha. As escritas
    /// dela são recusadas (410) com a rota da jornada; a leitura do consolidado continua. A gravação normal pela jornada, em
    /// rodada aberta, segue funcionando.
    /// </summary>
    [Fact]
    public async Task RotaLegada_NaoGravaNemEmRodadaEncerrada_EAJornadaSegueGravandoEmRodadaAberta()
    {
        if (_api is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var t = await _api.SeedTenantAsync("Cliente Rota Legada");
        const string Legacy = "/api/v1/assessments";
        using var manager = _api.As(t.Manager);

        string a, c, s;
        int cycleVersion;
        using (var r = await manager.PostAsync(Base, AegisApiHarness.JsonBody(new { name = "Avaliação legada", initialScopeName = "Matriz" })))
        {
            var text = await r.Content.ReadAsStringAsync();
            r.StatusCode.Should().Be(HttpStatusCode.Created, text);
            using var doc = JsonDocument.Parse(text);
            a = doc.RootElement.GetProperty("id").GetString()!;
            s = doc.RootElement.GetProperty("scopes")[0].GetProperty("id").GetString()!;
            var cycle = doc.RootElement.GetProperty("cycles")[0];
            c = cycle.GetProperty("id").GetString()!;
            cycleVersion = cycle.GetProperty("version").GetInt32();
        }
        var sub = $"{Base}/{a}/cycles/{c}/scopes/{s}/subcategories/ID.AM-01";
        var legacySub = $"{Legacy}/scopes/{s}/evaluations/ID.AM-01";

        using (var r = await manager.PutAsync(sub, AegisApiHarness.JsonBody(new { currentLevel = 2, targetLevel = 4, expectedVersion = 0 })))
            r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());

        async Task<(int Version, int Current, int Target, int Audit)> StateAsync()
        {
            using var d = await GetJsonAsync(manager, sub);
            var e = d.RootElement.GetProperty("evaluation");
            using var audit = await GetJsonAsync(manager, $"{Base}/{a}/audit?cycleId={c}");
            return (e.GetProperty("version").GetInt32(), e.GetProperty("currentLevel").GetInt32(), e.GetProperty("targetLevel").GetInt32(),
                audit.RootElement.GetArrayLength());
        }

        async Task SetCycleAsync(string status)
        {
            using var r = await manager.PutAsync($"{Base}/{a}/cycles/{c}/status", AegisApiHarness.JsonBody(new { status, expectedVersion = cycleVersion }));
            var text = await r.Content.ReadAsStringAsync();
            r.StatusCode.Should().Be(HttpStatusCode.OK, text);
            using var doc = JsonDocument.Parse(text);
            cycleVersion = doc.RootElement.GetProperty("version").GetInt32();
        }

        // Rodada ENCERRADA: a rota legada gravava níveis, carimbo de revisão e versão sem trilha.
        await SetCycleAsync("Closed");
        var closed = await StateAsync();
        using (var r = await manager.PutAsync(legacySub, AegisApiHarness.JsonBody(new { currentLevel = 5, targetLevel = 5, currentComments = "Alterado por fora." })))
        {
            var text = await r.Content.ReadAsStringAsync();
            using (new FluentAssertions.Execution.AssertionScope())
            {
                (await StateAsync()).Should().Be(closed, "rodada encerrada não muda por nenhum caminho");
                r.StatusCode.Should().Be(HttpStatusCode.Gone, text);
                if (r.StatusCode == HttpStatusCode.Gone)
                {
                    using var problem = JsonDocument.Parse(text);
                    problem.RootElement.GetProperty("detail").GetString().Should()
                        .Contain("/api/v1/nist/assessments/{avaliação}/cycles/{rodada}/scopes/{escopo}/subcategories/{código}").And.Contain("expectedVersion");
                }
            }
        }
        using (var r = await manager.PutAsync(sub, AegisApiHarness.JsonBody(new { currentLevel = 5, targetLevel = 5, expectedVersion = closed.Version })))
        {
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a jornada recusa rodada encerrada");
            (await r.Content.ReadAsStringAsync()).Should().Contain("encerrada");
        }

        // Rodada ABERTA: a rota legada continua sem gravar — não tem como conferir a versão-base do cliente nem nomear a rodada.
        await SetCycleAsync("Open");
        var open = await StateAsync();
        using (var r = await manager.PutAsync(legacySub, AegisApiHarness.JsonBody(new { currentLevel = 3, targetLevel = 4 })))
        {
            r.StatusCode.Should().Be(HttpStatusCode.Gone, await r.Content.ReadAsStringAsync());
            (await StateAsync()).Should().Be(open);
        }

        // Caso normal preservado: rota explícita, rodada aberta, versão-base do cliente; autoria e trilha registradas.
        using (var r = await manager.PutAsync(sub, AegisApiHarness.JsonBody(new { currentLevel = 3, targetLevel = 4, expectedVersion = open.Version })))
            r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var saved = await StateAsync();
        (saved.Version, saved.Current, saved.Target).Should().Be((open.Version + 1, 3, 4));
        saved.Audit.Should().Be(open.Audit + 1);

        // As demais escritas legadas (avaliação e escopo sem trilha) também são recusadas; nada é criado.
        using (var r = await manager.PostAsync(Legacy, AegisApiHarness.JsonBody(new { name = "Por fora" })))
            r.StatusCode.Should().Be(HttpStatusCode.Gone, await r.Content.ReadAsStringAsync());
        using (var r = await manager.PostAsync($"{Legacy}/{a}/scopes",
                   AegisApiHarness.JsonBody(new { businessProcessId = Guid.NewGuid(), businessUnitId = Guid.NewGuid() })))
            r.StatusCode.Should().Be(HttpStatusCode.Gone, await r.Content.ReadAsStringAsync());
        using (var list = await GetJsonAsync(manager, Base))
        {
            list.RootElement.GetArrayLength().Should().Be(1);
            list.RootElement[0].GetProperty("scopes").GetArrayLength().Should().Be(1);
        }

        // Leitura legada preservada.
        using (var m = await GetJsonAsync(manager, $"{Legacy}/{a}/maturity"))
            m.RootElement.GetProperty("overall").GetProperty("currentScore").GetDouble().Should().Be(3);

        // Papel conferido antes da recusa: Analyst continua 403.
        using (var analyst = _api.As(t.Analyst))
        using (var r = await analyst.PutAsync(legacySub, AegisApiHarness.JsonBody(new { currentLevel = 1 })))
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url);
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text);
    }
}
