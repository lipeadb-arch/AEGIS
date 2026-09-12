using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Api.Controllers;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using AegisScore.Infrastructure.Tests.Connectors;   // SyntheticDeviceSources
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Queries;

/// <summary>
/// [AEGIS-CROSS-SOURCE-01] Situações identificadas entre fontes pelo CAMINHO REAL: conectores reais do Defender e do
/// Intune sobre HTTP SINTÉTICO (URLs oficiais, sem rede, sem credencial) → executor de ingestão → resolução por chave
/// forte → banco descartável (SQLite) → consulta → controllers (detalhe do ativo e Central de Prioridades). Nenhum caso
/// monta objetos à mão para o avaliador: o que ele julga é o que a coleta persistiu.
///
/// Relógio: o Defender marca a coleta com o instante REAL; o Intune, com o relógio-base do harness — aqui ancorado no
/// instante real (<see cref="_base"/>). A consulta usa um relógio controlado. Assim idades, janelas e defasagens não
/// dependem da data em que a bateria roda. Tudo é sintético (GUIDs inventados, demo.example.com).
/// </summary>
public sealed class CrossSourceCorrelationTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("c3000000-0000-0000-0000-00000000000c");
    private static readonly Guid TenantB = Guid.Parse("d4000000-0000-0000-0000-00000000000d");

    private const string DirA = SyntheticDeviceSources.DirA;
    private const string DirB = SyntheticDeviceSources.DirB;
    private const string DevX = SyntheticDeviceSources.DevX;
    private const string DevY = SyntheticDeviceSources.DevY;
    private const string DevZ = "7a3b6c5d-9f8e-4a01-b2c3-d4e5f6071829";

    private const string RuleA = CrossSourceRuleCodes.VulnerableAndNoncompliant;
    private const string RuleB = CrossSourceRuleCodes.VulnerableAndUnencrypted;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private readonly DateTimeOffset _base;
    private readonly SyntheticDeviceSources _src;

    public CrossSourceCorrelationTests()
    {
        _base = DeviceSnapshotMarker.Normalize(DateTimeOffset.UtcNow);
        _src = new SyntheticDeviceSources { IntuneNow = _base };
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        ctx.Tenants.Add(new Tenant { Id = TenantA, Name = "Cliente Sintético C", Slug = "sintetico-c", Status = TenantStatus.Active });
        ctx.Tenants.Add(new Tenant { Id = TenantB, Name = "Cliente Sintético D", Slug = "sintetico-d", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ---- apoio ------------------------------------------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenant) => new(_options, new SystemTenantContext(tenant));

    private Guid Seed(Guid tenant, ConnectorCapability capability, string directory, string? name = null)
    {
        using var db = NewContext(tenant);
        var cfg = SyntheticDeviceSources.Connector(tenant, capability, directory);
        if (name is not null) cfg.DisplayName = name;
        db.Connectors.Add(cfg);
        db.SaveChanges();
        return cfg.Id;
    }

    private Task<PullIngestionResult> Sync(Guid connectorId, Guid? tenant = null, SyntheticDeviceSources? src = null,
        Func<string, CancellationToken, Task>? checkpoint = null) =>
        (src ?? _src).SyncAsync(_options, tenant ?? TenantA, connectorId, checkpoint);

    private static string Q(string s) => SyntheticDeviceSources.Q(s);
    private static string Page(params string[] items) => SyntheticDeviceSources.Page(items);

    /// <summary>Máquina do Defender com atividade informada relativa ao relógio-base (padrão: 1 h antes).</summary>
    private string Machine(string id, string dns, string? deviceId, DateTimeOffset? lastSeen = null) =>
        "{\"id\":" + Q(id) + ",\"osPlatform\":\"Windows11\",\"lastSeen\":" + Q((lastSeen ?? _base.AddHours(-1)).ToString("O")) +
        ",\"computerDnsName\":" + Q(dns) + (deviceId is null ? "" : ",\"aadDeviceId\":" + Q(deviceId)) + "}";

    /// <summary>managedDevice do Intune (padrão: não conforme e sem criptografia, sincronizado 1 h antes).</summary>
    private string Device(string id, string? deviceId, string compliance = "noncompliant", bool? encrypted = false,
        DateTimeOffset? lastSync = null) =>
        "{\"id\":" + Q(id) + ",\"complianceState\":" + Q(compliance) + ",\"operatingSystem\":\"Windows\"" +
        ",\"lastSyncDateTime\":" + Q((lastSync ?? _base.AddHours(-1)).ToString("O")) +
        (encrypted is null ? "" : ",\"isEncrypted\":" + (encrypted.Value ? "true" : "false")) +
        (deviceId is null ? "" : ",\"azureADDeviceId\":" + Q(deviceId)) + "}";

    private static string Relation(string machine, string cve) => SyntheticDeviceSources.Relation(machine, cve);

    private static string Catalog(params string[] cves) =>
        Page(cves.Select(c => "{\"id\":" + Q(c) + ",\"name\":" + Q(c) + ",\"severity\":\"High\",\"cvssV3\":8.1}").ToArray());

    private static void DefenderData(SyntheticDeviceSources src, string[] machines, (string Machine, string Cve)[] relations)
    {
        src.DefenderMachines = Page(machines);
        src.DefenderRelations = Page(relations.Select(r => Relation(r.Machine, r.Cve)).ToArray());
        src.DefenderCves = Catalog(relations.Select(r => r.Cve).Distinct().ToArray());
    }

    private async Task<Guid> AssetOf(Guid connectorId, string externalId, Guid? tenant = null)
    {
        await using var db = NewContext(tenant ?? TenantA);
        return (await db.AssetSourceBindings.AsNoTracking()
            .SingleAsync(b => b.ConnectorConfigId == connectorId && b.ExternalId == externalId)).AssetId;
    }

    private async Task<ConnectorConfig> ConnectorOf(Guid connectorId, Guid? tenant = null)
    {
        await using var db = NewContext(tenant ?? TenantA);
        return await db.Connectors.AsNoTracking().SingleAsync(c => c.Id == connectorId);
    }

    private ICrossSourceCorrelationQuery Query(AegisScoreDbContext db, DateTimeOffset? now, CrossSourceCorrelationOptions? options) =>
        new CrossSourceCorrelationQuery(db, new FakeTimeProvider(now ?? _base.AddHours(2)),
            Options.Create(options ?? new CrossSourceCorrelationOptions()));

    /// <summary>Detalhe pelo CONTROLLER real (papel sem diagnóstico).</summary>
    private async Task<ActionResult<AssetCrossSourceDto>> DetailAction(
        Guid assetId, Guid? tenant = null, int cvePage = 1, int cvePageSize = 10,
        DateTimeOffset? now = null, CrossSourceCorrelationOptions? options = null)
    {
        await using var db = NewContext(tenant ?? TenantA);
        var controller = new AssetsController(db, new AssetSourceQuery(db))
        {
            ControllerContext = SyntheticDeviceSources.ControllerContextFor("Analyst"),
        };
        return await controller.Correlations(assetId, Query(db, now, options), cvePage, cvePageSize, CancellationToken.None);
    }

    private async Task<AssetCrossSourceDto> Detail(
        Guid assetId, Guid? tenant = null, int cvePage = 1, int cvePageSize = 10,
        DateTimeOffset? now = null, CrossSourceCorrelationOptions? options = null) =>
        (await DetailAction(assetId, tenant, cvePage, cvePageSize, now, options)).Value!;

    /// <summary>Lista pelo CONTROLLER real da Central (a rota não depende da leitura composta das filas).</summary>
    private async Task<ActionResult<CrossSourceSituationListDto>> ListAction(
        Guid? tenant = null, string? state = null, string? rule = null, int page = 1, int pageSize = 10,
        DateTimeOffset? now = null)
    {
        await using var db = NewContext(tenant ?? TenantA);
        var controller = new PrioritiesController(null!)
        {
            ControllerContext = SyntheticDeviceSources.ControllerContextFor("Analyst"),
        };
        return await controller.Correlations(Query(db, now, null), state, rule, page, pageSize, CancellationToken.None);
    }

    private async Task<CrossSourceSituationListDto> List(
        Guid? tenant = null, string? state = null, string? rule = null, int page = 1, int pageSize = 10, DateTimeOffset? now = null) =>
        (CrossSourceSituationListDto)((OkObjectResult)(await ListAction(tenant, state, rule, page, pageSize, now)).Result!).Value!;

    private static CrossSourceRuleResultDto Rule(AssetCrossSourceDto d, string code) => d.Rules.Single(r => r.RuleCode == code);

    /// <summary>Dispositivo X vinculado nas duas fontes, com as CVEs pedidas e o estado do Intune pedido.</summary>
    private async Task<(Guid Defender, Guid Intune, Guid Asset)> LinkedDeviceAsync(
        string[] cves, string compliance = "noncompliant", bool? encrypted = false)
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        DefenderData(_src, new[] { Machine("mde-0001", "pc-01.demo.example.com", DevX) },
            cves.Select(c => ("mde-0001", c)).ToArray());
        _src.IntuneDevices = Page(Device("int-0001", DevX, compliance, encrypted));
        await Sync(defender);
        await Sync(intune);
        return (defender, intune, await AssetOf(defender, "mde-0001"));
    }

    // ================= Regras e proveniência ======================================================================

    [Fact]
    public async Task BothConditions_OnAValidStrongLink_AreIdentified_WithOwnDatesProvenanceAndNoTechnicalIdentifiers()
    {
        var (defender, intune, assetId) = await LinkedDeviceAsync(new[] { "CVE-2024-1002", "CVE-2024-1001" });
        var d = await ConnectorOf(defender);
        var i = await ConnectorOf(intune);
        d.DeviceSnapshotOutcome.Should().Be(DeviceSnapshotOutcome.Complete, "a passada completa registra o desfecho");
        i.DeviceSnapshotOutcome.Should().Be(DeviceSnapshotOutcome.Complete);

        var dto = await Detail(assetId);
        dto.Heading.Should().Be("Situações identificadas entre fontes");
        dto.EvaluatedAt.Should().Be(_base.AddHours(2), "o momento do cálculo vem do relógio injetado");
        dto.Rules.Should().HaveCount(2).And.OnlyContain(r => r.State == CrossSourceStates.Identified && !r.HasCaveats);

        var a = Rule(dto, RuleA);
        a.RuleVersion.Should().Be(1);
        a.OpenCveCount.Should().Be(2);
        a.StateLabel.Should().Be("Situação identificada");
        a.Summary.Should().Be("No mesmo dispositivo, o Microsoft Defender reporta 2 vulnerabilidade(s) em aberto e o " +
            "Microsoft Intune informa o dispositivo como não conforme.");
        a.SupportingData.Should().Contain("mesmo identificador de dispositivo do Microsoft Entra")
            .And.Contain(CrossSourceCorrelationEvaluator.Utc(d.DeviceSnapshotWatermark!.Value))
            .And.Contain(CrossSourceCorrelationEvaluator.Utc(i.DeviceSnapshotWatermark!.Value));
        a.WhatToVerify.Should().Contain("Revise as vulnerabilidades").And.Contain("não aplica correção automaticamente");
        a.Limitation.Should().Contain("Não afirma que a não conformidade permita explorar")
            .And.Contain("Não é incidente confirmado, risco calculado, prioridade nem exposição à internet");
        Rule(dto, RuleB).Limitation.Should().Contain("Não afirma que a falta de criptografia permita explorar");
        a.Criteria.Should().Contain(c => c.Contains("noncompliant"));

        // Cada evidência com a SUA data: aquisição do AEGIS ≠ atividade informada pela fonte.
        dto.Evidence.Should().HaveCount(2).And.OnlyContain(e => e.Eligible && e.AcquisitionState == CrossSourceAcquisitionStates.Current);
        var dv = dto.Evidence.Single(e => e.Role == "vulnerabilities");
        var mg = dto.Evidence.Single(e => e.Role == "deviceManagement");
        dv.AcquiredAt.Should().Be(d.DeviceSnapshotWatermark!.Value);
        mg.AcquiredAt.Should().Be(i.DeviceSnapshotWatermark!.Value);
        dv.AcquiredAt.Should().NotBe(mg.AcquiredAt, "fontes independentes não compartilham um horário único");
        mg.SourceActivityAt.Should().Be(_base.AddHours(-1));
        mg.ComplianceLabel.Should().Be("Não conforme, segundo a fonte");
        mg.EncryptionLabel.Should().Be("Sem criptografia, segundo a fonte");
        dv.LinkedAt.Should().NotBeNull();

        dto.Cves!.Total.Should().Be(2);
        dto.Cves.Items.Select(c => c.CveId).Should().Equal("CVE-2024-1001", "CVE-2024-1002");
        dto.Cves.Items.Should().OnlyContain(c => c.Sources.Count == 1
            && c.Sources[0].AcquiredAt == d.DeviceSnapshotWatermark
            && c.Sources[0].AcquisitionState == CrossSourceAcquisitionStates.Current);

        // Identificadores técnicos da vinculação nunca vão para a leitura comum.
        var json = JsonSerializer.Serialize(dto);
        foreach (var technical in new[] { DevX, DirA, "mde-0001", "int-0001" })
            json.Should().NotContain(technical);

        var list = await List();
        list.Summary.ReadingState.Should().Be(CrossSourceReadingStates.Available);
        list.Summary.SituationsIdentified.Should().Be(2);
        list.Summary.AssetsWithSituations.Should().Be(1);
        list.Summary.AssetsWithBothSources.Should().Be(1);
        list.Items.Select(x => x.RuleCode).Should().Equal(RuleA, RuleB);
        list.Items.Should().OnlyContain(x => x.OpenCveCount == 2 && !x.CvePreviewTruncated
            && x.CvePreview.SequenceEqual(new[] { "CVE-2024-1001", "CVE-2024-1002" }));
        list.Items[0].VulnerabilitiesAcquiredAt.Should().Be(d.DeviceSnapshotWatermark);
        list.Items[0].DeviceManagementAcquiredAt.Should().Be(i.DeviceSnapshotWatermark);
        JsonSerializer.Serialize(list).Should().NotContain(DevX).And.NotContain("mde-0001");
    }

    [Fact]
    public async Task OnlyOneCondition_IdentifiesOnlyThatRule_AndTheOtherSaysWhy()
    {
        var (_, _, assetId) = await LinkedDeviceAsync(new[] { "CVE-2024-1001" }, compliance: "compliant", encrypted: false);

        var dto = await Detail(assetId);
        var a = Rule(dto, RuleA);
        a.State.Should().Be(CrossSourceStates.NotIdentified);
        a.StateLabel.Should().Be("Nenhuma condição correspondente");
        a.Summary.Should().StartWith("Nenhuma condição correspondente nas evidências elegíveis.")
            .And.Contain("“Conforme, segundo a fonte”");
        a.OpenCveCount.Should().Be(1, "as vulnerabilidades continuam contadas, só não formam a situação desta regra");
        Rule(dto, RuleB).State.Should().Be(CrossSourceStates.Identified);

        (await List()).Items.Should().ContainSingle().Which.RuleCode.Should().Be(RuleB);
    }

    [Fact]
    public async Task VulnerabilityNoLongerReported_DoesNotSustainAnOpenCondition()
    {
        var (defender, _, assetId) = await LinkedDeviceAsync(new[] { "CVE-2024-1001" });
        Rule(await Detail(assetId), RuleA).State.Should().Be(CrossSourceStates.Identified);

        // Nova coleta COMPLETA do Defender sem a CVE: a observação passa a "não mais reportada".
        DefenderData(_src, new[] { Machine("mde-0001", "pc-01.demo.example.com", DevX) }, Array.Empty<(string, string)>());
        await Sync(defender);

        var dto = await Detail(assetId);
        var a = Rule(dto, RuleA);
        a.State.Should().Be(CrossSourceStates.NotIdentified);
        a.Summary.Should().Contain("não reporta vulnerabilidade em aberto")
            .And.Contain("Ausência de vulnerabilidades reportadas não comprova que o dispositivo esteja seguro");
        a.OpenCveCount.Should().Be(0);
        dto.Cves!.Total.Should().Be(0);
        dto.Cves.NoLongerReported.Should().Be(1, "a CVE não mais reportada é mostrada como tal, sem sustentar a condição");
    }

    [Fact]
    public async Task UnknownComplianceOrEncryption_IsInsufficientEvidence_NotAbsence()
    {
        var (_, _, assetId) = await LinkedDeviceAsync(new[] { "CVE-2024-1001" }, compliance: "unknown", encrypted: null);

        var dto = await Detail(assetId);
        Rule(dto, RuleA).State.Should().Be(CrossSourceStates.InsufficientEvidence);
        Rule(dto, RuleA).Reasons.Should().Contain(r => r.Contains("não informou conformidade determinada"));
        Rule(dto, RuleB).State.Should().Be(CrossSourceStates.InsufficientEvidence);
        Rule(dto, RuleB).Reasons.Should().Contain(r => r.Contains("não informou criptografia determinada"));
        var mg = dto.Evidence.Single(e => e.Role == "deviceManagement");
        mg.Eligible.Should().BeTrue("o registro é elegível; é o FATO que é desconhecido");
        mg.ComplianceLabel.Should().Be("Conformidade não informada");
        mg.EncryptionLabel.Should().Be("Criptografia não informada");
    }

    // ================= Completude, falha e publicação ==============================================================

    [Fact]
    public async Task PartialAcquisition_PositiveFactSustainsWithCaveat_ButZeroIsNotConclusive()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        // Uma relação órfã (máquina que não veio na lista) torna a coleta de vulnerabilidades PARCIAL.
        DefenderData(_src,
            new[] { Machine("mde-0001", "pc-01.demo.example.com", DevX), Machine("mde-0002", "pc-02.demo.example.com", DevY) },
            new[] { ("mde-0001", "CVE-2024-1001"), ("mde-orfa", "CVE-2024-1009") });
        _src.IntuneDevices = Page(Device("int-0001", DevX), Device("int-0002", DevY, encrypted: true));
        (await Sync(defender)).Vulnerabilities!.WasComplete.Should().BeFalse();
        await Sync(intune);
        (await ConnectorOf(defender)).DeviceSnapshotOutcome.Should().Be(DeviceSnapshotOutcome.Partial);

        var x = await Detail(await AssetOf(defender, "mde-0001"));
        var ax = Rule(x, RuleA);
        ax.State.Should().Be(CrossSourceStates.Identified);
        ax.HasCaveats.Should().BeTrue();
        ax.StateLabel.Should().Be("Situação identificada, com ressalvas");
        ax.Caveats.Should().Contain(c => c.Code == "partialAcquisition" && c.Text.Contains("fato positivo"));
        x.Evidence.Single(e => e.Role == "vulnerabilities").AcquisitionState.Should().Be(CrossSourceAcquisitionStates.CurrentPartial);

        // Dispositivo sem CVE numa coleta PARCIAL: zero não prova ausência — insuficiência, não "nenhuma condição".
        var y = await Detail(await AssetOf(defender, "mde-0002"));
        Rule(y, RuleA).State.Should().Be(CrossSourceStates.InsufficientEvidence);
        Rule(y, RuleA).Reasons.Should().Contain(r => r.Contains("parcial"));
        Rule(y, RuleB).State.Should().Be(CrossSourceStates.NotIdentified, "o Intune informa criptografia — determinado");
    }

    [Fact]
    public async Task RecentFailures_PreservePreviousEvidence_WithCaveats()
    {
        var (defender, intune, assetId) = await LinkedDeviceAsync(new[] { "CVE-2024-1001" });
        var before = await ConnectorOf(defender);

        // Defender: a coleta falha antes de publicar (403 nas máquinas) → conector Failed, fotografia anterior intacta.
        _src.DefenderRoute = req => req.RequestUri!.AbsolutePath.Contains("/api/machines") ? (HttpStatusCode.Forbidden, "{}") : null;
        await FluentActions.Awaiting(() => Sync(defender)).Should().ThrowAsync<Exception>();
        (await ConnectorOf(defender)).LastStatus.Should().Be(ConnectorStatus.Failed);
        (await ConnectorOf(defender)).DeviceSnapshotWatermark.Should().Be(before.DeviceSnapshotWatermark);

        // Intune: a dimensão de dispositivos é negada (403) → tentativa falha registrada, fatos por dispositivo preservados.
        _src.IntuneRoute = _ => (HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden"}}""");
        await Sync(intune);

        var dto = await Detail(assetId);
        foreach (var r in dto.Rules)
        {
            r.State.Should().Be(CrossSourceStates.Identified, "a última evidência válida continua sustentando, com ressalva");
            r.Caveats.Where(c => c.Code == "latestAttemptFailed").Should().HaveCount(2, "uma ressalva por fonte");
        }
        dto.Evidence.Should().OnlyContain(e => e.LatestAttemptFailed && e.AcquisitionState == CrossSourceAcquisitionStates.Current,
            "a tentativa falha é da fonte; os fatos exibidos são da última aquisição publicada");
    }

    [Fact]
    public async Task InterruptedPublication_IsNotSilentlyMixed_EachCveCarriesItsAcquisition()
    {
        var (defender, _, assetId) = await LinkedDeviceAsync(new[] { "CVE-2024-1001" });
        var first = (await ConnectorOf(defender)).DeviceSnapshotWatermark!.Value;

        // Nova aquisição com uma CVE a mais, interrompida logo depois de publicar a presença dos dispositivos: a marca
        // avançou, mas nenhum lote de vulnerabilidades foi publicado.
        DefenderData(_src, new[] { Machine("mde-0001", "pc-01.demo.example.com", DevX) },
            new[] { ("mde-0001", "CVE-2024-1001"), ("mde-0001", "CVE-2024-1002") });
        await FluentActions.Awaiting(() => Sync(defender, checkpoint: (point, _) =>
                point == DeviceIdentityResolver.CheckpointPresenceCommitted
                    ? throw new InvalidOperationException("interrupção sintética")
                    : Task.CompletedTask))
            .Should().ThrowAsync<InvalidOperationException>();
        var c = await ConnectorOf(defender);
        c.DeviceSnapshotOutcome.Should().Be(DeviceSnapshotOutcome.Publishing);
        c.DeviceSnapshotWatermark.Should().BeAfter(first);

        var dto = await Detail(assetId);
        var a = Rule(dto, RuleA);
        a.State.Should().Be(CrossSourceStates.Identified);
        a.OpenCveCount.Should().Be(1, "a CVE da aquisição não publicada nunca aparece");
        a.Caveats.Select(n => n.Code).Should().Contain(new[] { "publicationNotConcluded", "previousAcquisition", "latestAttemptFailed" });
        dto.Evidence.Single(e => e.Role == "vulnerabilities").AcquisitionState
            .Should().Be(CrossSourceAcquisitionStates.CurrentUnconcluded);
        var cve = dto.Cves!.Items.Should().ContainSingle().Subject;
        cve.CveId.Should().Be("CVE-2024-1001");
        cve.Sources.Single().AcquiredAt.Should().Be(first, "cada CVE mantém a data da aquisição que a observou");
        cve.Sources.Single().AcquisitionState.Should().Be(CrossSourceAcquisitionStates.Previous);

        // A próxima aquisição completa conclui a publicação: tudo atual, sem ressalvas.
        _src.DefenderRoute = null;
        await Sync(defender);
        (await ConnectorOf(defender)).DeviceSnapshotOutcome.Should().Be(DeviceSnapshotOutcome.Complete);
        var done = Rule(await Detail(assetId), RuleA);
        done.HasCaveats.Should().BeFalse();
        done.OpenCveCount.Should().Be(2);
    }

    // ================= Política temporal =========================================================================

    [Fact]
    public async Task TemporalPolicy_OutOfWindow_InactiveInSource_DivergentDates_AndConfigurableWindow()
    {
        var (defender, intune, assetId) = await LinkedDeviceAsync(new[] { "CVE-2024-1001" });

        // (a) Oito dias depois, com a política padrão (7 dias): nenhuma evidência sustenta; cada exclusão diz por quê.
        var later = _base.AddDays(8);
        var old = await Detail(assetId, now: later);
        old.Rules.Should().OnlyContain(r => r.State == CrossSourceStates.InsufficientEvidence);
        old.Evidence.Should().OnlyContain(e => e.Eligibility == CrossSourceEligibility.OutOfPolicy
            && e.ExclusionReason!.Contains("fora da política temporal (até 7 dia(s))"));
        old.Policy.MaxEvidenceAgeDays.Should().Be(7);
        old.Policy.Description.Should().Contain("não exigência normativa nem garantia de segurança");

        // A janela é configurável e testável: com 10 dias, a mesma evidência volta a sustentar.
        var wider = await Detail(assetId, now: later, options: new CrossSourceCorrelationOptions { MaxEvidenceAgeDays = 10 });
        wider.Rules.Should().OnlyContain(r => r.State == CrossSourceStates.Identified);
        new CrossSourceCorrelationOptions { MaxEvidenceAgeDays = 0 }.IsValid.Should().BeFalse("valor inválido não vira janela silenciosa");

        // (b) Dispositivo sem atividade recente informada pela fonte: não é tratado como atualmente observado.
        DefenderData(_src, new[]
            {
                Machine("mde-0001", "pc-01.demo.example.com", DevX),
                Machine("mde-0003", "pc-03.demo.example.com", DevZ),
            },
            new[] { ("mde-0001", "CVE-2024-1001"), ("mde-0003", "CVE-2024-1003") });
        await Sync(defender);
        _src.IntuneDevices = Page(Device("int-0001", DevX), Device("int-0003", DevZ, lastSync: _base.AddDays(-40)));
        await Sync(intune);
        var stale = await Detail(await AssetOf(defender, "mde-0003"));
        stale.Rules.Should().OnlyContain(r => r.State == CrossSourceStates.InsufficientEvidence);
        stale.Evidence.Single(e => e.Role == "deviceManagement").Eligibility.Should().Be(CrossSourceEligibility.InactiveInSource);

        // (c) Datas divergentes (outro tenant): o Intune foi lido dois dias antes do Defender — a situação existe, com
        // ressalva e as DUAS datas; não se exige o mesmo horário.
        var defenderB = Seed(TenantB, ConnectorCapability.VulnerabilityScanner, DirA);
        var intuneB = Seed(TenantB, ConnectorCapability.ConfigAnalyzer, DirA);
        DefenderData(_src, new[] { Machine("mde-0001", "pc-01.demo.example.com", DevX) }, new[] { ("mde-0001", "CVE-2024-1001") });
        await Sync(defenderB, TenantB);
        _src.IntuneNow = _base.AddDays(-2);
        _src.IntuneDevices = Page(Device("int-0001", DevX, lastSync: _base.AddDays(-2).AddHours(-1)));
        await Sync(intuneB, TenantB);
        var gap = Rule(await Detail(await AssetOf(defenderB, "mde-0001", TenantB), TenantB), RuleA);
        gap.State.Should().Be(CrossSourceStates.Identified);
        var note = gap.Caveats.Should().ContainSingle(n => n.Code == "acquisitionGap").Subject;
        note.Text.Should().Contain(CrossSourceCorrelationEvaluator.Utc((await ConnectorOf(defenderB, TenantB)).DeviceSnapshotWatermark!.Value))
            .And.Contain(CrossSourceCorrelationEvaluator.Utc((await ConnectorOf(intuneB, TenantB)).DeviceSnapshotWatermark!.Value));
    }

    // ================= Vínculo e registros ========================================================================

    [Fact]
    public async Task LinkConflict_BlocksTheCombination_AndKeepsIndividualEvidence()
    {
        var (defender, _, assetId) = await LinkedDeviceAsync(new[] { "CVE-2024-1001" });

        // A mesma máquina passa a informar OUTRO identificador de diretório: conflito preservado, binding não move.
        DefenderData(_src, new[] { Machine("mde-0001", "pc-01.demo.example.com", DevY) }, new[] { ("mde-0001", "CVE-2024-1001") });
        await Sync(defender);

        var dto = await Detail(assetId);
        dto.Rules.Should().OnlyContain(r => r.State == CrossSourceStates.LinkConflict && r.OpenCveCount == null);
        var a = Rule(dto, RuleA);
        a.StateLabel.Should().Be("Vínculo em conflito — combinação indisponível");
        a.Reasons.Should().ContainSingle().Which.Should().Contain("Conflito: identificador mudou");
        a.WhatToVerify.Should().Contain("conflito de vínculo");
        dto.Cves.Should().BeNull("sem vínculo resolvido, nenhuma CVE é atribuída à combinação");
        dto.Evidence.Should().HaveCount(2, "as evidências de cada fonte continuam visíveis");
        dto.Evidence.Single(e => e.Role == "vulnerabilities").Eligibility.Should().Be(CrossSourceEligibility.Conflict);

        var conflicts = await List(state: CrossSourceStates.LinkConflict);
        conflicts.Total.Should().Be(2);
        conflicts.Summary.ByRule.Should().OnlyContain(t => t.LinkConflict == 1 && t.Identified == 0);
        (await List()).Summary.SituationsIdentified.Should().Be(0);
    }

    [Fact]
    public async Task ContradictoryRecordsOfTheSameSource_AreNotResolvedByChoice_RegardlessOfOrder()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        DefenderData(_src,
            new[] { Machine("mde-0001", "pc-01.demo.example.com", DevX), Machine("mde-0002", "pc-02.demo.example.com", DevY) },
            new[] { ("mde-0001", "CVE-2024-1001"), ("mde-0002", "CVE-2024-1002") });
        // X: dois registros do Intune com o MESMO dispositivo de diretório e fatos opostos. Y: um fato determinado e um
        // desconhecido — desconhecido não contradiz.
        var x1 = Device("int-0001", DevX, "noncompliant", encrypted: true);
        var x2 = Device("int-0002", DevX, "compliant", encrypted: false);
        var y1 = Device("int-0003", DevY, "noncompliant", encrypted: null);
        var y2 = Device("int-0004", DevY, "unknown", encrypted: false);
        _src.IntuneDevices = Page(x1, x2, y1, y2);
        await Sync(defender);
        await Sync(intune);

        var assetX = await AssetOf(defender, "mde-0001");
        var first = await Detail(assetX);
        first.Rules.Should().OnlyContain(r => r.State == CrossSourceStates.ContradictoryEvidence);
        Rule(first, RuleA).Reasons.Single().Should().Contain("Não conforme, segundo a fonte").And.Contain("Conforme, segundo a fonte")
            .And.Contain("Nenhum foi escolhido");
        first.Evidence.Count(e => e.Role == "deviceManagement").Should().Be(2, "a divergência é preservada");

        var y = await Detail(await AssetOf(defender, "mde-0002"));
        y.Rules.Should().OnlyContain(r => r.State == CrossSourceStates.Identified);

        // Mesmos registros em outra ordem de página: mesmo resultado (nada depende do "primeiro").
        _src.IntuneDevices = Page(y2, x2, y1, x1);
        await Sync(intune);
        (await Detail(assetX)).Rules.Should().OnlyContain(r => r.State == CrossSourceStates.ContradictoryEvidence);
    }

    [Fact]
    public async Task StaleDuplicateRecordOfTheSameDefenderConnector_MakesAttributionAmbiguous()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        // Duas máquinas do Defender com o mesmo dispositivo de diretório; a antiga (sem atividade há 60 dias) é quem
        // reporta a CVE. A observação é por (conector, ativo×CVE): não dá para atribuí-la só ao registro elegível.
        DefenderData(_src,
            new[]
            {
                Machine("mde-0001", "pc-01.demo.example.com", DevX),
                Machine("mde-0009", "pc-01-antigo.demo.example.com", DevX, lastSeen: _base.AddDays(-60)),
            },
            new[] { ("mde-0009", "CVE-2024-1009") });
        _src.IntuneDevices = Page(Device("int-0001", DevX));
        await Sync(defender);
        await Sync(intune);

        var dto = await Detail(await AssetOf(defender, "mde-0001"));
        dto.Rules.Should().OnlyContain(r => r.State == CrossSourceStates.InsufficientEvidence);
        Rule(dto, RuleA).Reasons.Should().Contain(r => r.Contains("não podem ser atribuídas apenas ao registro elegível"));
        var vuln = dto.Evidence.Where(e => e.Role == "vulnerabilities").ToList();
        vuln.Select(e => e.Eligibility).Should().BeEquivalentTo(new[]
            { CrossSourceEligibility.AmbiguousAttribution, CrossSourceEligibility.InactiveInSource });
    }

    [Fact]
    public async Task SameIdentifierInOtherTenantOrDirectory_NeverCombines_AndReadsAreTenantScoped()
    {
        // Tenant A: Defender no diretório A e Intune no diretório B, com o MESMO identificador de dispositivo.
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intuneOtherDir = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirB);
        DefenderData(_src, new[] { Machine("mde-0001", "pc-01.demo.example.com", DevX) }, new[] { ("mde-0001", "CVE-2024-1001") });
        _src.IntuneDevices = Page(Device("int-0001", DevX));
        await Sync(defender);
        await Sync(intuneOtherDir);
        // Tenant B: Intune no diretório A com o mesmo identificador.
        var intuneB = Seed(TenantB, ConnectorCapability.ConfigAnalyzer, DirA);
        await Sync(intuneB, TenantB);

        var assetA = await AssetOf(defender, "mde-0001");
        (await AssetOf(intuneOtherDir, "int-0001")).Should().NotBe(assetA, "diretório diferente é outro dispositivo");
        var dto = await Detail(assetA);
        dto.Rules.Should().OnlyContain(r => r.State == CrossSourceStates.NotEvaluated);
        Rule(dto, RuleA).Summary.Should().Contain("não tem registro do Microsoft Intune");

        var listA = await List();
        listA.Summary.ReadingState.Should().Be(CrossSourceReadingStates.Available);
        listA.Summary.AssetsWithBothSources.Should().Be(0);
        listA.Items.Should().BeEmpty();
        (await List(TenantB)).Summary.ReadingState.Should().Be(CrossSourceReadingStates.NoSource, "o tenant B só tem Intune");

        // Um tenant não lê o ativo de outro: não existe para ele.
        (await DetailAction(assetA, TenantB)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task MultipleRecordsAndCves_OneSituationPerAssetAndRule_CvesDeduplicatedWithProvenance()
    {
        // O tenant tem UMA integração por produto (índice único); a multiplicidade real é de REGISTROS: duas máquinas do
        // Defender e dois managedDevices do Intune com o mesmo dispositivo de diretório, e CVEs repetidas entre máquinas.
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        DefenderData(_src,
            new[]
            {
                Machine("mde-0001", "pc-01.demo.example.com", DevX),
                Machine("mde-0101", "pc-01.demo.example.com", DevX),
            },
            new[]
            {
                ("mde-0001", "CVE-2024-1001"), ("mde-0001", "CVE-2024-1002"),
                ("mde-0101", "CVE-2024-1002"), ("mde-0101", "CVE-2024-1003"),
            });
        _src.IntuneDevices = Page(Device("int-0001", DevX), Device("int-0002", DevX));
        await Sync(defender);
        await Sync(intune);

        var assetId = await AssetOf(defender, "mde-0001");
        (await AssetOf(defender, "mde-0101")).Should().Be(assetId, "a mesma chave forte, o mesmo ativo");
        (await AssetOf(intune, "int-0002")).Should().Be(assetId);
        var dto = await Detail(assetId);
        var a = Rule(dto, RuleA);
        a.State.Should().Be(CrossSourceStates.Identified);
        a.OpenCveCount.Should().Be(3, "CVE-2024-1002 reportada pelas duas máquinas conta UMA vez");
        a.SupportingData.Should().Contain("CVEs distintas em aberto que sustentam a situação: 3");
        dto.Cves!.Total.Should().Be(3);
        dto.Cves.Items.Select(c => c.CveId).Should().Equal("CVE-2024-1001", "CVE-2024-1002", "CVE-2024-1003");
        dto.Cves.Items.Should().OnlyContain(c => c.Sources.Count == 1, "uma linha por CVE, com a fonte que a reporta");
        dto.Evidence.Should().HaveCount(4).And.OnlyContain(e => e.Eligible, "registros concordantes sustentam juntos");

        var list = await List();
        list.Items.Should().HaveCount(2, "uma situação por ativo × regra, não um cartão por CVE ou por registro");
        list.Items.Should().OnlyContain(x => x.OpenCveCount == 3);
        list.Summary.AssetsWithBothSources.Should().Be(1);
    }

    // ================= Central: paginação, totais, prévias, filtros e estados ======================================

    [Fact]
    public async Task List_PaginationTotalsPreviewsFiltersAndValidation()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        var five = Enumerable.Range(1, 5).Select(n => ("mde-0001", $"CVE-2024-200{n}")).ToArray();
        DefenderData(_src,
            new[]
            {
                Machine("mde-0003", "pc-03.demo.example.com", DevZ),
                Machine("mde-0001", "pc-01.demo.example.com", DevX),
                Machine("mde-0002", "pc-02.demo.example.com", DevY),
            },
            five.Concat(new[] { ("mde-0002", "CVE-2024-2001"), ("mde-0003", "CVE-2024-3001"), ("mde-0003", "CVE-2024-3002") }).ToArray());
        _src.IntuneDevices = Page(Device("int-0001", DevX), Device("int-0002", DevY), Device("int-0003", DevZ));
        await Sync(defender);
        await Sync(intune);

        var page1 = await List(pageSize: 4);
        page1.Total.Should().Be(6, "unidade: situações (3 ativos × 2 regras)");
        page1.Summary.SituationsIdentified.Should().Be(6);
        page1.Summary.AssetsWithSituations.Should().Be(3);
        page1.Summary.AssetsWithBothSources.Should().Be(3);
        page1.Summary.EvaluationTruncated.Should().BeFalse();
        page1.Items.Select(x => (x.AssetName, x.RuleCode)).Should().Equal(
            ("pc-01.demo.example.com", RuleA), ("pc-01.demo.example.com", RuleB),
            ("pc-02.demo.example.com", RuleA), ("pc-02.demo.example.com", RuleB));
        var pc01 = page1.Items[0];
        pc01.OpenCveCount.Should().Be(5);
        pc01.CvePreview.Should().Equal("CVE-2024-2001", "CVE-2024-2002", "CVE-2024-2003");
        pc01.CvePreviewTruncated.Should().BeTrue("a prévia não se apresenta como total");
        var page2 = await List(page: 2, pageSize: 4);
        page2.Items.Select(x => x.AssetName).Should().Equal("pc-03.demo.example.com", "pc-03.demo.example.com");

        (await List(rule: RuleB)).Total.Should().Be(3);
        var filtered = await List(state: CrossSourceStates.NotIdentified);
        filtered.Total.Should().Be(0);
        filtered.Summary.SituationsIdentified.Should().Be(6, "o filtro não altera o resumo");
        (await List(pageSize: 500)).PageSize.Should().Be(CrossSourceCorrelationQuery.MaxListPageSize);

        // CVEs do ativo paginadas no banco, com o total na unidade certa.
        var detail = await Detail(pc01.AssetId, cvePage: 3, cvePageSize: 2);
        detail.Cves!.Total.Should().Be(5);
        detail.Cves.Items.Select(c => c.CveId).Should().Equal("CVE-2024-2005");

        (await ListAction(state: "risco")).Result.Should().BeOfType<BadRequestObjectResult>();
        (await ListAction(rule: "XS-INEXISTENTE")).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ReadingStates_NoSource_NeverCollected_Available()
    {
        (await List()).Summary.ReadingState.Should().Be(CrossSourceReadingStates.NoSource);

        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        var never = await List();
        never.Summary.ReadingState.Should().Be(CrossSourceReadingStates.NeverCollected);
        never.Summary.AssetsWithBothSources.Should().Be(0);

        DefenderData(_src, new[] { Machine("mde-0001", "pc-01.demo.example.com", DevX) }, new[] { ("mde-0001", "CVE-2024-1001") });
        _src.IntuneDevices = Page(Device("int-0001", DevX, "compliant", encrypted: true));
        await Sync(defender);
        await Sync(intune);
        var zero = await List();
        zero.Summary.ReadingState.Should().Be(CrossSourceReadingStates.Available);
        zero.Summary.AssetsWithBothSources.Should().Be(1);
        zero.Summary.SituationsIdentified.Should().Be(0, "zero apurado, com a população avaliada informada");
        zero.Summary.ByRule.Should().OnlyContain(t => t.NotIdentified == 1);
    }
}
