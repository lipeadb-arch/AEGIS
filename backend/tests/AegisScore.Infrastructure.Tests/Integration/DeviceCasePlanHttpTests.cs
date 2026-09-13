using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Connectors;   // SyntheticDeviceSources
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Integration;

/// <summary>
/// [AEGIS-JOURNEY-01] A jornada da prioridade por dispositivo ao acompanhamento da correção, atravessada pelo PIPELINE
/// HTTP REAL (JWT real, papéis reais, tenant pelo claim) sobre PostgreSQL real preparado pelo migrator: visão geral →
/// fila e detalhe do dispositivo → plano do caso (contexto obtido pelo servidor) → edição → execução → validação →
/// acompanhamento da situação na fonte. E o que só o PostgreSQL decide: duas criações simultâneas do mesmo caso
/// desempatadas pelo índice único parcial.
///
/// LIMITES declarados: as fontes são SINTÉTICAS (conectores reais do Defender e do Intune sobre HTTP sintético, gravando
/// direto no banco descartável do harness); isto NÃO é homologação de tenant Microsoft nem validação de interface.
/// </summary>
public sealed class DeviceCasePlanHttpTests : IClassFixture<AegisApiFixture>
{
    private const string CveCritica = "CVE-2024-3001";
    private const string CveMedia = "CVE-2024-3002";
    private const string Base = "/api/v1/remediation/action-plans";

    private readonly ITestOutputHelper _output;
    private readonly AegisApiHarness? _api;

    public DeviceCasePlanHttpTests(AegisApiFixture fixture, ITestOutputHelper output)
    {
        _api = fixture.Api;
        _output = output;
    }

    // ---- (1) Jornada completa por HTTP ---------------------------------------------------------------------------------

    [Fact]
    public async Task Jornada_DaVisaoGeralAoAcompanhamento_ComContextoDoServidor_ESemEfeitoTecnico()
    {
        if (_api is null) return;   // AEGIS_TEST_PG ausente — pulado honestamente
        var (t, a, _) = await CenarioAsync("Cliente Jornada Dispositivos");
        using var analyst = _api.As(t.Analyst);
        using var manager = _api.As(t.Manager);

        // (a) VISÃO GERAL: a mesma autoridade da Central, com dispositivos, casos e planos em unidades separadas.
        var dp = (await GetOkAsync(analyst, "/api/v1/dashboard/overview")).GetProperty("devicePriority");
        dp.GetProperty("state").GetString().Should().Be("Available");
        dp.GetProperty("policyCode").GetString().Should().Be("AEGIS-PRIO-DEV");
        dp.GetProperty("candidateAssets").GetInt32().Should().Be(2, "unidade: dispositivos");
        Count(dp.GetProperty("assetsByBand"), "p1").Should().Be(2);
        Count(dp.GetProperty("casesByBand"), "p1").Should().Be(2, "unidade: casos dispositivo × CVE");
        Count(dp.GetProperty("casesByBand"), "p3").Should().Be(1);
        dp.GetProperty("casesPartial").GetBoolean().Should().BeFalse();
        var topo = dp.GetProperty("top").EnumerateArray().Single(i => i.GetProperty("assetId").GetGuid() == a);
        topo.GetProperty("cveId").GetString().Should().Be(CveCritica);
        topo.GetProperty("activePlanId").ValueKind.Should().Be(JsonValueKind.Null);
        dp.GetProperty("plans").GetProperty("active").GetInt32().Should().Be(0, "unidade: planos");

        // (b) INVESTIGAÇÃO: fila e detalhe do dispositivo.
        var fila = await GetOkAsync(analyst, "/api/v1/priorities/devices?band=p1");
        fila.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("assetId").GetGuid()).Should().Contain(a);
        var detalhe = await GetOkAsync(analyst, $"/api/v1/assets/{a}/priority");
        detalhe.GetProperty("band").GetString().Should().Be("p1");
        detalhe.GetProperty("determiningCase").GetProperty("cveId").GetString().Should().Be(CveCritica);

