using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Integration;

/// <summary>
/// [AEGIS-KNIGHT-DURABLE-01] A restrição que a tela já adotava — execução não finalizada não origina plano nem
/// serve de evidência de validação automática — é da API, não só da interface. Verificada pelo PIPELINE HTTP
/// REAL (JWT, <c>TenantConsistencyMiddleware</c>, controller, tratamento de erro) sobre PostgreSQL real,
/// chamando a API diretamente, como um cliente que ignora a tela.
///
/// As execuções não finalizadas são produzidas rebaixando uma avaliação de demonstração à forma das encontradas
/// no ambiente real (<c>Running</c>, sem <c>CompletedAt</c>, sem narrativa) — com vereditos gravados, que é
/// justamente o que tornaria a recusa tentadora de pular. Nenhuma é reparada.
/// </summary>
public sealed class KnightUnfinishedRunPlanHttpTests : IClassFixture<AegisApiFixture>
{
    private const string Indicator = "AK-ENTRA-001";
    private const string Plans = "/api/v1/remediation/action-plans";

    private readonly AegisApiHarness? _api;
    private readonly ITestOutputHelper _output;

    public KnightUnfinishedRunPlanHttpTests(AegisApiFixture fixture, ITestOutputHelper output)
    {
        _api = fixture.Api;
        _output = output;
    }

    [Fact]
    public async Task CriarPlano_DeExecucaoNaoFinalizada_EhRecusado_SemEscrita_EDeConcluidaContinuaAceito()
    {
        if (_api is null) return;   // AEGIS_TEST_PG ausente — pulado honestamente
        var t = await _api.SeedTenantAsync("Criacao nao finalizada");
        using var manager = _api.As(t.Manager);

        foreach (var status in new[] { KnightRunStatus.Running, KnightRunStatus.Pending, KnightRunStatus.Failed })
        {
            var run = await DemoAsync(manager);
            await DegradeAsync(t.Id, run, status);
            var antes = await WritesAsync(t.Id);

            using var r = await manager.PostAsync(Plans, NovoPlano(run, $"Plano a partir de {status}"));
            var corpo = await r.Content.ReadAsStringAsync();
            _output.WriteLine($"{status}: {(int)r.StatusCode} {corpo}");

            r.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"execução {status} não origina plano");
            corpo.Should().Contain("não foi finalizada");
            (await WritesAsync(t.Id)).Should().Be(antes, "a recusa acontece antes de gravar plano ou trilha");
        }

