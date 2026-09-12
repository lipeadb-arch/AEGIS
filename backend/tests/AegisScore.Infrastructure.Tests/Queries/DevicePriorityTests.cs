using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Api.Contracts;
using AegisScore.Api.Controllers;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Auth;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using AegisScore.Infrastructure.Tests.Connectors;   // SyntheticDeviceSources
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Queries;

/// <summary>
/// [AEGIS-RISK-PRIORITIZATION-01] Prioridade de tratamento em dispositivos pelo CAMINHO REAL: conectores reais do Defender e
/// do Intune sobre HTTP SINTÉTICO → executor de ingestão → resolução por chave forte → banco descartável (SQLite) →
/// consulta → controllers (Central de Prioridades e detalhe do ativo). Os fatos julgados são os que a coleta persistiu;
/// só as disposições humanas, a marca sem origem do catálogo e os desfechos que a coleta não produz aqui são gravados
/// direto no banco (é assim que chegam de fora do fluxo). Tudo é sintético (GUIDs inventados, demo.example.com).
/// </summary>
public sealed class DevicePriorityTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("c5000000-0000-0000-0000-00000000000c");
    private static readonly Guid TenantB = Guid.Parse("d6000000-0000-0000-0000-00000000000d");
    private static readonly Guid Manager = Guid.Parse("a7000000-0000-0000-0000-0000000000a7");

    private const string DirA = SyntheticDeviceSources.DirA;
    private const string DevX = SyntheticDeviceSources.DevX;
    private const string DevY = SyntheticDeviceSources.DevY;
    private const string DevZ = "7a3b6c5d-9f8e-4a01-b2c3-d4e5f6071829";

    private const string RuleA = CrossSourceRuleCodes.VulnerableAndNoncompliant;
    private const string RuleB = CrossSourceRuleCodes.VulnerableAndUnencrypted;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private readonly DateTimeOffset _base;
    private readonly SyntheticDeviceSources _src;

    public DevicePriorityTests()
    {
        _base = DeviceSnapshotMarker.Normalize(DateTimeOffset.UtcNow);
        _src = new SyntheticDeviceSources { IntuneNow = _base };
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        ctx.Tenants.Add(new Tenant { Id = TenantA, Name = "Cliente Sintético E", Slug = "sintetico-e", Status = TenantStatus.Active });
        ctx.Tenants.Add(new Tenant { Id = TenantB, Name = "Cliente Sintético F", Slug = "sintetico-f", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ---- apoio ------------------------------------------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenant) => new(_options, new SystemTenantContext(tenant));

    private Guid Seed(Guid tenant, ConnectorCapability capability)
    {
        using var db = NewContext(tenant);
        var cfg = SyntheticDeviceSources.Connector(tenant, capability, DirA);
        db.Connectors.Add(cfg);
        db.SaveChanges();
        return cfg.Id;
    }

    private Task<PullIngestionResult> Sync(Guid connectorId, Guid? tenant = null,
        Func<string, CancellationToken, Task>? checkpoint = null) =>
        _src.SyncAsync(_options, tenant ?? TenantA, connectorId, checkpoint);

    private static string Q(string s) => SyntheticDeviceSources.Q(s);
    private static string Page(params string[] items) => SyntheticDeviceSources.Page(items);

    private string Machine(string id, string dns, string? deviceId, DateTimeOffset? lastSeen = null) =>
        "{\"id\":" + Q(id) + ",\"osPlatform\":\"Windows11\",\"lastSeen\":" + Q((lastSeen ?? _base.AddHours(-1)).ToString("O")) +
        ",\"computerDnsName\":" + Q(dns) + (deviceId is null ? "" : ",\"aadDeviceId\":" + Q(deviceId)) + "}";

    private string Device(string id, string deviceId, string compliance = "noncompliant", bool? encrypted = false) =>
        "{\"id\":" + Q(id) + ",\"complianceState\":" + Q(compliance) + ",\"operatingSystem\":\"Windows\"" +
        ",\"lastSyncDateTime\":" + Q(_base.AddHours(-1).ToString("O")) +
        (encrypted is null ? "" : ",\"isEncrypted\":" + (encrypted.Value ? "true" : "false")) +
        ",\"azureADDeviceId\":" + Q(deviceId) + "}";

    /// <summary>CVE no formato oficial do Defender; campos nulos são omitidos (a fonte não os informou).</summary>
    private static string Cve(string id, string? severity = "High", double? cvss = 8.1, bool? publicExploit = null,
        bool? exploitVerified = null, double? epss = null, string? vector = null)
    {
        var parts = new List<string> { "\"id\":" + Q(id), "\"name\":" + Q(id) };
        if (severity is not null) parts.Add("\"severity\":" + Q(severity));
        if (cvss is { } c) parts.Add("\"cvssV3\":" + c.ToString(CultureInfo.InvariantCulture));
        if (vector is not null) parts.Add("\"cvssVector\":" + Q(vector));
        if (publicExploit is { } pe) parts.Add("\"publicExploit\":" + (pe ? "true" : "false"));
        if (exploitVerified is { } ev) parts.Add("\"exploitVerified\":" + (ev ? "true" : "false"));
        if (epss is { } e) parts.Add("\"epss\":" + e.ToString(CultureInfo.InvariantCulture));
        return "{" + string.Join(",", parts) + "}";
    }

    private void DefenderData(string[] machines, (string Machine, string Cve)[] relations, params string[] cves)
    {
        _src.DefenderMachines = Page(machines);
        _src.DefenderRelations = Page(relations.Select(r => SyntheticDeviceSources.Relation(r.Machine, r.Cve)).ToArray());
        _src.DefenderCves = Page(cves);
    }

    private async Task<Guid> AssetOf(Guid connectorId, string externalId, Guid? tenant = null)
    {
        await using var db = NewContext(tenant ?? TenantA);
        return (await db.AssetSourceBindings.AsNoTracking()
            .SingleAsync(b => b.ConnectorConfigId == connectorId && b.ExternalId == externalId)).AssetId;
    }

    private DevicePriorityQuery Query(AegisScoreDbContext db, DateTimeOffset? now, int? max = null) =>
        new(db, new FakeTimeProvider(now ?? _base.AddHours(2)), Options.Create(new CrossSourceCorrelationOptions()))
        {
            MaxEvaluatedAssets = max ?? DevicePriorityQuery.DefaultMaxEvaluatedAssets,
        };

    /// <summary>Fila pelo CONTROLLER real da Central.</summary>
    private async Task<ActionResult<DevicePriorityListDto>> ListAction(
        Guid? tenant = null, string? band = null, int page = 1, int pageSize = 10, DateTimeOffset? now = null, int? max = null)
    {
        await using var db = NewContext(tenant ?? TenantA);
        var controller = new PrioritiesController(null!) { ControllerContext = SyntheticDeviceSources.ControllerContextFor("Analyst") };
        return await controller.Devices(Query(db, now, max), band, page, pageSize, CancellationToken.None);
    }

    private async Task<DevicePriorityListDto> List(
        Guid? tenant = null, string? band = null, int page = 1, int pageSize = 10, DateTimeOffset? now = null, int? max = null) =>
        (DevicePriorityListDto)((OkObjectResult)(await ListAction(tenant, band, page, pageSize, now, max)).Result!).Value!;

    /// <summary>Detalhe pelo CONTROLLER real do ativo (papel sem diagnóstico).</summary>
    private async Task<ActionResult<AssetDevicePriorityDto>> DetailAction(
        Guid assetId, Guid? tenant = null, int casePage = 1, int casePageSize = 10, DateTimeOffset? now = null)
    {
        await using var db = NewContext(tenant ?? TenantA);
        var controller = new AssetsController(db, new AssetSourceQuery(db))
        {
            ControllerContext = SyntheticDeviceSources.ControllerContextFor("Analyst"),
        };
        return await controller.Priority(assetId, Query(db, now), casePage, casePageSize, CancellationToken.None);
    }

    private async Task<AssetDevicePriorityDto> Detail(
        Guid assetId, Guid? tenant = null, int casePage = 1, int casePageSize = 10, DateTimeOffset? now = null) =>
        (await DetailAction(assetId, tenant, casePage, casePageSize, now)).Value!;

    private async Task<ActionResult<DevicePriorityCriticalityDto>> Declare(
        Guid assetId, int criticality, string? note = null, Guid? tenant = null, DateTimeOffset? at = null)
    {
        await using var db = NewContext(tenant ?? TenantA);
        var controller = new AssetsController(db, new AssetSourceQuery(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("role", "Manager"), new Claim("name", "Gestora Sintética"),
                        new Claim(JwtTokenService.AccountClaim, Manager.ToString()),
                    }, "sintetico", "name", "role")),
                },
            },
        };
        return await controller.DeclareCriticality(assetId, new DeclareAssetCriticalityRequest(criticality, note),
            new FakeTimeProvider(at ?? _base.AddHours(1)), CancellationToken.None);
    }

    private async Task SetDispositionAsync(Guid assetId, string cve, ExposureStatus status)
    {
        await using var db = NewContext(TenantA);
        var threat = await db.Threats.AsNoTracking().SingleAsync(t => t.Code == cve);
        var e = await db.AssetThreatExposures.SingleAsync(x => x.AssetId == assetId && x.ThreatId == threat.Id);
        e.Status = status;
        await db.SaveChangesAsync();
    }

    private static DevicePriorityFactorDto Factor(AssetDevicePriorityDto d, string code) => d.Factors.Single(f => f.Code == code);

    // ================= Política contextual, empate e fontes ==========================================================

    [Fact]
    public async Task ContextualPriority_ProvenGapRaisesOneBand_MoreSourcesDoNotRaise_AndRealTiesAreStable()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer);
        // Mesma CVE (Alta, sem exploit informado → Prioridade 3) em quatro dispositivos:
        //   A: vinculado, Intune não conforme E sem criptografia (duas situações) → UM agravante → Prioridade 2;
        //   B e C: só Defender (sem identificador de diretório) → Prioridade 3;
        //   D: vinculado, Intune conforme e com criptografia → mais uma fonte, nenhum agravante → Prioridade 3.
        string[] machines =
        {
            Machine("mde-d", "pc-d.demo.example.com", DevY), Machine("mde-c", "pc-c.demo.example.com", null),
            Machine("mde-b", "pc-b.demo.example.com", null), Machine("mde-a", "pc-a.demo.example.com", DevX),
        };
        DefenderData(machines, new[] { ("mde-a", "CVE-2024-2001"), ("mde-b", "CVE-2024-2001"), ("mde-c", "CVE-2024-2001"), ("mde-d", "CVE-2024-2001") },
            Cve("CVE-2024-2001"));
        _src.IntuneDevices = Page(Device("int-a", DevX), Device("int-d", DevY, "compliant", true));
        await Sync(defender);
        await Sync(intune);

        var list = await List();
        list.Heading.Should().Be("Prioridade de tratamento");
        list.Scope.Should().Contain("Não é avaliação completa dos riscos do ambiente");
        list.Policy.Code.Should().Be("AEGIS-PRIO-DEV");
        list.Policy.Version.Should().Be(1);
        list.Items.Select(i => i.AssetName).Should().Equal(
            "pc-a.demo.example.com", "pc-b.demo.example.com", "pc-c.demo.example.com", "pc-d.demo.example.com");
        list.Items.Select(i => i.Band).Should().Equal(DevicePriorityBands.P2, DevicePriorityBands.P3, DevicePriorityBands.P3, DevicePriorityBands.P3);
        list.Items.Select(i => i.Position).Should().Equal(1, 2, 3, 4);

        var a = list.Items[0];
        a.Aggravators.Should().ContainSingle("duas situações no mesmo dispositivo são UM agravante")
            .Which.Should().Contain(RuleA).And.Contain(RuleB);
        a.PositionReason.Should().Contain("determinada por CVE-2024-2001")
            .And.Contain("antecipada de Prioridade 3 para Prioridade 2");
        a.TiedAssets.Should().Be(0);
        a.DeviceContextLabel.Should().Contain("não conforme").And.Contain("sem criptografia");

        // Empate real: mesmos fatores → mesma faixa; a ordem entre eles é só estabilidade (nome, identificador) e é dita.
        list.Items.Skip(1).Should().OnlyContain(i => i.TiedAssets == 2);
        list.Items[1].DeviceContextLabel.Should().StartWith("Sem registro do Microsoft Intune");
        list.Items[3].DeviceContextLabel.Should().Be("O Intune informa o dispositivo como conforme e com criptografia");
        list.Summary.CandidateAssets.Should().Be(4);
        list.Summary.AssetsByBand.Single(b => b.Band == DevicePriorityBands.P2).Count.Should().Be(1);
        list.Summary.AssetsByBand.Single(b => b.Band == DevicePriorityBands.P3).Count.Should().Be(3);
        list.Summary.CasesByBand.Single(b => b.Band == DevicePriorityBands.P3).Count.Should().Be(3);

        // Determinismo: outra leitura, com as fontes devolvendo os registros em outra ordem, dá a mesma fila.
        DefenderData(machines.Reverse().ToArray(),
            new[] { ("mde-d", "CVE-2024-2001"), ("mde-c", "CVE-2024-2001"), ("mde-b", "CVE-2024-2001"), ("mde-a", "CVE-2024-2001") },
            Cve("CVE-2024-2001"));
        await Sync(defender);
        (await List()).Items.Select(i => (i.AssetName, i.Band)).Should().Equal(list.Items.Select(i => (i.AssetName, i.Band)));

        // Detalhe de A: cada fator com valor, natureza, origem, data e efeito.
        var d = await Detail(list.Items[0].AssetId);
        d.Status.Should().Be(DevicePriorityStatuses.Prioritized);
        Factor(d, "deviceManagement").Kind.Should().Be(DevicePriorityFactorKinds.Inferred);
        Factor(d, "deviceManagement").Effect.Should().Be(DevicePriorityEffects.Aggravating);
        Factor(d, "deviceManagement").AvailableAt.Should().NotBeNull("a data da aquisição do Intune que sustenta a situação");
        Factor(d, "technicalSeverity").Value.Should().Be("Alta (CVSS 8.1)");
        Factor(d, "technicalSeverity").Kind.Should().Be(DevicePriorityFactorKinds.SourceFact);
        Factor(d, "exploit").Value.Should().Be("Informação de exploit não fornecida pela fonte");
        Factor(d, "exploit").Kind.Should().Be(DevicePriorityFactorKinds.Unknown);
        d.Situations.Should().HaveCount(2).And.OnlyContain(s => s.State == CrossSourceStates.Identified);
        d.CouldChange.Should().Contain(c => c.Contains("Outro agravante não mudaria"));
        d.Cases!.Total.Should().Be(1);

        var b = await Detail(list.Items[1].AssetId);
        b.CouldChange.Should().Contain(c => c.Contains("Criticidade 3 ou 4")).And.Contain(c => c.Contains("Intune"));
        var dd = await Detail(list.Items[3].AssetId);
        dd.CouldChange.Should().Contain(c => c.Contains("Criticidade 3 ou 4"))
            .And.NotContain(c => c.Contains("Intune"), "o Intune já informou conforme e com criptografia");
        Factor(dd, "deviceManagement").Effect.Should().Be(DevicePriorityEffects.None);
    }

    [Fact]
    public async Task ImportantVulnerabilityWithoutIntune_IsPrioritized_AndUnknownsAreDeclared_NeverFavorable()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        DefenderData(new[] { Machine("mde-0001", "srv-01.demo.example.com", DevX) },
            new[] { ("mde-0001", "CVE-2024-3001"), ("mde-0001", "CVE-2024-3002") },
            Cve("CVE-2024-3001", "Critical", 9.8, publicExploit: true, exploitVerified: false, epss: 0.42,
                vector: "CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H"),
            Cve("CVE-2024-3002", "Medium", 5.0));
        await Sync(defender);
        var assetId = await AssetOf(defender, "mde-0001");

        var d = await Detail(assetId);
        d.Status.Should().Be(DevicePriorityStatuses.Prioritized);
        d.Band.Should().Be(DevicePriorityBands.P1);
        d.BandLabel.Should().Be("Prioridade 1 · tratar primeiro");
        d.PositionReason.Should().StartWith("Prioridade 1 · tratar primeiro: determinada por CVE-2024-3001 — severidade " +
            "técnica crítica (CVSS 9.8) e exploit público informado pela fonte");
        var c = d.DeterminingCase!;
        c.CveId.Should().Be("CVE-2024-3001");
        c.ExploitLabel.Should().Be("Exploit público informado pela fonte");
        c.SeverityLabel.Should().Be("Crítica (CVSS 9.8)");
        c.Epss.Should().Be(0.42);
        c.AttackVectorLabel.Should().Contain("rede (AV:N)").And.Contain("não comprova");
        c.AcquisitionState.Should().Be(CrossSourceAcquisitionStates.Current);

        Factor(d, "deviceManagement").Value.Should().StartWith("Sem registro do Microsoft Intune");
        Factor(d, "deviceManagement").Effect.Should().Be(DevicePriorityEffects.NotUsed);
        Factor(d, "networkExposure").Value.Should().Be("Desconhecida");
        Factor(d, "networkExposure").Note.Should().Contain("não é exposição de rede").And.Contain("AV:N");
        Factor(d, "knownExploitation").Value.Should().Be("Desconhecida");
        Factor(d, "threatObserved").Note.Should().Contain("alertas agregados por software");
        Factor(d, "epss").Effect.Should().Be(DevicePriorityEffects.NotUsed);
        Factor(d, "epss").Note.Should().Contain("GLOBAL").And.Contain("Não é probabilidade de comprometimento deste ambiente");
        d.InformationLabel.Should().StartWith("Informação parcial").And.Contain("criticidade").And.Contain("exposição de rede");
        d.Limitations.Should().Contain(l => l.Contains("não comprova exploração ativa"));
        d.NextAction.Should().Contain("Tratar CVE-2024-3001").And.Contain("confirmar numa nova coleta");

        d.Cases!.Total.Should().Be(2);
        d.Cases.Items.Select(x => (x.CveId, x.Band)).Should().Equal(("CVE-2024-3001", DevicePriorityBands.P1), ("CVE-2024-3002", DevicePriorityBands.P4));

        // Nada foi escrito nos scores existentes; nenhum identificador técnico sai na leitura comum.
        await using (var db = NewContext(TenantA))
        {
            var asset = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == assetId);
            asset.RiskScore.Should().BeNull();
            asset.RiskLevel.Should().BeNull();
            (await db.Signals.CountAsync()).Should().Be(0, "a prioridade não gera sinal de score");
            (await db.TenantControlStates.CountAsync()).Should().Be(0, "nem estado de controle");
        }
        var json = JsonSerializer.Serialize(d) + JsonSerializer.Serialize(await List());
        foreach (var technical in new[] { DevX, DirA, "mde-0001" })
            json.Should().NotContain(technical);
    }

    // ================= Criticidade: padrão × declarada com proveniência ==============================================

    [Fact]
    public async Task DefaultCriticality_IsNotConfirmed_DeclarationWithProvenanceRaisesOneBand_NeverLowers_AndOnlyManagersDeclare()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        DefenderData(new[] { Machine("mde-0001", "pc-01.demo.example.com", null) }, new[] { ("mde-0001", "CVE-2024-3101") },
            Cve("CVE-2024-3101"));
        await Sync(defender);
        var assetId = await AssetOf(defender, "mde-0001");

        var before = await Detail(assetId);
        before.Band.Should().Be(DevicePriorityBands.P3);
        before.Criticality.State.Should().Be(DevicePriorityCriticalityStates.NotConfirmed);
        before.Criticality.StoredValue.Should().Be(1);
        before.Criticality.Label.Should().Contain("não confirmada").And.Contain("valor padrão do modelo");
        Factor(before, "assetCriticality").Kind.Should().Be(DevicePriorityFactorKinds.Unknown);
        Factor(before, "assetCriticality").Effect.Should().Be(DevicePriorityEffects.NotUsed);

        // Só Manager/TenantAdmin declaram; a leitura continua aberta a qualquer papel autenticado do tenant.
        typeof(AssetsController).GetMethod(nameof(AssetsController.DeclareCriticality))!
            .GetCustomAttribute<AuthorizeAttribute>()!.Roles.Should().Be("Manager,TenantAdmin");
        typeof(AssetsController).GetMethod(nameof(AssetsController.Priority))!
            .GetCustomAttribute<AuthorizeAttribute>().Should().BeNull();

        var at = _base.AddHours(1);
        var declared = (await Declare(assetId, 4, "Servidor de <b>produção</b> (sintético)", at: at)).Value!;
        declared.State.Should().Be(DevicePriorityCriticalityStates.Declared);
        declared.DeclaredValue.Should().Be(4);
        declared.DeclaredByName.Should().Be("Gestora Sintética");
        declared.DeclaredAt.Should().Be(at);
        declared.Note.Should().NotContain("<b>");
        await using (var db = NewContext(TenantA))
        {
            var asset = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == assetId);
            asset.CriticalityDeclaredByAccountId.Should().Be(Manager, "o autor vem do token, nunca do corpo");
            asset.Criticality.Should().Be(4);
            asset.RiskScore.Should().BeNull("declarar criticidade não escreve score");
        }

        var after = await Detail(assetId);
        after.Band.Should().Be(DevicePriorityBands.P2);
        Factor(after, "assetCriticality").Kind.Should().Be(DevicePriorityFactorKinds.Declared);
        Factor(after, "assetCriticality").Effect.Should().Be(DevicePriorityEffects.Aggravating);
        Factor(after, "assetCriticality").AvailableAt.Should().Be(at);
        after.PositionReason.Should().Contain("antecipada de Prioridade 3 para Prioridade 2")
            .And.Contain("criticidade 4 declarada com proveniência");
        (await List()).Items.Single().Aggravators.Should().ContainSingle(x => x.Contains("criticidade 4"));

        // Criticidade DECLARADA baixa não rebaixa a faixa técnica; valor alterado fora da declaração deixa de ser confirmado.
        (await Declare(assetId, 2)).Value!.State.Should().Be(DevicePriorityCriticalityStates.Declared);
        var low = await Detail(assetId);
        low.Band.Should().Be(DevicePriorityBands.P3, "a política só antecipa com agravante comprovado — nunca rebaixa");
        Factor(low, "assetCriticality").Effect.Should().Be(DevicePriorityEffects.None);
        await Declare(assetId, 4);
        await using (var db = NewContext(TenantA))
            await db.Assets.Where(x => x.Id == assetId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Criticality, 1));
        var diverged = await Detail(assetId);
        diverged.Criticality.State.Should().Be(DevicePriorityCriticalityStates.Diverged);
        diverged.Band.Should().Be(DevicePriorityBands.P3);

        // Validação, inexistência e isolamento.
        (await Declare(assetId, 5)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await Declare(assetId, 0)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await Declare(Guid.NewGuid(), 3)).Result.Should().BeOfType<NotFoundResult>();
        (await Declare(assetId, 3, tenant: TenantB)).Result.Should().BeOfType<NotFoundResult>("outro tenant não enxerga o ativo");
    }

    // ================= Exploit, exploração conhecida, EPSS e vetor ===================================================

    [Fact]
    public async Task ExploitIsNotActiveExploitation_KnownExploitedWithoutOriginIsIgnored_EpssDoesNotReorder_AndSeverityPrecedenceIsFixed()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        DefenderData(
            new[] { Machine("mde-exp", "pc-exp.demo.example.com", null), Machine("mde-div", "pc-div.demo.example.com", null) },
            new[] { ("mde-exp", "CVE-2024-4001"), ("mde-exp", "CVE-2024-4002"), ("mde-div", "CVE-2024-4003") },
            Cve("CVE-2024-4001", "Medium", 5.3, publicExploit: true, exploitVerified: true, vector: "CVSS:3.1/AV:N/AC:L/PR:N/UI:R/S:U/C:L/I:L/A:N"),
            Cve("CVE-2024-4002", "Low", 3.1, publicExploit: false, exploitVerified: false, epss: 0.97),
            Cve("CVE-2024-4003", "High", 9.1));
        await Sync(defender);
        // A marca "explorada ativamente" sem origem registrada (nenhuma fonte de threat intel integrada).
        await using (var db = NewContext(TenantA))
            await db.Threats.Where(t => t.Code == "CVE-2024-4001").ExecuteUpdateAsync(s => s.SetProperty(t => t.KnownExploited, true));

        var list = await List();
        // Mesma faixa (P2): exploit verificado vem antes de severidade crítica sem exploit (desempate explícito).
        list.Items.Select(i => (i.AssetName, i.Band)).Should().Equal(
            ("pc-exp.demo.example.com", DevicePriorityBands.P2), ("pc-div.demo.example.com", DevicePriorityBands.P2));

        var exp = await Detail(list.Items[0].AssetId);
        exp.DeterminingCase!.CveId.Should().Be("CVE-2024-4001");
        exp.DeterminingCase.ExploitLabel.Should().Be("Exploit verificado informado pela fonte");
        exp.DeterminingCase.KnownExploitedMarkedWithoutOrigin.Should().BeTrue();
        Factor(exp, "knownExploitation").Value.Should().Be("Desconhecida");
        Factor(exp, "knownExploitation").Note.Should().Contain("sem origem verificável");
        exp.PositionReason.Should().NotContain("explorada").And.NotContain("ameaça");
        // EPSS 97% não antecipa uma CVE baixa sem exploit: informação global, fora da decisão.
        exp.Cases!.Items.Select(c => (c.CveId, c.Band)).Should().Equal(
            ("CVE-2024-4001", DevicePriorityBands.P2), ("CVE-2024-4002", DevicePriorityBands.P4));
        Factor(exp, "networkExposure").Note.Should().Contain("AV:N").And.Contain("não comprova");

        // CVSS 9.1 com severidade textual "High": precedência FIXA do CVSS (Crítica), divergência visível.
        var div = await Detail(list.Items[1].AssetId);
        Factor(div, "technicalSeverity").Value.Should().Be("Crítica (CVSS 9.1)");
        div.Caveats.Should().Contain(n => n.Code == "severityDivergence" && n.Text.Contains("Alta") && n.Text.Contains("Crítica"));
    }

    // ================= Agravantes sem dupla contagem =================================================================

    [Fact]
    public async Task Aggravators_AreCappedAtOneBand_TwoSituationsAndDuplicateReportsNeverCountTwice()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer);
        // A mesma relação máquina × CVE reportada duas vezes: UM caso.
        DefenderData(new[] { Machine("mde-0001", "pc-01.demo.example.com", DevX) },
            new[] { ("mde-0001", "CVE-2024-5101"), ("mde-0001", "CVE-2024-5101") }, Cve("CVE-2024-5101", "Low", 3.1));
        _src.IntuneDevices = Page(Device("int-0001", DevX));
        await Sync(defender);
        await Sync(intune);
        var assetId = await AssetOf(defender, "mde-0001");
        await Declare(assetId, 4);

        var d = await Detail(assetId);
        d.Band.Should().Be(DevicePriorityBands.P3, "Baixa sem exploit = Prioridade 4; três agravantes antecipam UMA faixa");
        d.Situations.Where(s => s.State == CrossSourceStates.Identified).Should().HaveCount(2);
        d.PositionReason.Should().Contain("antecipada de Prioridade 4 para Prioridade 3").And.Contain("no máximo uma faixa");
        d.CasesByBand.Single(b => b.Band == DevicePriorityBands.P3).Count.Should().Be(1);
        d.Cases!.Total.Should().Be(1);
        Factor(d, "deviceManagement").Effect.Should().Be(DevicePriorityEffects.Aggravating);
        Factor(d, "assetCriticality").Effect.Should().Be(DevicePriorityEffects.Aggravating);
        d.CouldChange.Should().Contain(c => c.Contains("Outro agravante não mudaria"));
        (await List()).Items.Single().Aggravators.Should().HaveCount(2);
    }

    // ================= Estados da coleta propagados ==================================================================

    [Fact]
    public async Task PartialFailedAndUnconcludedAcquisitions_KeepThePriority_WithTheCorrelationCaveats_NeverAsADiscount()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer);
        DefenderData(new[] { Machine("mde-0001", "pc-01.demo.example.com", DevX) }, new[] { ("mde-0001", "CVE-2024-6101") },
            Cve("CVE-2024-6101"));
        _src.IntuneDevices = Page(Device("int-0001", DevX));
        await Sync(defender);
        await Sync(intune);
        var assetId = await AssetOf(defender, "mde-0001");
        var clean = await Detail(assetId);
        clean.Band.Should().Be(DevicePriorityBands.P2);
        clean.Caveats.Should().BeEmpty();

        // Parcial (relação órfã): o fato positivo sustenta, com ressalva — a faixa não é descontada.
        DefenderData(new[] { Machine("mde-0001", "pc-01.demo.example.com", DevX) },
            new[] { ("mde-0001", "CVE-2024-6101"), ("mde-orfa", "CVE-2024-6109") }, Cve("CVE-2024-6101"), Cve("CVE-2024-6109"));
        await Sync(defender);
        var partial = await Detail(assetId);
        partial.Band.Should().Be(DevicePriorityBands.P2);
        partial.Caveats.Should().Contain(n => n.Code == "partialAcquisition");

        // Falha recente: a última evidência publicada continua sustentando, com ressalva.
        _src.DefenderRoute = req => req.RequestUri!.AbsolutePath.Contains("/api/machines") ? (HttpStatusCode.Forbidden, "{}") : null;
        await FluentActions.Awaiting(() => Sync(defender)).Should().ThrowAsync<Exception>();
        var failed = await Detail(assetId);
        failed.Band.Should().Be(DevicePriorityBands.P2);
        failed.Caveats.Should().Contain(n => n.Code == "latestAttemptFailed");

        // Publicação interrompida com uma CVE crítica nova: ela nunca entra; o caso anterior segue, qualificado.
        _src.DefenderRoute = null;
        DefenderData(new[] { Machine("mde-0001", "pc-01.demo.example.com", DevX) },
            new[] { ("mde-0001", "CVE-2024-6101"), ("mde-0001", "CVE-2024-6102") },
            Cve("CVE-2024-6101"), Cve("CVE-2024-6102", "Critical", 9.8, publicExploit: true));
        await FluentActions.Awaiting(() => Sync(defender, checkpoint: (point, _) =>
                point == DeviceIdentityResolver.CheckpointPresenceCommitted
                    ? throw new InvalidOperationException("interrupção sintética")
                    : Task.CompletedTask))
            .Should().ThrowAsync<InvalidOperationException>();
        var unconcluded = await Detail(assetId);
        unconcluded.Band.Should().Be(DevicePriorityBands.P2);
        unconcluded.Cases!.Total.Should().Be(1, "a CVE da aquisição não publicada nunca aparece");
        unconcluded.DeterminingCase!.CveId.Should().Be("CVE-2024-6101");
        unconcluded.DeterminingCase.AcquisitionState.Should().Be(CrossSourceAcquisitionStates.Previous);
        unconcluded.Caveats.Select(n => n.Code).Should().Contain(new[] { "publicationNotConcluded", "previousAcquisition" });

        // A próxima aquisição completa publica a CVE nova: Prioridade 1, sem ressalvas.
        await Sync(defender);
        var done = await Detail(assetId);
        done.Band.Should().Be(DevicePriorityBands.P1);
        done.DeterminingCase!.CveId.Should().Be("CVE-2024-6102");
        done.Caveats.Should().BeEmpty();
    }

    [Fact]
    public async Task StaleEvidence_IsNotPrioritized_WithAnExplicitReason_AndStaysVisibleAsInsufficient()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        DefenderData(
            new[] { Machine("mde-0001", "pc-01.demo.example.com", null), Machine("mde-0002", "pc-02.demo.example.com", null) },
            new[] { ("mde-0001", "CVE-2024-7001"), ("mde-0002", "CVE-2024-7002") },
            Cve("CVE-2024-7001", "Critical", 9.8, publicExploit: true), Cve("CVE-2024-7002"));
        await Sync(defender);
        var one = await AssetOf(defender, "mde-0001");

        // (a) Oito dias depois: o registro da fonte está fora da política temporal — não é tratado como atual.
        var later = _base.AddDays(8);
        var old = await Detail(one, now: later);
        old.Status.Should().Be(DevicePriorityStatuses.Insufficient);
        old.Band.Should().Be(DevicePriorityBands.Insufficient);
        old.BandLabel.Should().Be("Não priorizável · informação insuficiente");
        old.PositionReason.Should().Contain("não está atualmente observado");
        (await List(now: later)).Items.Should().BeEmpty("nada antigo é anunciado como prioridade atual");
        var insufficient = await List(band: DevicePriorityBands.Insufficient, now: later);
        insufficient.Total.Should().Be(2, "cada dispositivo continua visível, com o motivo");
        insufficient.Items.Should().OnlyContain(i => i.Status == DevicePriorityStatuses.Insufficient && i.DeterminingCase == null);

        // (b) Registro recente, observações antigas: as CVEs em aberto não sustentam prioridade atual.
        await using (var db = NewContext(TenantA))
        {
            var rows = await db.AssetThreatObservations.Where(o => o.AssetThreatExposure!.AssetId == one).ToListAsync();
            foreach (var o in rows) { o.LastSeenAt = o.LastSeenAt.AddDays(-10); o.FirstSeenAt = o.FirstSeenAt.AddDays(-10); }
            await db.SaveChangesAsync();
        }
        var stale = await Detail(one);
        stale.Status.Should().Be(DevicePriorityStatuses.Insufficient);
        stale.ExcludedOutOfPolicy.Should().Be(1);
        stale.PositionReason.Should().Contain("antes da política temporal");
        (await List()).Items.Select(i => i.AssetName).Should().Equal("pc-02.demo.example.com");
    }

    [Fact]
    public async Task LinkConflictAndContradictoryIntuneRecords_NeitherAggravateNorHide_TheDefenderCase()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer);
        DefenderData(
            new[] { Machine("mde-x", "pc-x.demo.example.com", DevX), Machine("mde-z", "pc-z.demo.example.com", DevZ) },
            new[] { ("mde-x", "CVE-2024-8001"), ("mde-z", "CVE-2024-8002") }, Cve("CVE-2024-8001"), Cve("CVE-2024-8002"));
        // Z: dois registros do Intune com o mesmo dispositivo de diretório e fatos opostos.
        _src.IntuneDevices = Page(Device("int-x", DevX),
            Device("int-z1", DevZ, "noncompliant", encrypted: true), Device("int-z2", DevZ, "compliant", encrypted: false));
        await Sync(defender);
        await Sync(intune);
        var x = await AssetOf(defender, "mde-x");
        var z = await AssetOf(defender, "mde-z");

        var contradictory = await Detail(z);
        contradictory.Band.Should().Be(DevicePriorityBands.P3, "registros contraditórios não agravam nem atenuam");
        contradictory.Caveats.Should().Contain(n => n.Code == "deviceContextContradictory");
        Factor(contradictory, "deviceManagement").Value.Should().StartWith("Registros do Intune contraditórios");

        (await Detail(x)).Band.Should().Be(DevicePriorityBands.P2);
        // A máquina X passa a informar outro identificador de diretório: conflito — o caso do Defender continua atribuído.
        DefenderData(
            new[] { Machine("mde-x", "pc-x.demo.example.com", DevY), Machine("mde-z", "pc-z.demo.example.com", DevZ) },
            new[] { ("mde-x", "CVE-2024-8001"), ("mde-z", "CVE-2024-8002") }, Cve("CVE-2024-8001"), Cve("CVE-2024-8002"));
        await Sync(defender);
        var conflict = await Detail(x);
        conflict.Status.Should().Be(DevicePriorityStatuses.Prioritized);
        conflict.Band.Should().Be(DevicePriorityBands.P3, "sem associação resolvida, o contexto do Intune não é usado");
        conflict.Caveats.Should().Contain(n => n.Code == "deviceContextConflict");
        Factor(conflict, "deviceManagement").Value.Should().StartWith("Vínculo em conflito");
    }

    // ================= Disposições humanas e ciclo de vida ===========================================================

    [Fact]
    public async Task HumanDispositions_AndNoLongerReported_LeaveTheQueue_WithEvidencePreserved_AndNothingReopened()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        string[] machines =
        {
            Machine("mde-disp", "pc-disp.demo.example.com", null), Machine("mde-all", "pc-all.demo.example.com", null),
            Machine("mde-gone", "pc-gone.demo.example.com", null),
        };
        string[] cves =
        {
            Cve("CVE-2024-9001", "Critical", 9.8, publicExploit: true), Cve("CVE-2024-9002", "High", 8.8),
            Cve("CVE-2024-9003", "Medium", 5.0), Cve("CVE-2024-9004", "Critical", 9.0), Cve("CVE-2024-9005"),
        };
        DefenderData(machines,
            new[] { ("mde-disp", "CVE-2024-9001"), ("mde-disp", "CVE-2024-9002"), ("mde-disp", "CVE-2024-9003"),
                    ("mde-all", "CVE-2024-9004"), ("mde-gone", "CVE-2024-9005") }, cves);
        await Sync(defender);
        var disp = await AssetOf(defender, "mde-disp");
        var all = await AssetOf(defender, "mde-all");
        var gone = await AssetOf(defender, "mde-gone");
        await SetDispositionAsync(disp, "CVE-2024-9001", ExposureStatus.Accepted);
        await SetDispositionAsync(disp, "CVE-2024-9002", ExposureStatus.FalsePositive);
        await SetDispositionAsync(all, "CVE-2024-9004", ExposureStatus.Mitigated);

        var d = await Detail(disp);
        d.Band.Should().Be(DevicePriorityBands.P4, "o que resta na fila é a CVE média sem exploit informado");
        d.DeterminingCase!.CveId.Should().Be("CVE-2024-9003");
        d.Dispositions.Single(x => x.Status == "accepted").Count.Should().Be(1);
        d.Dispositions.Single(x => x.Status == "falsePositive").Count.Should().Be(1);
        d.Cases!.Total.Should().Be(1);

        var a = await Detail(all);
        a.Status.Should().Be(DevicePriorityStatuses.AllDisposed);
        a.BandLabel.Should().Be("Fora da fila · disposição registrada");
        a.Dispositions.Single(x => x.Status == "mitigated").Label.Should().Be("Mitigação informada");

        // Não mais reportada pela fonte (coleta completa sem a CVE): sai da fila e não é "correção validada". O catálogo
        // devolve só as CVEs pedidas — uma CVE não pedida na resposta torna a coleta incompleta (e a ausência não é publicada).
        DefenderData(machines,
            new[] { ("mde-disp", "CVE-2024-9001"), ("mde-disp", "CVE-2024-9002"), ("mde-disp", "CVE-2024-9003"),
                    ("mde-all", "CVE-2024-9004") }, cves[..4]);
        await Sync(defender);
        await using (var check = NewContext(TenantA))
            (await check.AssetThreatObservations.AsNoTracking().Where(o => o.AssetThreatExposure!.AssetId == gone)
                    .Select(o => o.LifecycleState).ToListAsync())
                .Should().Equal(new[] { ObservationLifecycle.Resolved }, "a coleta completa sem a CVE a marca como não mais reportada");
        var g = await Detail(gone);
        g.Status.Should().Be(DevicePriorityStatuses.NoOpenCases);
        g.NoLongerReported.Should().Be(1);
        g.NextAction.Should().Contain("não comprova que o dispositivo esteja seguro");

        var list = await List();
        list.Items.Select(i => i.AssetName).Should().Equal("pc-disp.demo.example.com");
        list.Summary.Dispositions.Select(x => (x.Status, x.Count)).Should().Equal(("mitigated", 1), ("accepted", 1), ("falsePositive", 1));

        // A leitura não apaga evidência nem reabre trabalho: disposições e observações seguem como estavam.
        await using var db = NewContext(TenantA);
        (await db.AssetThreatExposures.CountAsync()).Should().Be(5);
        (await db.AssetThreatExposures.CountAsync(e => e.Status != ExposureStatus.Active)).Should().Be(3);
    }

    // ================= Caso determinante = primeiro caso pela chave FINAL ===========================================

    [Fact]
    public async Task DeterminingCase_FollowsTheFinalKey_WhenAggravationSaturatesInP1()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        // A: crítica com exploit público → base P1. B: média com exploit verificado → base P2. Com agravante, as duas
        // ficam em P1 e a política desempata pelo exploit: B (verificado) antes de A (público).
        DefenderData(new[] { Machine("mde-sat", "srv-sat.demo.example.com", null) },
            new[] { ("mde-sat", "CVE-2024-1101"), ("mde-sat", "CVE-2024-1102") },
            Cve("CVE-2024-1101", "Critical", 9.8, publicExploit: true, exploitVerified: false),
            Cve("CVE-2024-1102", "Medium", 6.5, publicExploit: true, exploitVerified: true));
        await Sync(defender);
        var assetId = await AssetOf(defender, "mde-sat");

        var plain = await Detail(assetId);
        plain.Band.Should().Be(DevicePriorityBands.P1);
        plain.DeterminingCase!.CveId.Should().Be("CVE-2024-1101", "sem agravante, A é P1 e B é P2");
        plain.Cases!.Items.Select(c => (c.CveId, c.Band)).Should().Equal(
            ("CVE-2024-1101", DevicePriorityBands.P1), ("CVE-2024-1102", DevicePriorityBands.P2));

        await Declare(assetId, 4);
        var aggravated = await Detail(assetId);
        aggravated.Band.Should().Be(DevicePriorityBands.P1);
        aggravated.DeterminingCase!.CveId.Should().Be("CVE-2024-1102", "P1 × P1: exploit verificado precede o público");
        aggravated.Cases!.Items.Select(c => (c.CveId, c.Band)).Should().Equal(
            ("CVE-2024-1102", DevicePriorityBands.P1), ("CVE-2024-1101", DevicePriorityBands.P1));
        aggravated.PositionReason.Should().StartWith("Prioridade 1 · tratar primeiro: determinada por CVE-2024-1102 — " +
            "severidade técnica média (CVSS 6.5) e exploit verificado informado pela fonte; antecipada de Prioridade 2 " +
            "para Prioridade 1 por criticidade 4 declarada com proveniência");
        Factor(aggravated, "technicalSeverity").Value.Should().Be("Média (CVSS 6.5)");
        Factor(aggravated, "exploit").Value.Should().Be("Exploit verificado informado pela fonte");
        aggravated.NextAction.Should().StartWith("Tratar CVE-2024-1102 neste dispositivo");
        var item = (await List()).Items.Single();
        item.DeterminingCase!.CveId.Should().Be("CVE-2024-1102");
        item.PositionReason.Should().Be(aggravated.PositionReason);
        item.NextAction.Should().Be(aggravated.NextAction);
        (await Detail(assetId, casePage: 2, casePageSize: 1)).Cases!.Items.Single().CveId.Should().Be("CVE-2024-1101",
            "a paginação segue a mesma ordem");
    }

    [Fact]
    public async Task CaseOrder_EveryTableCombination_MatchesThePolicyComparer_WithAndWithoutAggravation()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        var combos = new List<(string Cve, int Sev, int Exp, double Cvss)>();
        var cvss = new[] { 9.8, 8.1, 5.5, 3.1 };
        for (var s = 1; s <= 4; s++)
            for (var e = 0; e <= 2; e++)
                combos.Add(($"CVE-2025-{s}{e}00", s, e, cvss[s - 1]));
        DefenderData(new[] { Machine("mde-all", "srv-all.demo.example.com", null) },
            combos.Select(c => ("mde-all", c.Cve)).ToArray(),
            combos.Select(c => Cve(c.Cve, null, c.Cvss, publicExploit: c.Exp <= 1, exploitVerified: c.Exp == 0)).ToArray());
        await Sync(defender);
        var assetId = await AssetOf(defender, "mde-all");

        IReadOnlyList<(string, string)> Expected(bool aggravated) => combos
            .Select(c => (c.Cve, Key: new DevicePriorityKey(
                DevicePriorityPolicy.FinalBand(DevicePriorityPolicy.BaseBand(c.Sev, c.Exp)!.Value, aggravated),
                c.Exp, c.Sev, aggravated, c.Cvss)))
            .OrderBy(x => x.Key, Comparer<DevicePriorityKey>.Create(DevicePriorityPolicy.Compare))
            .ThenBy(x => x.Cve, StringComparer.Ordinal)
            .Select(x => (x.Cve, DevicePriorityBands.Of(x.Key.Band)))
            .ToList();

        foreach (var aggravated in new[] { false, true })
        {
            if (aggravated) await Declare(assetId, 4);
            var d = await Detail(assetId, casePageSize: 50);
            d.Cases!.Items.Select(c => (c.CveId, c.Band)).Should().Equal(Expected(aggravated),
                $"ordem do banco = DevicePriorityPolicy.Compare (agravante: {aggravated})");
            d.DeterminingCase!.CveId.Should().Be(Expected(aggravated)[0].Item1);
        }
    }

    // ================= Ausência só com completude suficiente =========================================================

    [Fact]
    public async Task AbsenceOfCases_IsConcludedOnlyFromACompleteAcquisition()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        string[] machines =
        {
            Machine("mde-a", "pc-a.demo.example.com", null), Machine("mde-b", "pc-b.demo.example.com", null),
            Machine("mde-c", "pc-c.demo.example.com", null),
        };
        // (1) PRIMEIRA aquisição parcial (relação órfã): A observado sem CVE publicada; B com CVE; C com CVE disposta.
        DefenderData(machines,
            new[] { ("mde-b", "CVE-2024-1201"), ("mde-c", "CVE-2024-1202"), ("mde-orfa", "CVE-2024-1209") },
            Cve("CVE-2024-1201"), Cve("CVE-2024-1202"), Cve("CVE-2024-1209"));
        await Sync(defender);
        var a = await AssetOf(defender, "mde-a");
        var b = await AssetOf(defender, "mde-b");
        var c = await AssetOf(defender, "mde-c");
        await SetDispositionAsync(c, "CVE-2024-1202", ExposureStatus.Accepted);

        var partialA = await Detail(a);
        partialA.Status.Should().NotBe(DevicePriorityStatuses.NoOpenCases, "aquisição parcial não prova ausência");
        partialA.NextAction.Should().NotContain("Nada a tratar");
        partialA.PositionReason.Should().Contain("parcial");
        partialA.Status.Should().Be(DevicePriorityStatuses.AbsenceNotVerified);
        partialA.BandLabel.Should().Be("Sem caso publicado · ausência não verificável");
        partialA.AbsenceState.Should().Be(DevicePriorityAbsenceStates.NotVerifiable);
        partialA.StoredOpenCases.Should().Be(0, "o fato (nenhum caso publicado) é distinto da conclusão");
        Factor(partialA, "evidence").Kind.Should().Be(DevicePriorityFactorKinds.Unknown);
        Factor(partialA, "evidence").Value.Should().Contain("ausência não verificável");
        partialA.CouldChange.Should().Contain(x => x.Contains("aquisição completa"));
        var partialB = await Detail(b);
        partialB.Band.Should().Be(DevicePriorityBands.P3, "o caso positivo de coleta parcial continua utilizável (alta sem exploit)");
        partialB.Caveats.Should().Contain(n => n.Code == "partialAcquisition");
        partialB.AbsenceState.Should().Be(DevicePriorityAbsenceStates.NotVerifiable, "a ausência de OUTRAS não é concluída");
        partialB.AbsenceLabel.Should().Contain("não pode ser concluída");
        var partialC = await Detail(c);
        partialC.Status.Should().Be(DevicePriorityStatuses.AllDisposed);
        partialC.NextAction.Should().Contain("não permite concluir");
        partialC.StoredOpenCases.Should().Be(1);
        var partialList = await List();
        partialList.Summary.CandidateAssets.Should().Be(1, "candidatos continuam sendo só os dispositivos com caso em aberto");
        partialList.Summary.AbsenceState.Should().Be(DevicePriorityAbsenceStates.NotVerifiable);
        partialList.Summary.AbsenceNote.Should().Contain("parcial");
        partialList.Items.Should().ContainSingle().Which.AssetId.Should().Be(b);

        // (2) Aquisição completa sem CVE para A: ZERO verdadeiro.
        DefenderData(machines, new[] { ("mde-b", "CVE-2024-1201"), ("mde-c", "CVE-2024-1202") },
            Cve("CVE-2024-1201"), Cve("CVE-2024-1202"));
        await Sync(defender);
        var complete = await Detail(a);
        complete.Status.Should().Be(DevicePriorityStatuses.NoOpenCases);
        complete.NextAction.Should().Contain("não comprova que o dispositivo esteja seguro");
        complete.AbsenceState.Should().Be(DevicePriorityAbsenceStates.Conclusive);
        complete.BandLabel.Should().Be("Sem vulnerabilidade em aberto na aquisição completa mais recente");
        Factor(complete, "evidence").Kind.Should().Be(DevicePriorityFactorKinds.SourceFact);
        Factor(complete, "evidence").Value.Should().StartWith("Nenhuma em aberto na aquisição completa de");
        (await Detail(c)).NextAction.Should().NotContain("não permite concluir");
        var completeList = await List();
        completeList.Summary.AbsenceState.Should().Be(DevicePriorityAbsenceStates.Conclusive);
        completeList.Summary.AbsenceNote.Should().BeNull();

        // (3) Tentativa seguinte falha: o zero é o da última aquisição completa, com ressalva da tentativa.
        _src.DefenderRoute = req => req.RequestUri!.AbsolutePath.Contains("/api/machines") ? (HttpStatusCode.Forbidden, "{}") : null;
        await FluentActions.Awaiting(() => Sync(defender)).Should().ThrowAsync<Exception>();
        var failed = await Detail(a);
        failed.Status.Should().Be(DevicePriorityStatuses.NoOpenCases);
        failed.Caveats.Should().Contain(n => n.Code == "latestAttemptFailed");
        failed.PositionReason.Should().Contain("tentativa mais recente");
        failed.NextAction.Should().Contain("tentativa mais recente");
        failed.AbsenceState.Should().Be(DevicePriorityAbsenceStates.ConclusiveAttemptFailed);
        var failedList = await List();
        failedList.Summary.AbsenceState.Should().Be(DevicePriorityAbsenceStates.ConclusiveAttemptFailed);
        failedList.Summary.AbsenceNote.Should().Contain("falhou").And.Contain("última aquisição completa publicada");

        // (4) Publicação interrompida DEPOIS dos dispositivos e ANTES dos lotes de CVEs.
        _src.DefenderRoute = null;
        DefenderData(machines,
            new[] { ("mde-a", "CVE-2024-1203"), ("mde-b", "CVE-2024-1201"), ("mde-c", "CVE-2024-1202") },
            Cve("CVE-2024-1201"), Cve("CVE-2024-1202"), Cve("CVE-2024-1203"));
        await FluentActions.Awaiting(() => Sync(defender, checkpoint: (point, _) =>
                point == DeviceIdentityResolver.CheckpointPresenceCommitted
                    ? throw new InvalidOperationException("interrupção sintética")
                    : Task.CompletedTask))
            .Should().ThrowAsync<InvalidOperationException>();
        var unconcluded = await Detail(a);
        unconcluded.Status.Should().NotBe(DevicePriorityStatuses.NoOpenCases, "a publicação não permite concluir");
        unconcluded.NextAction.Should().NotContain("Nada a tratar");
        unconcluded.PositionReason.Should().Contain("não foi concluída");
        unconcluded.Cases!.Total.Should().Be(0, "a CVE do lote não publicado nunca aparece");
        unconcluded.Status.Should().Be(DevicePriorityStatuses.AbsenceNotVerified);
        var unconcludedList = await List();
        unconcludedList.Summary.AbsenceState.Should().Be(DevicePriorityAbsenceStates.NotVerifiable);
        unconcludedList.Summary.AbsenceNote.Should().Contain("não foi concluída");

        // (5) Leitura cujo desfecho não foi registrado (legado anterior ao registro do desfecho).
        DefenderData(machines, new[] { ("mde-b", "CVE-2024-1201"), ("mde-c", "CVE-2024-1202") },
            Cve("CVE-2024-1201"), Cve("CVE-2024-1202"));
        await Sync(defender);
        (await Detail(a)).Status.Should().Be(DevicePriorityStatuses.NoOpenCases);
        await using (var db = NewContext(TenantA))
            await db.Connectors.Where(x => x.Id == defender)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeviceSnapshotOutcome, DeviceSnapshotOutcome.NotRecorded));
        var notRecorded = await Detail(a);
        notRecorded.Status.Should().NotBe(DevicePriorityStatuses.NoOpenCases);
        notRecorded.PositionReason.Should().Contain("desfecho");
        notRecorded.Status.Should().Be(DevicePriorityStatuses.AbsenceNotVerified);
        (await List()).Summary.AbsenceNote.Should().Contain("desfecho");
        (await Detail(b)).Band.Should().Be(DevicePriorityBands.P3, "o caso positivo continua valendo");

        // (6) Central com ZERO candidatos numa primeira aquisição parcial (outro tenant): a contagem é zero, a ausência
        // não é conclusiva — nunca "nenhum dispositivo com vulnerabilidade" como fato consumado.
        var defenderB = Seed(TenantB, ConnectorCapability.VulnerabilityScanner);
        DefenderData(new[] { Machine("mde-z", "pc-z.demo.example.com", null) }, new[] { ("mde-orfa", "CVE-2024-1209") },
            Cve("CVE-2024-1209"));
        await Sync(defenderB, TenantB);
        var zero = await List(TenantB);
        zero.Summary.ReadingState.Should().Be(DevicePriorityReadingStates.Available);
        zero.Summary.CandidateAssets.Should().Be(0);
        zero.Summary.AbsenceState.Should().Be(DevicePriorityAbsenceStates.NotVerifiable);
        zero.Summary.AbsenceNote.Should().Contain("parcial");
    }

    // ================= Estado informado pelo Intune preservado =======================================================

    [Fact]
    public async Task GracePeriod_IsNeverPresentedAsCompliant()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer);
        DefenderData(
            new[] { Machine("mde-grace", "pc-grace.demo.example.com", DevX), Machine("mde-ok", "pc-ok.demo.example.com", DevY) },
            new[] { ("mde-grace", "CVE-2024-1301"), ("mde-ok", "CVE-2024-1301") }, Cve("CVE-2024-1301"));
        _src.IntuneDevices = Page(Device("int-grace", DevX, "inGracePeriod", true), Device("int-ok", DevY, "compliant", true));
        await Sync(defender);
        await Sync(intune);
        var grace = await AssetOf(defender, "mde-grace");
        var ok = await AssetOf(defender, "mde-ok");

        var g = await Detail(grace);
        g.Band.Should().Be(DevicePriorityBands.P3, "carência não é não conformidade: nenhuma situação identificada");
        Factor(g, "deviceManagement").Value.Should().NotContain("como conforme").And.Contain("período de carência");
        Factor(g, "deviceManagement").Value.Should().Be(
            "O Intune informa o dispositivo em período de carência de conformidade e com criptografia — carência não é conformidade");
        Factor(g, "deviceManagement").Note.Should().Contain("Não é conformidade").And.Contain("XS-DEF-INT-NONCOMPLIANT");
        Factor(g, "deviceManagement").Effect.Should().Be(DevicePriorityEffects.None);
        g.CouldChange.Should().Contain(x => x.Contains("período de carência terminar"));
        g.Situations.Should().OnlyContain(s => s.State == CrossSourceStates.NotIdentified);
        var o = await Detail(ok);
        o.Band.Should().Be(DevicePriorityBands.P3);
        Factor(o, "deviceManagement").Value.Should().Be("O Intune informa o dispositivo como conforme e com criptografia");
        Factor(o, "deviceManagement").Note.Should().NotContain("carência");
        o.CouldChange.Should().NotContain(x => x.Contains("carência"));

        var list = await List();
        list.Items.Single(i => i.AssetId == grace).DeviceContextLabel.Should().Be(Factor(g, "deviceManagement").Value);
        list.Items.Single(i => i.AssetId == ok).DeviceContextLabel.Should().Be(Factor(o, "deviceManagement").Value);
    }

    // ================= Isolamento, paginação e seleção de candidatos =================================================

    [Fact]
    public async Task TenantIsolation_SameIdentifiersInAnotherTenant_NeverMix()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        var defenderB = Seed(TenantB, ConnectorCapability.VulnerabilityScanner);
        DefenderData(new[] { Machine("mde-0001", "pc-01.demo.example.com", DevX) }, new[] { ("mde-0001", "CVE-2024-9101") },
            Cve("CVE-2024-9101", "Critical", 9.8, publicExploit: true));
        await Sync(defender);
        DefenderData(new[] { Machine("mde-0001", "pc-01.demo.example.com", DevX) }, new[] { ("mde-0001", "CVE-2024-9102") },
            Cve("CVE-2024-9102", "Low", 2.0));
        await Sync(defenderB, TenantB);

        var a = await List();
        var b = await List(TenantB);
        a.Items.Should().ContainSingle().Which.Band.Should().Be(DevicePriorityBands.P1);
        b.Items.Should().ContainSingle().Which.Band.Should().Be(DevicePriorityBands.P4);
        a.Items[0].AssetId.Should().NotBe(b.Items[0].AssetId);
        (await DetailAction(a.Items[0].AssetId, TenantB)).Result.Should().BeOfType<NotFoundResult>();
        (await List(TenantB)).Summary.CandidateAssets.Should().Be(1);
    }

    [Fact]
    public async Task Pagination_TopCandidateOutsideTheAlphabeticalFirstPage_Totals_AndTheDeclaredCut()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        var machines = Enumerable.Range(1, 11).Select(i => Machine($"mde-{i:00}", $"pc-{i:00}.demo.example.com", null))
            .Append(Machine("mde-zz", "zz-critico.demo.example.com", null))
            .Append(Machine("mde-aa", "aa-sem-severidade.demo.example.com", null))
            .ToArray();
        var relations = Enumerable.Range(1, 11).Select(i => ($"mde-{i:00}", "CVE-2024-6001"))
            .Append(("mde-zz", "CVE-2024-6002")).Append(("mde-aa", "CVE-2024-6003")).ToArray();
        DefenderData(machines, relations,
            Cve("CVE-2024-6001", "Low", 3.1), Cve("CVE-2024-6002", "Critical", 9.8, exploitVerified: true),
            Cve("CVE-2024-6003", severity: null, cvss: null));
        await Sync(defender);
        // A relação máquina × CVE do Defender também traz severidade (que o catálogo herda): uma CVE sem CVSS NEM severidade
        // é o caso de uma fonte que não informa nenhuma das duas — gravada assim no catálogo.
        await using (var db = NewContext(TenantA))
            await db.Threats.Where(t => t.Code == "CVE-2024-6003")
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Severity, (string?)null).SetProperty(t => t.CvssScore, (double?)null));

        var p1 = await List(pageSize: 5);
        p1.Total.Should().Be(12, "unidade: dispositivos priorizáveis");
        p1.Items.Should().HaveCount(5);
        p1.Items[0].AssetName.Should().Be("zz-critico.demo.example.com", "o candidato prioritário não depende da ordem alfabética");
        p1.Items[0].Band.Should().Be(DevicePriorityBands.P1);
        p1.Items.Skip(1).Select(i => i.AssetName).Should().Equal(
            "pc-01.demo.example.com", "pc-02.demo.example.com", "pc-03.demo.example.com", "pc-04.demo.example.com");
        p1.Items.Skip(1).Should().OnlyContain(i => i.TiedAssets == 10 && i.Band == DevicePriorityBands.P4);
        p1.Summary.CandidateAssets.Should().Be(13);
        p1.Summary.EvaluationTruncated.Should().BeFalse();
        p1.Summary.AssetsByBand.Single(b => b.Band == DevicePriorityBands.Insufficient).Count.Should().Be(1);

        var p3 = await List(page: 3, pageSize: 5);
        p3.Items.Select(i => i.Position).Should().Equal(11, 12);
        (await List(band: DevicePriorityBands.P4)).Total.Should().Be(11);
        var unknown = (await List(band: DevicePriorityBands.Insufficient)).Items.Should().ContainSingle().Subject;
        unknown.AssetName.Should().Be("aa-sem-severidade.demo.example.com");
        unknown.PositionReason.Should().Contain("sem CVSS nem severidade");
        (await ListAction(band: "p9")).Result.Should().BeOfType<BadRequestObjectResult>();

        // Recorte DECLARADO: com teto 3, os avaliados são os de melhor limite superior — o crítico entra, e o resumo diz
        // quais faixas estão completas.
        var cut = await List(max: 3);
        cut.Summary.EvaluationTruncated.Should().BeTrue();
        cut.Summary.AssetsEvaluated.Should().Be(3);
        cut.Items[0].AssetName.Should().Be("zz-critico.demo.example.com");
        cut.Summary.CompleteThroughBand.Should().Be(DevicePriorityBands.P3);
        cut.Summary.TruncationNote.Should().Contain("não por ordem alfabética").And.Contain("Prioridade 3");
        var tiny = await List(max: 1);
        tiny.Items.Should().ContainSingle().Which.AssetName.Should().Be("zz-critico.demo.example.com");
        tiny.Summary.CompleteThroughBand.Should().BeNull();
        tiny.Summary.TruncationNote.Should().Contain("Nenhuma faixa está completa");
    }

    [Fact]
    public async Task ReadingStates_NoSource_And_NeverCollected()
    {
        var none = await List();
        none.Summary.ReadingState.Should().Be(DevicePriorityReadingStates.NoSource);
        none.Summary.ReadingNote.Should().Contain("Ausência de fonte não é ausência de vulnerabilidade");
        none.Items.Should().BeEmpty();
        none.Summary.AbsenceState.Should().Be(DevicePriorityAbsenceStates.NotApplicable);

        Seed(TenantA, ConnectorCapability.VulnerabilityScanner);
        var never = await List();
        never.Summary.ReadingState.Should().Be(DevicePriorityReadingStates.NeverCollected);
        never.Summary.AbsenceState.Should().Be(DevicePriorityAbsenceStates.NotApplicable, "o estado da leitura já diz o motivo");
    }

    // ================= Política (autoridade pura) e tradução da classificação =======================================

    [Fact]
    public async Task PolicyTable_MonotonicAggravation_AndTheRankExpressionAgreeInMemoryAndOnSqlite()
    {
        for (var s = 1; s <= 4; s++)
            for (var e = 0; e <= 2; e++)
            {
                var b = DevicePriorityPolicy.BaseBand(s, e)!.Value;
                b.Should().Be(Math.Clamp(s + e - 1, 1, 4), "a forma usada no banco é equivalente à tabela");
                DevicePriorityPolicy.FinalBand(b, true).Should().Be(Math.Max(1, b - 1));
                DevicePriorityPolicy.FinalBand(b, true).Should().BeLessOrEqualTo(DevicePriorityPolicy.FinalBand(b, false),
                    "um agravante comprovado nunca reduz a prioridade");
            }
        DevicePriorityPolicy.BaseBand(DevicePrioritySeverity.Unknown, 0).Should().BeNull("sem severidade: não priorizável");

        var variants = new (double? Cvss, string? Severity, bool? Pe, bool? Ev, int Sev, int Exp)[]
        {
            (10.0, null, null, null, 1, 2), (9.0, "Low", null, null, 1, 2), (8.9, null, true, null, 2, 1), (7.0, null, null, true, 2, 0),
            (6.9, null, false, false, 3, 2), (4.0, null, null, null, 3, 2), (3.9, null, null, null, 4, 2), (0.0, null, null, null, 4, 2),
            (null, "Critical", null, null, 1, 2), (null, " high ", null, null, 2, 2), (null, "MEDIUM", null, null, 3, 2),
            (null, "low", null, null, 4, 2), (null, "unknown", null, null, 5, 2), (null, null, true, true, 5, 0),
        };
        var ids = new List<Guid>();
        await using (var db = NewContext(null))
        {
            for (var i = 0; i < variants.Length; i++)
            {
                var v = variants[i];
                var t = new Threat
                {
                    Code = $"CVE-2099-{i:0000}", Source = ThreatSource.Cve, Title = "sintética",
                    CvssScore = v.Cvss, Severity = v.Severity, PublicExploit = v.Pe, ExploitVerified = v.Ev,
                };
                db.Threats.Add(t);
                ids.Add(t.Id);
            }
            await db.SaveChangesAsync();
        }
        await using (var db = NewContext(TenantA))
        {
            var translated = await db.Threats.AsNoTracking().Where(t => ids.Contains(t.Id))
                .Select(DevicePriorityPolicy.Rank).ToDictionaryAsync(r => r.ThreatId);
            for (var i = 0; i < variants.Length; i++)
            {
                var v = variants[i];
                var memory = DevicePriorityPolicy.RankOf(v.Cvss, v.Severity, v.Pe, v.Ev);
                (memory.SeverityOrder, memory.ExploitOrder).Should().Be((v.Sev, v.Exp), $"variante {i} em memória");
                (translated[ids[i]].SeverityOrder, translated[ids[i]].ExploitOrder).Should().Be((v.Sev, v.Exp), $"variante {i} no SQLite");
            }
        }
    }
}