        // (c) CRIAÇÃO: o corpo tenta impor faixa, motivo, fatores e vínculos — nada disso é aceito como evidência.
        var inventadoThreat = Guid.NewGuid();
        var corpo = AegisApiHarness.JsonBody(new
        {
            assetId = a,
            cveId = "cve-2024-3001",
            title = "Aplicar a atualização do fornecedor",
            proposedAction = "Aplicar a atualização indicada pela fonte e confirmar numa nova coleta.",
            responsiblePerson = "Equipe de Estações",
            responsibleArea = "TI",
            dueDate = Prazo(30),
            band = "p4",
            caseBand = "p4",
            positionReason = "Motivo inventado pelo navegador",
            policyVersion = 99,
            factors = new[] { new { code = "technicalSeverity", value = "Baixa" } },
            threatId = inventadoThreat,
            exposureId = Guid.NewGuid(),
        });
        var criado = await PostOkAsync(manager, $"{Base}/device-cases", corpo, HttpStatusCode.Created);
        var planoId = criado.GetProperty("id").GetGuid();
        criado.GetProperty("originKind").GetString().Should().Be("DeviceVulnerability");
        criado.GetProperty("knightIndicatorId").ValueKind.Should().Be(JsonValueKind.Null);
        var origem = criado.GetProperty("deviceOrigin");
        origem.GetProperty("cveId").GetString().Should().Be(CveCritica);
        origem.GetProperty("caseBand").GetString().Should().Be("p1", "a faixa vem do servidor, não do corpo");
        origem.GetProperty("policyVersion").GetInt32().Should().Be(1);
        origem.GetProperty("devicePositionReason").GetString().Should().NotContain("inventado");
        origem.GetProperty("factors").EnumerateArray()
            .Single(f => f.GetProperty("code").GetString() == "technicalSeverity")
            .GetProperty("value").GetString().Should().Be("Crítica (CVSS 9.8)");

        await using (var db = new AegisScoreDbContext(_api.DbOptions(), new SystemTenantContext(t.Id)))
        {
            var linha = await db.ActionPlans.AsNoTracking().SingleAsync(p => p.Id == planoId);
            var threat = await db.Threats.AsNoTracking().SingleAsync(x => x.Code == CveCritica);
            linha.OriginThreatId.Should().Be(threat.Id).And.NotBe(inventadoThreat);
            (await db.AssetThreatExposures.AsNoTracking().SingleAsync(x => x.Id == linha.OriginExposureId))
                .AssetId.Should().Be(a);
        }