        // Fluxo válido: a avaliação CONCLUÍDA continua originando plano.
        var concluida = await DemoAsync(manager);
        using var ok = await manager.PostAsync(Plans, NovoPlano(concluida, "Plano da avaliação concluída"));
        ok.StatusCode.Should().Be(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync());
        (await WritesAsync(t.Id)).Plans.Should().Be(1);
    }

    [Fact]
    public async Task ValidarComExecucaoNaoFinalizada_EhRecusado_SemEscrita_EComConcluidaContinuaAceito()
    {
        if (_api is null) return;
        var t0 = _api.OpenTimeWindow();
        var t = await _api.SeedTenantAsync("Validacao nao finalizada");
        using var manager = _api.As(t.Manager);

        var origem = await DemoAsync(manager);
        var plano = await PostJsonAsync(manager, Plans, NovoPlano(origem, "Corrigir"), HttpStatusCode.Created);
        var planoId = plano.GetProperty("id").GetGuid();

        _api.Clock.SetUtcNow(t0.AddHours(1));
        var executado = await PostJsonAsync(manager, $"{Plans}/{planoId}/execution",
            AegisApiHarness.JsonBody(new
            {
                expectedVersion = plano.GetProperty("version").GetInt32(),
                notes = "Segundo fator registrado nas contas administrativas.",
                evidenceReference = "CHAMADO-EXEMPLO-1",
            }), HttpStatusCode.OK);
        var versao = executado.GetProperty("version").GetInt32();

        _api.Clock.SetUtcNow(t0.AddHours(2));
        var naoFinalizada = await DemoAsync(manager);
        await DegradeAsync(t.Id, naoFinalizada, KnightRunStatus.Running);
        var antes = await WritesAsync(t.Id);

        using (var r = await manager.PostAsync($"{Plans}/{planoId}/validations",
                   AegisApiHarness.JsonBody(new { expectedVersion = versao, validationRunId = naoFinalizada })))
        {
            var corpo = await r.Content.ReadAsStringAsync();
            _output.WriteLine($"validação recusada: {(int)r.StatusCode} {corpo}");
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            corpo.Should().Contain("não foi finalizada");
        }

        (await WritesAsync(t.Id)).Should().Be(antes, "a recusa não grava validação, trilha nem versão");
        var lido = await GetJsonAsync(manager, $"{Plans}/{planoId}");
        lido.GetProperty("version").GetInt32().Should().Be(versao);
        lido.GetProperty("latestValidation").ValueKind.Should().Be(JsonValueKind.Null);

        // Fluxo válido: uma avaliação CONCLUÍDA continua servindo de evidência, com a MESMA versão.
        _api.Clock.SetUtcNow(t0.AddHours(3));
        var concluida = await DemoAsync(manager);
        var validado = await PostJsonAsync(manager, $"{Plans}/{planoId}/validations",
            AegisApiHarness.JsonBody(new { expectedVersion = versao, validationRunId = concluida }), HttpStatusCode.OK);
        validado.GetProperty("latestValidation").GetProperty("validationRunId").GetGuid().Should().Be(concluida);
        (await WritesAsync(t.Id)).Validations.Should().Be(antes.Validations + 1);

        // A atestação humana não depende de execução alguma e continua disponível.
        _api.Clock.SetUtcNow(t0.AddHours(4));
        var atestado = await PostJsonAsync(manager, $"{Plans}/{planoId}/validations",
            AegisApiHarness.JsonBody(new
            {
                expectedVersion = validado.GetProperty("version").GetInt32(),
                evidenceReference = "CHAMADO-EXEMPLO-2",
            }), HttpStatusCode.OK);
        atestado.GetProperty("latestValidation").GetProperty("method").GetString().Should().Be("HumanEvidence");
    }

    /// <summary>
    /// Sem invalidação retroativa: um plano que JÁ EXISTE e cuja origem não está finalizada (a forma de um plano
    /// criado antes desta regra) continua legível, com a trilha e a validação anteriores, e continua validável
    /// com uma avaliação concluída. A regra só recusa NOVAS criações e NOVAS evidências.
    /// </summary>
    [Fact]
    public async Task PlanoExistente_ComOrigemNaoFinalizada_ContinuaLegivel_EValidavelComConcluida()
    {
        if (_api is null) return;
        var t0 = _api.OpenTimeWindow();
        var t = await _api.SeedTenantAsync("Plano legado");
        using var manager = _api.As(t.Manager);
        using var analyst = _api.As(t.Analyst);

        var origem = await DemoAsync(manager);
        var plano = await PostJsonAsync(manager, Plans, NovoPlano(origem, "Plano anterior à regra"), HttpStatusCode.Created);
        var planoId = plano.GetProperty("id").GetGuid();

        var atestado = await PostJsonAsync(manager, $"{Plans}/{planoId}/validations",
            AegisApiHarness.JsonBody(new
            {
                expectedVersion = plano.GetProperty("version").GetInt32(),
                evidenceReference = "CHAMADO-EXEMPLO-3",
            }), HttpStatusCode.OK);

        // A origem passa à forma legada DEPOIS de o plano existir — nada no plano é reparado nem apagado.
        await DegradeAsync(t.Id, origem, KnightRunStatus.Running);

        var lido = await GetJsonAsync(analyst, $"{Plans}/{planoId}");
        lido.GetProperty("originRunId").GetGuid().Should().Be(origem);
        lido.GetProperty("latestValidation").GetProperty("method").GetString().Should().Be("HumanEvidence");
        lido.GetProperty("events").GetArrayLength().Should().BeGreaterThan(1);

        _api.Clock.SetUtcNow(t0.AddHours(1));
        var concluida = await DemoAsync(manager);
        var validado = await PostJsonAsync(manager, $"{Plans}/{planoId}/validations",
            AegisApiHarness.JsonBody(new
            {
                expectedVersion = atestado.GetProperty("version").GetInt32(),
                validationRunId = concluida,
            }), HttpStatusCode.OK);
        validado.GetProperty("latestValidation").GetProperty("validationRunId").GetGuid().Should().Be(concluida);
        validado.GetProperty("originRunId").GetGuid().Should().Be(origem, "a origem congelada não muda");
    }

    // ---- infraestrutura ---------------------------------------------------------------------------

    private sealed record Writes(int Plans, int Events, int Validations, int VersionSum);

    private async Task<Writes> WritesAsync(Guid tenantId)
    {
        await using var db = new AegisScoreDbContext(_api!.DbOptions(), new SystemTenantContext(tenantId));
        return new Writes(
            await db.ActionPlans.CountAsync(),
            await db.ActionPlanEvents.CountAsync(),
            await db.ActionPlanValidations.CountAsync(),
            await db.ActionPlans.SumAsync(p => p.Version));
    }

    /// <summary>Rebaixa uma avaliação à forma das execuções não finalizadas do ambiente real — vereditos preservados.</summary>
    private async Task DegradeAsync(Guid tenantId, Guid runId, KnightRunStatus status)
    {
        await using var db = new AegisScoreDbContext(_api!.DbOptions(), new SystemTenantContext(tenantId));
        var run = await db.KnightAssessmentRuns.SingleAsync(r => r.Id == runId);
        run.Status = status;
        run.CompletedAt = null;
        run.AdvisoryJson = null;
        await db.SaveChangesAsync();
        (await db.KnightIndicatorResults.CountAsync(i => i.RunId == runId && i.IndicatorId == Indicator))
            .Should().Be(1, "o achado continua gravado na execução não finalizada");
    }

    private async Task<Guid> DemoAsync(HttpClient manager) =>
        (await PostJsonAsync(manager, "/api/v1/knight/assessments/demo", null, HttpStatusCode.OK))
            .GetProperty("id").GetGuid();

    private static HttpContent NovoPlano(Guid runId, string titulo) =>
        AegisApiHarness.JsonBody(new
        {
            runId,
            indicatorId = Indicator,
            title = titulo,
            proposedAction = "Registrar método resistente a phishing.",
            responsibleArea = "TI",
        });

    private async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        return await ReadAsync(response, HttpStatusCode.OK, "GET " + url);
    }

    private async Task<JsonElement> PostJsonAsync(
        HttpClient client, string url, HttpContent? body, HttpStatusCode esperado)
    {
        using var response = await client.PostAsync(url, body);
        return await ReadAsync(response, esperado, "POST " + url);
    }

    private async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode esperado, string rotulo)
    {
        var corpo = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != esperado) _output.WriteLine($"{rotulo} -> {(int)response.StatusCode}: {corpo}");
        response.StatusCode.Should().Be(esperado, $"{rotulo} respondeu {(int)response.StatusCode}: {corpo}");
        using var doc = JsonDocument.Parse(corpo);
        return doc.RootElement.Clone();
    }
}