        // Segundo clique: 409 com o plano existente, para a tela abri-lo.
        using (var duplicado = await manager.PostAsync($"{Base}/device-cases", AegisApiHarness.JsonBody(new
               {
                   assetId = a, cveId = CveCritica, title = "Segundo clique",
               })))
        {
            duplicado.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await JsonOf(duplicado)).GetProperty("existingActionPlanId").GetGuid().Should().Be(planoId);
        }

        // A visão geral passa a apontar o plano do caso determinante.
        dp = (await GetOkAsync(analyst, "/api/v1/dashboard/overview")).GetProperty("devicePriority");
        dp.GetProperty("top").EnumerateArray().Single(i => i.GetProperty("assetId").GetGuid() == a)
            .GetProperty("activePlanId").GetGuid().Should().Be(planoId);
        dp.GetProperty("plans").GetProperty("active").GetInt32().Should().Be(1);

        var tecnicoAntes = await EstadoTecnicoAsync(t.Id);

        // (d) EDIÇÃO, EXECUÇÃO, VALIDAÇÃO (KNIGHT recusada; atestação aceita) e CONCLUSÃO.
        _api.Clock.Advance(TimeSpan.FromMinutes(5));
        var editado = await PutOkAsync(manager, $"{Base}/{planoId}", AegisApiHarness.JsonBody(new
        {
            expectedVersion = criado.GetProperty("version").GetInt32(),
            responsiblePerson = "Ana Souza",
            dueDate = Prazo(20),
        }));
        editado.GetProperty("responsiblePerson").GetString().Should().Be("Ana Souza");

        _api.Clock.Advance(TimeSpan.FromMinutes(5));
        var executado = await PostOkAsync(manager, $"{Base}/{planoId}/execution", AegisApiHarness.JsonBody(new
        {
            expectedVersion = editado.GetProperty("version").GetInt32(),
            notes = "Atualização do fornecedor aplicada no dispositivo.",
            evidenceReference = "CHG-0001",
        }));
        executado.GetProperty("status").GetString().Should().Be("AguardandoValidacao");

        _api.Clock.Advance(TimeSpan.FromMinutes(5));
        using (var knight = await manager.PostAsync($"{Base}/{planoId}/validations", AegisApiHarness.JsonBody(new
               {
                   expectedVersion = executado.GetProperty("version").GetInt32(),
                   validationRunId = Guid.NewGuid(),
               })))
        {
            knight.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a comparação KNIGHT não valida CVE — recusa da API");
            (await knight.Content.ReadAsStringAsync()).Should().Contain("não se aplica");
        }

        var atestado = await PostOkAsync(manager, $"{Base}/{planoId}/validations", AegisApiHarness.JsonBody(new
        {
            expectedVersion = executado.GetProperty("version").GetInt32(),
            evidenceReference = "CHG-0001 · relatório do fornecedor",
        }));
        atestado.GetProperty("applicableValidation").GetProperty("method").GetString().Should().Be("HumanEvidence");

        _api.Clock.Advance(TimeSpan.FromMinutes(5));
        var concluido = await PutOkAsync(manager, $"{Base}/{planoId}", AegisApiHarness.JsonBody(new
        {
            expectedVersion = atestado.GetProperty("version").GetInt32(),
            status = "concluido",
        }));
        concluido.GetProperty("status").GetString().Should().Be("Concluido");

        // (e) ACOMPANHAMENTO: situação na fonte, separada do plano; nada técnico mudou.
        var leitura = await GetOkAsync(analyst, $"{Base}/{planoId}/source-reading");
        leitura.GetProperty("state").GetString().Should().Be("open", "plano concluído ≠ ambiente corrigido");
        leitura.GetProperty("case").GetProperty("band").GetString().Should().Be("p1");
        leitura.GetProperty("verificationNote").GetString().Should().Contain("pendente");
        (await GetOkAsync(analyst, $"/api/v1/assets/{a}/priority")).GetProperty("band").GetString().Should().Be("p1");
        (await EstadoTecnicoAsync(t.Id)).Should().Be(tecnicoAntes,
            "criar, executar, validar e concluir não alteram exposição, disposição, observação, ativo nem fonte");

        // Lista: a origem nova aparece no recorte próprio; o recorte padrão continua o de antes.
        (await GetOkAsync(analyst, $"{Base}?origin=device&assetId={a}")).EnumerateArray()
            .Select(p => p.GetProperty("id").GetGuid()).Should().Contain(planoId);
        (await GetOkAsync(analyst, Base)).EnumerateArray()
            .Select(p => p.GetProperty("id").GetGuid()).Should().NotContain(planoId);
    }

    // ---- (2) Papéis e tenants ----------------------------------------------------------------------------------------

    [Fact]
    public async Task NovaOrigem_UsaAMesmaMatrizDePapeis_EOutroTenantNaoVeNada()
    {
        if (_api is null) return;
        var (t, a, _) = await CenarioAsync("Cliente Papeis Dispositivos");
        var beta = await _api.SeedTenantAsync("Cliente Beta Dispositivos");
        using var analyst = _api.As(t.Analyst);
        using var admin = _api.As(t.Admin);
        using var intruso = _api.As(beta.Manager);

        (await analyst.PostAsync($"{Base}/device-cases", AegisApiHarness.JsonBody(new
        {
            assetId = a, cveId = CveCritica, title = "Tentativa do Analyst",
        }))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "Analyst acompanha, não cria");

        var criado = await PostOkAsync(admin, $"{Base}/device-cases", AegisApiHarness.JsonBody(new
        {
            assetId = a, cveId = CveMedia, title = "Revisar a CVE média",
        }), HttpStatusCode.Created);
        var planoId = criado.GetProperty("id").GetGuid();

        (await analyst.GetAsync($"{Base}?origin=device")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await analyst.GetAsync($"{Base}/{planoId}/source-reading")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await analyst.PostAsync($"{Base}/{planoId}/execution", AegisApiHarness.JsonBody(new
        {
            expectedVersion = criado.GetProperty("version").GetInt32(), notes = "Analyst",
        }))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Outro tenant: o plano, a leitura da fonte e o ativo são inexistentes.
        (await intruso.GetAsync($"{Base}/{planoId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await intruso.GetAsync($"{Base}/{planoId}/source-reading")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await intruso.PostAsync($"{Base}/device-cases", AegisApiHarness.JsonBody(new
        {
            assetId = a, cveId = CveCritica, title = "Tentativa cruzada",
        }))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetOkAsync(intruso, $"{Base}?origin=all")).GetArrayLength().Should().Be(0);
        (await GetOkAsync(intruso, "/api/v1/dashboard/overview"))
            .GetProperty("devicePriority").GetProperty("plans").GetProperty("active").GetInt32().Should().Be(0);
    }

    // ---- (3) Concorrência decidida pelo banco --------------------------------------------------------------------------

    [Fact]
    public async Task CriacoesSimultaneasDoMesmoCaso_UmUnicoPlanoAtivo_EAsDemaisAbremOVencedor()
    {
        if (_api is null) return;
        var (t, a, _) = await CenarioAsync("Cliente Concorrência Dispositivos");
        using var manager = _api.As(t.Manager);

        var respostas = await Task.WhenAll(Enumerable.Range(1, 6).Select(async n =>
        {
            using var r = await manager.PostAsync($"{Base}/device-cases", AegisApiHarness.JsonBody(new
            {
                assetId = a, cveId = CveCritica, title = $"Clique simultâneo {n}",
            }));
            return (r.StatusCode, Body: await JsonOf(r));
        }));

        respostas.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1, "o banco desempata: um plano ativo por caso");
        respostas.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(5);
        var vencedor = respostas.Single(r => r.StatusCode == HttpStatusCode.Created).Body.GetProperty("id").GetGuid();
        respostas.Where(r => r.StatusCode == HttpStatusCode.Conflict)
            .Should().OnlyContain(r => r.Body.GetProperty("existingActionPlanId").GetGuid() == vencedor,
                "cada clique perdedor recebe o plano que venceu");

        await using var db = new AegisScoreDbContext(_api.DbOptions(), new SystemTenantContext(t.Id));
        (await db.ActionPlans.AsNoTracking().CountAsync(p =>
                p.OriginKind == ActionPlanOriginKind.DeviceVulnerability && p.OriginAssetId == a && p.OriginCveId == CveCritica))
            .Should().Be(1);
    }

    // ---- apoio ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Tenant sintético com as duas fontes coletadas pelos conectores REAIS sobre HTTP sintético, direto no banco do
    /// harness: dispositivo A (vinculado ao Intune não conforme e sem criptografia) com uma CVE crítica com exploit público
    /// e uma média; dispositivo B (só Defender) com a mesma CVE crítica. O relógio da API é alinhado ao instante real,
    /// porque o Defender marca a aquisição com ele — e só avança.
    /// </summary>
    private async Task<(SeededTenant T, Guid AssetA, Guid AssetB)> CenarioAsync(string label)
    {
        var t = await _api!.SeedTenantAsync(label);
        var agora = DeviceSnapshotMarker.Normalize(DateTimeOffset.UtcNow);
        var opt = _api.DbOptions();

        Guid defender, intune;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(t.Id)))
        {
            var d = SyntheticDeviceSources.Connector(t.Id, ConnectorCapability.VulnerabilityScanner, SyntheticDeviceSources.DirA);
            var i = SyntheticDeviceSources.Connector(t.Id, ConnectorCapability.ConfigAnalyzer, SyntheticDeviceSources.DirA);
            db.Connectors.AddRange(d, i);
            await db.SaveChangesAsync();
            (defender, intune) = (d.Id, i.Id);
        }

        string Q(string s) => SyntheticDeviceSources.Q(s);
        string Maquina(string id, string dns, string? aad) =>
            "{\"id\":" + Q(id) + ",\"osPlatform\":\"Windows11\",\"lastSeen\":" + Q(agora.AddHours(-1).ToString("O")) +
            ",\"computerDnsName\":" + Q(dns) + (aad is null ? "" : ",\"aadDeviceId\":" + Q(aad)) + "}";
        string Cve(string id, string severity, double cvss, bool publicExploit) =>
            "{\"id\":" + Q(id) + ",\"name\":" + Q(id) + ",\"severity\":" + Q(severity) + ",\"cvssV3\":" +
            cvss.ToString(CultureInfo.InvariantCulture) + (publicExploit ? ",\"publicExploit\":true" : "") + "}";

        var fonte = new SyntheticDeviceSources
        {
            IntuneNow = agora,
            DefenderMachines = SyntheticDeviceSources.Page(
                Maquina("mde-a", "pc-a.demo.example.com", SyntheticDeviceSources.DevX),
                Maquina("mde-b", "pc-b.demo.example.com", null)),
            DefenderRelations = SyntheticDeviceSources.Page(
                SyntheticDeviceSources.Relation("mde-a", CveCritica), SyntheticDeviceSources.Relation("mde-a", CveMedia),
                SyntheticDeviceSources.Relation("mde-b", CveCritica)),
            DefenderCves = SyntheticDeviceSources.Page(Cve(CveCritica, "Critical", 9.8, true), Cve(CveMedia, "Medium", 5.5, false)),
            IntuneDevices = SyntheticDeviceSources.Page(
                "{\"id\":\"int-a\",\"complianceState\":\"noncompliant\",\"operatingSystem\":\"Windows\",\"lastSyncDateTime\":" +
                Q(agora.AddHours(-1).ToString("O")) + ",\"isEncrypted\":false,\"azureADDeviceId\":" + Q(SyntheticDeviceSources.DevX) + "}"),
        };
        await fonte.SyncAsync(opt, t.Id, defender);
        await fonte.SyncAsync(opt, t.Id, intune);

        Guid a, b;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(t.Id)))
        {
            a = (await db.AssetSourceBindings.AsNoTracking().SingleAsync(x => x.ConnectorConfigId == defender && x.ExternalId == "mde-a")).AssetId;
            b = (await db.AssetSourceBindings.AsNoTracking().SingleAsync(x => x.ConnectorConfigId == defender && x.ExternalId == "mde-b")).AssetId;
        }

        var alvo = agora.AddHours(2);
        if (alvo > _api.Clock.GetUtcNow()) _api.Clock.SetUtcNow(alvo);
        else _api.Clock.Advance(TimeSpan.FromMinutes(1));
        return (t, a, b);
    }

    private static int Count(JsonElement counts, string band) =>
        counts.EnumerateArray().Single(c => c.GetProperty("band").GetString() == band).GetProperty("count").GetInt32();

    /// <summary>Situação técnica do tenant (exposições, disposições, observações, ativos, fontes), ordenada em memória.</summary>
    private async Task<string> EstadoTecnicoAsync(Guid tenant)
    {
        await using var db = new AegisScoreDbContext(_api!.DbOptions(), new SystemTenantContext(tenant));
        var linhas = new List<string>();
        linhas.AddRange((await db.AssetThreatExposures.AsNoTracking().ToListAsync())
            .Select(x => $"X {x.Id} {x.Status} {x.Likelihood} {x.MitigatingSubcategoryCode}"));
        linhas.AddRange((await db.AssetThreatObservations.AsNoTracking().ToListAsync())
            .Select(o => $"O {o.Id} {o.LifecycleState} {o.FirstSeenAt:O} {o.LastSeenAt:O} {o.ResolvedAt:O}"));
        linhas.AddRange((await db.Assets.AsNoTracking().ToListAsync())
            .Select(x => $"A {x.Id} {x.Criticality} {x.CriticalityDeclaredValue} {x.IsActive} {x.RiskScore}"));
        linhas.AddRange((await db.Connectors.AsNoTracking().ToListAsync())
            .Select(c => $"C {c.Id} {c.Enabled} {c.UpdatedAt:O}"));
        return string.Join("\n", linhas.OrderBy(l => l, StringComparer.Ordinal));
    }

    private static string Prazo(int dias) =>
        DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(dias)).ToString("yyyy-MM-dd");

    private async Task<JsonElement> GetOkAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        await EnsureAsync(response, HttpStatusCode.OK, "GET " + url);
        return await JsonOf(response);
    }

    private async Task<JsonElement> PostOkAsync(
        HttpClient client, string url, HttpContent? body, HttpStatusCode esperado = HttpStatusCode.OK)
    {
        using var response = await client.PostAsync(url, body);
        await EnsureAsync(response, esperado, "POST " + url);
        return await JsonOf(response);
    }

    private async Task<JsonElement> PutOkAsync(HttpClient client, string url, HttpContent body)
    {
        using var response = await client.PutAsync(url, body);
        await EnsureAsync(response, HttpStatusCode.OK, "PUT " + url);
        return await JsonOf(response);
    }

    /// <summary>Falha com o CORPO da resposta: um 400 mudo custaria uma rodada inteira de CI para diagnosticar.</summary>
    private async Task EnsureAsync(HttpResponseMessage response, HttpStatusCode esperado, string rotulo)
    {
        if (response.StatusCode == esperado) return;
        var corpo = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"{rotulo} -> {(int)response.StatusCode}: {corpo}");
        response.StatusCode.Should().Be(esperado, $"{rotulo} respondeu {(int)response.StatusCode}: {corpo}");
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        var texto = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(texto)) return default;
        using var doc = JsonDocument.Parse(texto);
        return doc.RootElement.Clone();
    }
}
