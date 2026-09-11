using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Api.Controllers;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Queries;
using AegisScore.Connectors.Microsoft.Defender;
using AegisScore.Connectors.Microsoft.Intune;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using AegisScore.Infrastructure.Scoring;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// [AEGIS-ENTITY-RESOLUTION-01] Resolução de dispositivos entre fontes pelo CAMINHO REAL: conectores reais do
/// Defender e do Intune sobre HTTP SINTÉTICO (URLs oficiais, sem rede, sem credencial real) → executor de ingestão →
/// autoridade única de resolução → banco descartável (SQLite) → leitura (query + controller). Os casos passam pelo
/// parser do conector, pela persistência e pela consulta; a única exceção declarada é o grupo "Contrato da autoridade",
/// que chama a autoridade diretamente para provar que a regra das repetições também vale no contrato dela.
///
/// Cenário SINTÉTICO: tenants, diretórios e identificadores são GUIDs inventados; nomes em demo.example.com.
/// </summary>
public sealed class DeviceResolutionTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("a1000000-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("b2000000-0000-0000-0000-00000000000b");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private readonly SyntheticDeviceSources _src = new();

    public DeviceResolutionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        ctx.Tenants.Add(new Tenant { Id = TenantA, Name = "Cliente Sintético A", Slug = "sintetico-a", Status = TenantStatus.Active });
        ctx.Tenants.Add(new Tenant { Id = TenantB, Name = "Cliente Sintético B", Slug = "sintetico-b", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private AegisScoreDbContext NewContext(Guid? tenant) => new(_options, new SystemTenantContext(tenant));

    private Guid Seed(Guid tenant, ConnectorCapability capability, string directory)
    {
        using var db = NewContext(tenant);
        var cfg = SyntheticDeviceSources.Connector(tenant, capability, directory);
        db.Connectors.Add(cfg);
        db.SaveChanges();
        return cfg.Id;
    }

    private Task<PullIngestionResult> Sync(Guid connectorId, Guid? tenant = null) =>
        _src.SyncAsync(_options, tenant ?? TenantA, connectorId);

    private async Task<List<AssetSourceBinding>> Bindings(Guid tenant)
    {
        await using var db = NewContext(tenant);
        return await db.AssetSourceBindings.AsNoTracking().ToListAsync();
    }

    private async Task<AssetSourceBinding> Binding(Guid connectorId, string externalId, Guid? tenant = null)
    {
        await using var db = NewContext(tenant ?? TenantA);
        return await db.AssetSourceBindings.AsNoTracking()
            .SingleAsync(b => b.ConnectorConfigId == connectorId && b.ExternalId == externalId);
    }

    private async Task<Asset> AssetOf(Guid assetId, Guid? tenant = null)
    {
        await using var db = NewContext(tenant ?? TenantA);
        return await db.Assets.AsNoTracking().SingleAsync(a => a.Id == assetId);
    }

    private async Task<AssetSourceSummaryDto> Summary(Guid assetId, Guid? tenant = null)
    {
        await using var db = NewContext(tenant ?? TenantA);
        return (await new AssetSourceQuery(db).GetSummariesAsync(new[] { assetId }))[assetId];
    }

    private async Task<AssetSourcesDto?> Sources(Guid assetId, string role = "Analyst", Guid? tenant = null)
    {
        await using var db = NewContext(tenant ?? TenantA);
        var controller = new AssetsController(db, new AssetSourceQuery(db))
        {
            ControllerContext = SyntheticDeviceSources.ControllerContextFor(role),
        };
        return (await controller.Sources(assetId, CancellationToken.None)).Value;
    }

    private static string Q(string s) => SyntheticDeviceSources.Q(s);
    private static string Page(params string[] items) => SyntheticDeviceSources.Page(items);

    private const string DirA = SyntheticDeviceSources.DirA;
    private const string DirB = SyntheticDeviceSources.DirB;
    private const string DevX = SyntheticDeviceSources.DevX;
    private const string DevY = SyntheticDeviceSources.DevY;
    private const string DefenderLabel = MicrosoftDefenderVulnerabilityConnector.SourceLabel;
    private const string IntuneLabel = MicrosoftIntuneDevicePostureConnector.SourceLabel;

    // ================= Vínculo forte nas duas ordens =============================================================

    [Fact]
    public async Task DefenderThenIntune_SameDirectoryAndDeviceId_OneAsset_TwoBindings_WithVulnerabilityAndSoftware()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.DefenderWithVulnerabilityAndSoftware(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        // Caixa diferente na fonte: a chave é o valor NORMALIZADO, então é o mesmo dispositivo.
        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", Q(DevX.ToUpperInvariant())));

        var d = await Sync(defender);
        var i = await Sync(intune);

        d.Vulnerabilities!.Resolution!.KeysEstablished.Should().Be(1);
        i.DeviceResolution!.Linked.Should().Be(1);
        i.DeviceResolution.AssetsCreated.Should().Be(0, "o dispositivo já tinha ativo canônico estabelecido pelo Defender");

        await using (var db = NewContext(TenantA))
        {
            var asset = await db.Assets.SingleAsync();
            var bindings = await db.AssetSourceBindings.ToListAsync();
            bindings.Should().HaveCount(2).And.OnlyContain(b =>
                b.AssetId == asset.Id && b.ResolutionState == AssetBindingResolutionState.Linked);
            bindings.Select(b => b.ConnectorConfigId).Should().BeEquivalentTo(new[] { defender, intune },
                "um binding DISTINTO por fonte");
            var key = await db.AssetStrongIdentifiers.SingleAsync();
            key.AssetId.Should().Be(asset.Id);
            key.DirectoryNamespace.Should().Be(DirA);
            key.IdentifierValue.Should().Be(DevX);
            (await db.AssetThreatExposures.SingleAsync()).AssetId.Should().Be(asset.Id, "vulnerabilidade no ativo correto");
            (await db.SoftwareInstallations.SingleAsync()).AssetId.Should().Be(asset.Id, "software no ativo correto");
            asset.Name.Should().Be("pc-01.demo.example.com");
        }

        var assetId = (await Binding(defender, "m-1")).AssetId;
        var summary = await Summary(assetId);
        summary.ActiveSourceCount.Should().Be(2);
        summary.CrossSourceState.Should().Be(AssetCrossSourceStates.Linked);
        summary.CrossSourceLabel.Should().Be("Vínculo confirmado entre fontes");
        summary.ActiveSources.Should().BeEquivalentTo(new[] { DefenderLabel, IntuneLabel });

        var sources = await Sources(assetId);
        sources!.Sources.Should().HaveCount(2).And.OnlyContain(s => s.ResolutionState == "Linked" && s.Diagnostics == null);
        sources.Explanation.Should().Contain("mesmo identificador de dispositivo do Microsoft Entra");
        sources.LinkMeaning.Should().Contain("Não confirma a segurança");
        sources.DirectoryNote.Should().Contain("não foi consultado");
        var intuneRecord = sources.Sources.Single(s => s.SourceLabel == IntuneLabel);
        intuneRecord.SourceComplianceLabel.Should().Be("Conforme, segundo a fonte");
        intuneRecord.SourceLastSeenAt.Should().Be(DateTimeOffset.Parse("2026-09-10T09:00:00Z"));
    }

    [Fact]
    public async Task IntuneThenDefender_ReverseOrder_AndReexecution_PreserveCanonicalId_AndReplaceOnlyThePlaceholderName()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", Q(DevX)));

        await Sync(intune);
        var assetId = (await Binding(intune, "dev-1")).AssetId;
        var created = await AssetOf(assetId);
        created.NameOrigin.Should().Be(AssetNameOrigin.Placeholder, "o Intune não coleta nome");
        created.Name.Should().StartWith("Dispositivo sem nome coletado")
            .And.NotContain("dev-1").And.NotContain(DevX, "nome provisório nunca é um identificador técnico");

        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        var d = await Sync(defender);
        d.Vulnerabilities!.Resolution!.AssetsCreated.Should().Be(0);
        (await Binding(defender, "m-1")).AssetId.Should().Be(assetId, "Intune→Defender reutiliza o ativo canônico");
        var named = await AssetOf(assetId);
        named.Name.Should().Be("pc-01.demo.example.com", "o nome PROVISÓRIO dá lugar ao primeiro nome observado");
        named.NameOrigin.Should().Be(AssetNameOrigin.ObservedBySource);
        var linkedAt = (await Binding(intune, "dev-1")).LinkedAt;

        // Reexecução, nas duas ordens: nada muda de identidade.
        await Sync(intune);
        await Sync(defender);
        await Sync(defender);
        await Sync(intune);

        await using var db = NewContext(TenantA);
        (await db.Assets.CountAsync()).Should().Be(1);
        (await db.Assets.SingleAsync()).Id.Should().Be(assetId);
        (await db.AssetStrongIdentifiers.CountAsync()).Should().Be(1);
        (await db.AssetSourceBindings.ToListAsync()).Should().HaveCount(2).And.OnlyContain(b => b.AssetId == assetId);
        (await Binding(intune, "dev-1")).LinkedAt.Should().Be(linkedAt, "o instante do vínculo é preservado");
    }

    // ================= Nada de união sem chave forte ============================================================

    [Fact]
    public async Task DifferentDirectoriesInTheSameTenant_NeverUnite()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirB);
        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", Q(DevX)));

        await Sync(defender);
        await Sync(intune);

        await using var db = NewContext(TenantA);
        (await db.Assets.CountAsync()).Should().Be(2, "o mesmo identificador em diretórios diferentes é outro dispositivo");
        var keys = await db.AssetStrongIdentifiers.ToListAsync();
        keys.Select(k => k.DirectoryNamespace).Should().BeEquivalentTo(new[] { DirA, DirB });
        (await Binding(defender, "m-1")).AssetId.Should().NotBe((await Binding(intune, "dev-1")).AssetId);
        (await Summary((await Binding(intune, "dev-1")).AssetId)).CrossSourceState
            .Should().Be(AssetCrossSourceStates.IdentifierOnly);
    }

    [Fact]
    public async Task DifferentTenants_SameDirectoryAndIds_StayIsolated_AndTheReadIsTenantScoped()
    {
        var defenderA = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intuneB = Seed(TenantB, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", Q(DevX)));

        await Sync(defenderA, TenantA);
        await Sync(intuneB, TenantB);

        var assetA = (await Binding(defenderA, "m-1", TenantA)).AssetId;
        var assetB = (await Binding(intuneB, "dev-1", TenantB)).AssetId;
        assetA.Should().NotBe(assetB, "tenants diferentes nunca se unem, mesmo com diretório e identificador iguais");

        await using (var db = NewContext(TenantA))
        {
            (await db.Assets.CountAsync()).Should().Be(1);
            (await db.AssetStrongIdentifiers.CountAsync()).Should().Be(1);
            (await db.AssetStrongIdentifiers.IgnoreQueryFilters().CountAsync()).Should().Be(2);
        }
        (await Sources(assetB, tenant: TenantA)).Should().BeNull("o ativo de outro tenant não existe na leitura deste");
        (await Sources(assetB, tenant: TenantB))!.CrossSourceState.Should().Be(AssetCrossSourceStates.IdentifierOnly);
    }

    [Fact]
    public async Task SameHostname_WithDifferentDeviceIds_NeverUnites()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.DefenderMachines = Page(
            SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)),
            SyntheticDeviceSources.Machine("m-2", "pc-01.demo.example.com", Q(DevY)));
        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", Q(DevY)));

        await Sync(defender);
        await Sync(intune);

        var m1 = (await Binding(defender, "m-1")).AssetId;
        var m2 = (await Binding(defender, "m-2")).AssetId;
        m1.Should().NotBe(m2, "hostname igual não autoriza união");
        (await Binding(intune, "dev-1")).AssetId.Should().Be(m2, "só o identificador de diretório une");
        (await Summary(m1)).CrossSourceState.Should().Be(AssetCrossSourceStates.IdentifierOnly);
        (await Summary(m2)).CrossSourceState.Should().Be(AssetCrossSourceStates.Linked);
    }

    [Fact]
    public async Task MissingEmptyInvalidOrPlaceholderIds_NeverUnite_AndTheMachinesStayValid()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        var machines = new[]
        {
            SyntheticDeviceSources.Machine("m-a", "a.demo.example.com", null),
            SyntheticDeviceSources.Machine("m-b", "b.demo.example.com", "null"),
            SyntheticDeviceSources.Machine("m-c", "c.demo.example.com", Q("")),
            SyntheticDeviceSources.Machine("m-d", "d.demo.example.com", Q("00000000-0000-0000-0000-000000000000")),
            SyntheticDeviceSources.Machine("m-e", "e.demo.example.com", Q("nao-e-um-guid")),
            SyntheticDeviceSources.Machine("m-f", "f.demo.example.com", "123"),
            SyntheticDeviceSources.Machine("m-g", "g.demo.example.com", Q("ffffffff-ffff-ffff-ffff-ffffffffffff")),
        };
        _src.DefenderMachines = Page(machines);
        _src.DefenderRelations = Page(new[] { "m-a", "m-b", "m-c", "m-d", "m-e", "m-f", "m-g" }
            .Select(m => SyntheticDeviceSources.Relation(m)).ToArray());
        _src.DefenderCves = SyntheticDeviceSources.CveCatalog;
        _src.IntuneDevices = Page(
            SyntheticDeviceSources.Device("dev-a", null),
            SyntheticDeviceSources.Device("dev-b", Q("00000000-0000-0000-0000-000000000000")),
            SyntheticDeviceSources.Device("dev-c", Q("{" + DevX + "}")),
            SyntheticDeviceSources.Device("dev-d", Q("11111111-1111-1111-1111-111111111111")));

        var d = await Sync(defender);
        var i = await Sync(intune);

        d.Vulnerabilities!.InvalidMachines.Should().Be(0, "identificador ausente/inválido NÃO invalida a máquina");
        d.Vulnerabilities.WasComplete.Should().BeTrue();
        d.Vulnerabilities.Resolution!.WithoutIdentifier.Should().Be(3);
        d.Vulnerabilities.Resolution.InvalidIdentifier.Should().Be(4);
        i.DeviceResolution!.WithoutIdentifier.Should().Be(1);
        i.DeviceResolution.InvalidIdentifier.Should().Be(3);

        await using var db = NewContext(TenantA);
        (await db.AssetStrongIdentifiers.CountAsync()).Should().Be(0, "nenhuma chave forte sem identificador válido");
        (await db.Assets.CountAsync()).Should().Be(11, "cada objeto é preservado no ativo da própria observação");
        (await db.AssetThreatExposures.CountAsync()).Should().Be(7, "as vulnerabilidades de todas as máquinas seguem");
        var byId = (await db.AssetSourceBindings.ToListAsync()).ToDictionary(b => b.ExternalId);
        foreach (var id in new[] { "m-a", "m-b", "m-c", "dev-a" })
            byId[id].ResolutionState.Should().Be(AssetBindingResolutionState.NoIdentifier, id);
        foreach (var id in new[] { "m-d", "m-e", "m-f", "m-g", "dev-b", "dev-c", "dev-d" })
        {
            byId[id].ResolutionState.Should().Be(AssetBindingResolutionState.InvalidIdentifier, id);
            byId[id].DirectoryIdStatus.Should().Be(DirectoryIdentifierStatus.Invalid, id);
            byId[id].DirectoryDeviceId.Should().BeNull(id);
        }
        var summary = await Summary(byId["dev-a"].AssetId);
        summary.CrossSourceState.Should().Be(AssetCrossSourceStates.NotLinked);
        (await Sources(byId["dev-a"].AssetId))!.Explanation.Should().Contain("não significa que seja um dispositivo diferente");
    }

    [Fact]
    public async Task DirectoryNotConfirmed_WhenTheIntegrationTenantIsNotAGuid_NoUnion()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, "contoso-sintetico.onmicrosoft.com");
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", Q(DevX)));

        var d = await Sync(defender);
        await Sync(intune);

        d.Vulnerabilities!.Resolution!.DirectoryUnconfirmed.Should().Be(1);
        var m1 = await Binding(defender, "m-1");
        m1.ResolutionState.Should().Be(AssetBindingResolutionState.DirectoryUnconfirmed);
        m1.DirectoryIdStatus.Should().Be(DirectoryIdentifierStatus.Provided);
        m1.DirectoryNamespace.Should().BeNull("o namespace nunca é deduzido de um domínio");
        (await Binding(intune, "dev-1")).AssetId.Should().NotBe(m1.AssetId);
    }

    // ================= Identificador informado depois ============================================================

    [Fact]
    public async Task IdentifierProvidedLater_LinksKeepingTheSameCanonicalAsset()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", null));
        await Sync(defender);
        var assetId = (await Binding(defender, "m-1")).AssetId;
        (await Binding(defender, "m-1")).ResolutionState.Should().Be(AssetBindingResolutionState.NoIdentifier);

        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        await Sync(defender);
        var later = await Binding(defender, "m-1");
        later.AssetId.Should().Be(assetId);
        later.ResolutionState.Should().Be(AssetBindingResolutionState.Linked);

        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", Q(DevX)));
        await Sync(intune);
        (await Binding(intune, "dev-1")).AssetId.Should().Be(assetId);
        await using var db = NewContext(TenantA);
        (await db.Assets.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task IdentifierProvidedLater_AlreadyHeldByAnotherExistingAsset_PreservesBothAsConflict()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", null));
        await Sync(intune);
        var intuneAsset = (await Binding(intune, "dev-1")).AssetId;

        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        await Sync(defender);
        var defenderAsset = (await Binding(defender, "m-1")).AssetId;
        defenderAsset.Should().NotBe(intuneAsset);

        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", Q(DevX)));
        var i = await Sync(intune);

        i.DeviceResolution!.Conflicts.Should().Be(1);
        var dev1 = await Binding(intune, "dev-1");
        dev1.AssetId.Should().Be(intuneAsset, "nada é movido nem fundido");
        dev1.ResolutionState.Should().Be(AssetBindingResolutionState.Conflict);
        dev1.ConflictKind.Should().Be(AssetBindingConflictKind.IdentifierHeldByOtherAsset);
        dev1.ConflictAssetId.Should().Be(defenderAsset);
        await using (var db = NewContext(TenantA))
        {
            (await db.Assets.CountAsync()).Should().Be(2, "os dois ativos existentes são preservados");
            (await db.AssetStrongIdentifiers.SingleAsync()).AssetId.Should().Be(defenderAsset, "a chave não é reescrita");
        }

        var sources = await Sources(intuneAsset);
        sources!.CrossSourceState.Should().Be(AssetCrossSourceStates.Conflict);
        var record = sources.Sources.Single();
        record.RelatedAssetId.Should().Be(defenderAsset);
        record.RelatedAssetName.Should().Be("pc-01.demo.example.com");
        record.ResolutionExplanation.Should().Contain("duplicidade entre ativos já existentes");
        (await Summary(defenderAsset)).CrossSourceState.Should().Be(AssetCrossSourceStates.IdentifierOnly);
    }

    // ================= Contradição e legado ======================================================================

    [Fact]
    public async Task ContradictoryIdentifierChange_DoesNotMoveTheBinding_AndReturningToTheEstablishedIdClearsIt()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", Q(DevX)));
        await Sync(defender);
        await Sync(intune);
        var assetId = (await Binding(defender, "m-1")).AssetId;

        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevY)));
        var d = await Sync(defender);

        d.Vulnerabilities!.Resolution!.Conflicts.Should().Be(1);
        var m1 = await Binding(defender, "m-1");
        m1.AssetId.Should().Be(assetId, "o binding estabelecido não é movido silenciosamente");
        m1.ResolutionState.Should().Be(AssetBindingResolutionState.Conflict);
        m1.ConflictKind.Should().Be(AssetBindingConflictKind.IdentifierChanged);
        m1.DirectoryDeviceId.Should().Be(DevX, "o vínculo estabelecido permanece");
        m1.ConflictDirectoryDeviceId.Should().Be(DevY, "a observação contraditória fica registrada para análise");
        m1.ConflictDirectoryNamespace.Should().Be(DirA, "o diretório da observação é registrado — aqui, o mesmo do vínculo");
        await using (var db = NewContext(TenantA))
        {
            (await db.AssetStrongIdentifiers.CountAsync()).Should().Be(1, "nenhuma chave é criada a partir da contradição");
            (await db.Assets.CountAsync()).Should().Be(1);
        }
        (await Summary(assetId)).CrossSourceState.Should().Be(AssetCrossSourceStates.Conflict);
        var current = (await Sources(assetId))!.Sources.Single(s => s.SourceLabel == DefenderLabel);
        current.ResolutionExplanation.Should().Contain("nada foi movido");
        current.ResolutionLabel.Should().Be("Conflito: identificador mudou", "mesmo diretório: só o identificador mudou");

        // Linha de conflito LEGADA (gravada antes da DeviceResolution02): o diretório daquela observação não foi guardado.
        // A leitura não o inventa nem afirma que só o identificador mudou.
        await using (var db = NewContext(TenantA))
        {
            var legacy = await db.AssetSourceBindings.SingleAsync(b => b.ConnectorConfigId == defender && b.ExternalId == "m-1");
            legacy.ConflictDirectoryNamespace = null;
            await db.SaveChangesAsync();
        }
        (await Sources(assetId))!.Sources.Single(s => s.SourceLabel == DefenderLabel)
            .ResolutionLabel.Should().Be("Conflito: vínculo de diretório mudou");
        var legacyDiag = (await Sources(assetId, "TenantAdmin"))!.Sources.Single(s => s.SourceLabel == DefenderLabel).Diagnostics!;
        (legacyDiag.ConflictDirectoryNamespace, legacyDiag.ConflictDirectoryDeviceId).Should().Be((null, DevY));

        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        await Sync(defender);
        var back = await Binding(defender, "m-1");
        back.ResolutionState.Should().Be(AssetBindingResolutionState.Linked);
        back.ConflictKind.Should().Be(AssetBindingConflictKind.None);
        back.ConflictDirectoryDeviceId.Should().BeNull();
        (await Summary(assetId)).CrossSourceState.Should().Be(AssetCrossSourceStates.Linked);
    }

    [Fact]
    public async Task LegacyBinding_EstablishesTheKeyOnItsOwnAsset_PreservingCuratedAttributes()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        var legacyAsset = SeedLegacyAsset(defender, "m-1");
        (await Summary(legacyAsset)).CrossSourceState.Should().Be(AssetCrossSourceStates.NotEvaluated,
            "registro anterior à resolução não recebe conclusão alguma");

        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "fs01.demo.example.com", Q(DevX)));
        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", Q(DevX)));
        await Sync(defender);
        await Sync(intune);

        (await Binding(defender, "m-1")).AssetId.Should().Be(legacyAsset);
        (await Binding(intune, "dev-1")).AssetId.Should().Be(legacyAsset, "o Intune entra no ativo legado que estabeleceu a chave");
        var asset = await AssetOf(legacyAsset);
        asset.Name.Should().Be("Servidor de arquivos (curado)", "nome curado nunca é sobrescrito por fonte");
        asset.OwnerName.Should().Be("Equipe de Infraestrutura");
        asset.Criticality.Should().Be(3, "criticidade declarada é preservada, nunca reiniciada ao padrão");
        asset.BusinessProcessId.Should().BeNull();
        (await Summary(legacyAsset)).CrossSourceState.Should().Be(AssetCrossSourceStates.Linked);
    }

    [Fact]
    public async Task LegacyDuplicate_WhenIntuneArrivedFirst_BothAssetsArePreservedAndTheConflictIsExposed()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        var legacyAsset = SeedLegacyAsset(defender, "m-1");

        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", Q(DevX)));
        await Sync(intune);
        var intuneAsset = (await Binding(intune, "dev-1")).AssetId;
        intuneAsset.Should().NotBe(legacyAsset);

        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "fs01.demo.example.com", Q(DevX)));
        _src.DefenderRelations = Page(SyntheticDeviceSources.Relation("m-1"));
        _src.DefenderCves = SyntheticDeviceSources.CveCatalog;
        await Sync(defender);

        var m1 = await Binding(defender, "m-1");
        m1.AssetId.Should().Be(legacyAsset, "o ativo legado não é fundido nem apagado");
        m1.ResolutionState.Should().Be(AssetBindingResolutionState.Conflict);
        m1.ConflictKind.Should().Be(AssetBindingConflictKind.IdentifierHeldByOtherAsset);
        m1.ConflictAssetId.Should().Be(intuneAsset);
        await using (var db = NewContext(TenantA))
        {
            (await db.Assets.CountAsync()).Should().Be(2);
            (await db.AssetThreatExposures.SingleAsync()).AssetId.Should().Be(legacyAsset,
                "a vulnerabilidade continua no ativo do binding — nenhuma evidência é reescrita");
            (await db.AssetStrongIdentifiers.SingleAsync()).AssetId.Should().Be(intuneAsset);
        }
        var legacy = await AssetOf(legacyAsset);
        legacy.OwnerName.Should().Be("Equipe de Infraestrutura");
        legacy.Criticality.Should().Be(3);
        var sources = await Sources(legacyAsset);
        sources!.CrossSourceState.Should().Be(AssetCrossSourceStates.Conflict);
        sources.Explanation.Should().Contain("Nenhum ativo foi escolhido arbitrariamente");
    }

    // ================= Ciclo de vida independente por fonte ======================================================

    [Fact]
    public async Task IntuneFailureOrPartial_NeverDeactivates_AndDisappearanceInOneSourceOnlyAffectsThatSource()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        _src.DefenderRelations = Page(SyntheticDeviceSources.Relation("m-1"));
        _src.DefenderCves = SyntheticDeviceSources.CveCatalog;
        _src.IntuneDevices = Page(
            SyntheticDeviceSources.Device("dev-1", Q(DevX)),
            SyntheticDeviceSources.Device("dev-2", Q(DevY)));
        await Sync(defender);
        await Sync(intune);
        var linkedAsset = (await Binding(defender, "m-1")).AssetId;
        var dev2Before = await Binding(intune, "dev-2");

        // (a) Sem permissão de dispositivos: a dimensão não produz inventário — nada é resolvido nem desativado.
        _src.IntuneRoute = _ => (HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied"}}""");
        var denied = await Sync(intune);
        denied.DeviceResolution.Should().BeNull();
        (await Bindings(TenantA)).Where(b => b.ConnectorConfigId == intune).Should().OnlyContain(b => b.IsActive);
        (await Binding(intune, "dev-2")).LastObservedAt.Should().Be(dev2Before.LastObservedAt,
            "a última evidência válida é preservada");

        // (b) Página intermediária falha: a dimensão é PARCIAL — só fatos positivos, nenhuma desativação.
        _src.IntuneRoute = req => req.RequestUri!.Query.Contains("skiptoken")
            ? (HttpStatusCode.InternalServerError, "{}")
            : (HttpStatusCode.OK, "{\"value\":[" + SyntheticDeviceSources.Device("dev-1", Q(DevX)) +
                "],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$skiptoken=p2\"}");
        var partial = await Sync(intune);
        partial.DeviceResolution!.DeactivationApplied.Should().BeFalse();
        (await Binding(intune, "dev-2")).IsActive.Should().BeTrue("coleta parcial não desativa objetos ausentes");

        // (c) Leitura completa sem dev-1: SÓ o binding do Intune sai; o ativo segue ativo pelo Defender.
        _src.IntuneRoute = null;
        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-2", Q(DevY)));
        var complete = await Sync(intune);
        complete.DeviceResolution!.BindingsDeactivated.Should().Be(1);
        var dev1 = await Binding(intune, "dev-1");
        dev1.IsActive.Should().BeFalse();
        dev1.AssetId.Should().Be(linkedAsset);
        (await AssetOf(linkedAsset)).IsActive.Should().BeTrue("ausência numa fonte não é exclusão do ativo");
        await using (var db = NewContext(TenantA))
            (await db.AssetThreatObservations.SingleAsync()).LifecycleState.Should().Be(ObservationLifecycle.Open,
                "a ausência no Intune não resolve o achado observado pelo Defender");
        var summary = await Summary(linkedAsset);
        summary.ActiveSourceCount.Should().Be(1);
        summary.CrossSourceState.Should().Be(AssetCrossSourceStates.IdentifierOnly);
        var intuneRecord = (await Sources(linkedAsset))!.Sources.Single(s => s.SourceLabel == IntuneLabel);
        intuneRecord.IsActive.Should().BeFalse();
        intuneRecord.PresenceLabel.Should().Be("Não mais observado pela fonte");
        intuneRecord.NoLongerObservedSince.Should().NotBeNull();

        // (d) O Defender também deixa de observá-lo: o ativo fica inativo, mas é PRESERVADO — e o retorno do
        // dispositivo reusa o MESMO ativo canônico.
        _src.DefenderMachines = Page();
        _src.DefenderRelations = Page();
        await Sync(defender);
        (await AssetOf(linkedAsset)).IsActive.Should().BeFalse();
        await using (var db = NewContext(TenantA))
            (await db.AssetStrongIdentifiers.SingleAsync(k => k.IdentifierValue == DevX)).AssetId.Should().Be(linkedAsset);

        _src.IntuneDevices = Page(
            SyntheticDeviceSources.Device("dev-1", Q(DevX)),
            SyntheticDeviceSources.Device("dev-2", Q(DevY)));
        await Sync(intune);
        (await Binding(intune, "dev-1")).Should().Match<AssetSourceBinding>(b => b.IsActive && b.AssetId == linkedAsset);
        (await AssetOf(linkedAsset)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task DuplicateRecordsOfTheSameSource_SameDeviceId_ShareOneAsset_WithoutClaimingCrossSourceLink()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        _src.DefenderMachines = Page(
            SyntheticDeviceSources.Machine("m-old", "pc-01.demo.example.com", Q(DevX)),
            SyntheticDeviceSources.Machine("m-new", "pc-01.demo.example.com", Q(DevX)));

        await Sync(defender);

        var a = (await Binding(defender, "m-old")).AssetId;
        (await Binding(defender, "m-new")).AssetId.Should().Be(a, "mesmo dispositivo de diretório reonboardado");
        (await Summary(a)).CrossSourceState.Should().Be(AssetCrossSourceStates.IdentifierOnly,
            "dois registros da MESMA fonte não são vínculo entre fontes");
    }

    // ================= Fronteiras: fatos da fonte, score, diagnóstico =============================================

    [Fact]
    public async Task SourceFactsStaySourceInformation_NoScoreNoSignal_AndIdentifiersOnlyInTenantAdminDiagnostics()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", Q(DevX), compliance: "noncompliant", encrypted: false));
        await Sync(defender);
        await Sync(intune);
        var assetId = (await Binding(defender, "m-1")).AssetId;

        await using (var db = NewContext(TenantA))
        {
            (await db.Signals.CountAsync()).Should().Be(0, "resolução não vira EvidenceSignal");
            (await db.TenantControlStates.CountAsync()).Should().Be(0, "resolução não toca o ledger/score");
            var asset = await db.Assets.SingleAsync();
            asset.RiskScore.Should().BeNull("nenhum veredito de risco é inferido do vínculo");
            asset.RiskLevel.Should().BeNull();
            asset.Criticality.Should().Be(1, "o valor padrão não é alterado nem apresentado como inferência");
        }

        var analyst = await Sources(assetId, "Analyst");
        analyst!.DiagnosticsIncluded.Should().BeFalse();
        analyst.Sources.Should().OnlyContain(s => s.Diagnostics == null);
        var intuneRecord = analyst.Sources.Single(s => s.SourceLabel == IntuneLabel);
        intuneRecord.SourceComplianceLabel.Should().Be("Não conforme, segundo a fonte");
        intuneRecord.SourceEncryptionLabel.Should().Be("Sem criptografia, segundo a fonte");
        var analystJson = JsonSerializer.Serialize(analyst);
        var summaryJson = JsonSerializer.Serialize(await Summary(assetId));
        foreach (var technical in new[] { DevX, DirA, "m-1", "dev-1" })
        {
            analystJson.Should().NotContain(technical, "identificador técnico só no diagnóstico restrito");
            summaryJson.Should().NotContain(technical, "a listagem nunca carrega identificador técnico");
        }

        var admin = await Sources(assetId, "TenantAdmin");
        admin!.DiagnosticsIncluded.Should().BeTrue();
        var diag = admin.Sources.Single(s => s.SourceLabel == DefenderLabel).Diagnostics!;
        diag.ExternalId.Should().Be("m-1");
        diag.DirectoryDeviceId.Should().Be(DevX);
        diag.DirectoryNamespace.Should().Be(DirA);
    }

    [Fact]
    public async Task DevicePostureCorrelation_ReportsTheRealLinkCounts_InsteadOfAGap()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        _src.IntuneDevices = Page(
            SyntheticDeviceSources.Device("dev-1", Q(DevX)),
            SyntheticDeviceSources.Device("dev-2", null));
        await Sync(defender);
        await Sync(intune);

        await using var db = NewContext(TenantA);
        var view = await new DevicePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();
        view.Correlation.DeterministicCorrelationAvailable.Should().BeTrue();
        view.Correlation.DevicesWithDirectoryId.Should().Be(1);
        view.Correlation.DevicesObserved.Should().Be(2);
        view.Correlation.DevicesLinkedAcrossSources.Should().Be(1);
        view.Correlation.DevicesWithoutLink.Should().Be(1);
        view.Correlation.DevicesInConflict.Should().Be(0);
        view.Correlation.Explanation.Should().Contain("não a segurança do dispositivo");
        view.DeviceSummary.TotalDevices.Should().Be(2, "os agregados atuais do Intune continuam intactos");
    }

    // ================= Repetição do mesmo registro na mesma coleta (fronteira dos conectores) ======================

    [Fact]
    public async Task EquivalentDuplicatesAcrossPages_AreOneObservation_AndLinkNormally()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        var machine = SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX));
        _src.DefenderMachines = Page(machine);
        _src.DefenderMachinesPage2 = Page(machine);
        var device = SyntheticDeviceSources.Device("dev-1", Q(DevX));
        _src.IntuneDevices = Page(device);
        _src.IntuneDevicesPage2 = Page(device);

        var d = await Sync(defender);
        var i = await Sync(intune);

        d.Vulnerabilities!.WasComplete.Should().BeTrue("repetição equivalente não é divergência");
        d.Vulnerabilities.InvalidMachines.Should().Be(0);
        d.Vulnerabilities.Resolution!.ContradictoryIdentifiers.Should().Be(0);
        i.DeviceResolution!.ContradictoryIdentifiers.Should().Be(0);
        i.DeviceResolution.Conflicts.Should().Be(0);
        await using var db = NewContext(TenantA);
        (await db.Assets.CountAsync()).Should().Be(1);
        (await db.AssetStrongIdentifiers.CountAsync()).Should().Be(1);
        (await db.AssetSourceBindings.ToListAsync()).Should().HaveCount(2, "uma observação por registro, não uma por repetição")
            .And.OnlyContain(b => b.ResolutionState == AssetBindingResolutionState.Linked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefenderSameMachineWithTwoDirectoryIds_InEitherPageOrder_NeverUnites_AndEndsIdentically(bool reversed)
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.IntuneDevices = Page(
            SyntheticDeviceSources.Device("dev-x", Q(DevX)),
            SyntheticDeviceSources.Device("dev-y", Q(DevY)));
        await Sync(intune);
        var assetX = (await Binding(intune, "dev-x")).AssetId;
        var assetY = (await Binding(intune, "dev-y")).AssetId;
        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-9", "pc-09.demo.example.com", null));
        await Sync(defender);

        // O MESMO registro (m-1) chega em duas páginas, com X numa e Y na outra; m-9 some desta coleta.
        var withX = SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX));
        var withY = SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevY));
        _src.DefenderMachines = Page(reversed ? withY : withX);
        _src.DefenderMachinesPage2 = Page(reversed ? withX : withY);
        _src.DefenderRelations = Page(SyntheticDeviceSources.Relation("m-1"));
        _src.DefenderCves = SyntheticDeviceSources.CveCatalog;
        var d = await Sync(defender);

        d.Vulnerabilities!.WasComplete.Should().BeFalse("repetição divergente torna a coleta incompleta");
        d.Vulnerabilities.InvalidMachines.Should().Be(1, "conta-se o registro divergente, não cada repetição");
        d.Vulnerabilities.Resolution!.ContradictoryIdentifiers.Should().Be(1);
        d.Vulnerabilities.Resolution.KeysEstablished.Should().Be(0);
        d.Vulnerabilities.Resolution.DeactivationApplied.Should().BeFalse();

        var m1 = await Binding(defender, "m-1");
        m1.AssetId.Should().NotBe(assetX, "a primeira observação não estabelece associação").And.NotBe(assetY);
        m1.ResolutionState.Should().Be(AssetBindingResolutionState.Conflict);
        m1.ConflictKind.Should().Be(AssetBindingConflictKind.ContradictoryObservation);
        m1.DirectoryIdStatus.Should().Be(DirectoryIdentifierStatus.Contradictory);
        m1.DirectoryDeviceId.Should().BeNull();
        m1.ConflictObservedDeviceIds.Should().Be(DevX + "," + DevY, "os dois valores ficam para explicar, em ordem estável");
        (await Binding(defender, "m-9")).IsActive.Should().BeTrue("coleta incompleta não desativa o ausente");
        await using (var db = NewContext(TenantA))
        {
            (await db.AssetStrongIdentifiers.CountAsync()).Should().Be(2, "nenhuma chave nasce da contradição");
            (await db.AssetThreatExposures.SingleAsync()).AssetId.Should().Be(m1.AssetId,
                "a vulnerabilidade da máquina continua utilizável, no ativo do próprio registro");
        }
        (await Summary(m1.AssetId)).CrossSourceState.Should().Be(AssetCrossSourceStates.Conflict);
        (await Summary(assetX)).CrossSourceState.Should().Be(AssetCrossSourceStates.IdentifierOnly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntuneSameDeviceWithTwoDirectoryIds_InEitherPageOrder_NeverUnites_AndEndsIdentically(bool reversed)
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.DefenderMachines = Page(
            SyntheticDeviceSources.Machine("m-x", "pc-x.demo.example.com", Q(DevX)),
            SyntheticDeviceSources.Machine("m-y", "pc-y.demo.example.com", Q(DevY)));
        await Sync(defender);
        var assetX = (await Binding(defender, "m-x")).AssetId;
        var assetY = (await Binding(defender, "m-y")).AssetId;
        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-3", null));
        await Sync(intune);

        var withX = SyntheticDeviceSources.Device("dev-1", Q(DevX));
        var withY = SyntheticDeviceSources.Device("dev-1", Q(DevY));
        _src.IntuneDevices = Page(reversed ? withY : withX);
        _src.IntuneDevicesPage2 = Page(reversed ? withX : withY);
        var i = await Sync(intune);

        i.DeviceResolution!.ContradictoryIdentifiers.Should().Be(1);
        i.DeviceResolution.KeysEstablished.Should().Be(0);
        i.DeviceResolution.DeactivationApplied.Should().BeFalse("a repetição torna a passada incompleta");
        var dev1 = await Binding(intune, "dev-1");
        dev1.AssetId.Should().NotBe(assetX).And.NotBe(assetY);
        dev1.ResolutionState.Should().Be(AssetBindingResolutionState.Conflict);
        dev1.ConflictKind.Should().Be(AssetBindingConflictKind.ContradictoryObservation);
        dev1.ConflictObservedDeviceIds.Should().Be(DevX + "," + DevY);
        dev1.SourceCompliance.Should().Be(DeviceComplianceBucket.Compliant, "os fatos não contraditórios seguem utilizáveis");
        (await Binding(intune, "dev-3")).IsActive.Should().BeTrue();
        await using var db = NewContext(TenantA);
        (await db.AssetStrongIdentifiers.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ContradictionWithAnEstablishedLink_KeepsTheLink_DeclaresIt_AndClearsWhenConsistentAgain()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", Q(DevX)));
        await Sync(defender);
        await Sync(intune);
        var assetId = (await Binding(defender, "m-1")).AssetId;

        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        _src.DefenderMachinesPage2 = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevY)));
        _src.DefenderRelations = Page(SyntheticDeviceSources.Relation("m-1"));
        _src.DefenderCves = SyntheticDeviceSources.CveCatalog;
        await Sync(defender);

        var m1 = await Binding(defender, "m-1");
        m1.AssetId.Should().Be(assetId, "o vínculo anterior é preservado");
        m1.IsActive.Should().BeTrue();
        m1.DirectoryNamespace.Should().Be(DirA);
        m1.DirectoryDeviceId.Should().Be(DevX);
        m1.ResolutionState.Should().Be(AssetBindingResolutionState.Conflict);
        m1.ConflictKind.Should().Be(AssetBindingConflictKind.ContradictoryObservation);
        m1.ConflictObservedDeviceIds.Should().Be(DevX + "," + DevY);
        (await Binding(intune, "dev-1")).ResolutionState.Should().Be(AssetBindingResolutionState.Linked);
        await using (var db = NewContext(TenantA))
        {
            (await db.AssetThreatExposures.SingleAsync()).AssetId.Should().Be(assetId, "evidência não contraditória segue no ativo");
            (await db.AssetStrongIdentifiers.CountAsync()).Should().Be(1);
            (await db.Assets.CountAsync()).Should().Be(1, "nada é apagado nem duplicado");
        }

        var analyst = (await Sources(assetId))!;
        analyst.CrossSourceState.Should().Be(AssetCrossSourceStates.Conflict);
        var record = analyst.Sources.Single(s => s.SourceLabel == DefenderLabel);
        record.ResolutionLabel.Should().Be("Conflito: identificadores contraditórios na mesma coleta");
        record.ResolutionExplanation.Should().Contain("o vínculo estabelecido antes foi mantido");
        record.IdentifierStatusLabel.Should().Be("Contraditório na mesma coleta (não usado)");
        JsonSerializer.Serialize(analyst).Should().NotContain(DevY, "valores contraditórios só no diagnóstico restrito");
        var diag = (await Sources(assetId, "TenantAdmin"))!.Sources.Single(s => s.SourceLabel == DefenderLabel).Diagnostics!;
        diag.ConflictObservedDeviceIds.Should().Equal(DevX, DevY);
        diag.DirectoryDeviceId.Should().Be(DevX);

        _src.DefenderMachinesPage2 = null;
        await Sync(defender);
        var back = await Binding(defender, "m-1");
        back.ResolutionState.Should().Be(AssetBindingResolutionState.Linked);
        back.ConflictObservedDeviceIds.Should().BeNull();
        back.ConflictKind.Should().Be(AssetBindingConflictKind.None);
    }

    [Fact]
    public async Task ExternalIdsOutsideTheContract_AreRejected_NeverTruncatedIntoOneRecord_AndNeverDeactivateByIncompleteness()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-keep", "keep.demo.example.com", Q(DevX)));
        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-keep", Q(DevX)));
        await Sync(defender);
        await Sync(intune);

        // Dois ids que só diferem DEPOIS do limite: truncados, virariam o mesmo registro.
        var longA = new string('a', DeviceSourceObservations.MaxExternalIdLength) + "1";
        var longB = new string('a', DeviceSourceObservations.MaxExternalIdLength) + "2";
        _src.DefenderMachines = Page(
            SyntheticDeviceSources.Machine(longA, "a.demo.example.com", Q(DevY)),
            SyntheticDeviceSources.Machine(" m-pad", "pad.demo.example.com", null));
        _src.IntuneDevices = Page(
            SyntheticDeviceSources.Device(longA, Q(DevY)),
            SyntheticDeviceSources.Device(longB, null));
        var d = await Sync(defender);
        var i = await Sync(intune);

        d.Vulnerabilities!.InvalidMachines.Should().Be(2, "id acima do contrato e id com espaço na borda são recusados");
        d.Vulnerabilities.WasComplete.Should().BeFalse();
        i.DeviceResolution!.Observed.Should().Be(0);
        i.DeviceResolution.DeactivationApplied.Should().BeFalse();
        var bindings = await Bindings(TenantA);
        bindings.Should().HaveCount(2, "nenhum registro nasce de id recusado")
            .And.OnlyContain(b => b.IsActive, "coleta incompleta não desativa os ausentes");
        bindings.Should().NotContain(b => b.ExternalId.StartsWith("aaaa") || b.ExternalId.Contains("m-pad"));
    }

    // ================= Mudança de diretório: o par observado fica separado do vínculo ==============================

    [Fact]
    public async Task DirectoryChangeOnly_PreservesTheObservedDirectory_ApartFromTheEstablishedLink()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        await Sync(defender);
        var assetId = (await Binding(defender, "m-1")).AssetId;

        SetDirectory(defender, DirB);
        var d = await Sync(defender);

        d.Vulnerabilities!.Resolution!.Conflicts.Should().Be(1);
        var m1 = await Binding(defender, "m-1");
        m1.AssetId.Should().Be(assetId);
        m1.ConflictKind.Should().Be(AssetBindingConflictKind.DirectoryChanged);
        (m1.DirectoryNamespace, m1.DirectoryDeviceId).Should().Be((DirA, DevX), "o vínculo estabelecido fica");
        (m1.ConflictDirectoryNamespace, m1.ConflictDirectoryDeviceId).Should().Be((DirB, DevX),
            "o par observado — com o diretório B — fica registrado à parte");
        await using (var db = NewContext(TenantA))
            (await db.AssetStrongIdentifiers.SingleAsync()).DirectoryNamespace.Should().Be(DirA, "nenhuma chave nova");

        var analyst = (await Sources(assetId))!;
        var record = analyst.Sources.Single();
        record.ResolutionLabel.Should().Be("Conflito: diretório de origem mudou");
        record.ResolutionExplanation.Should().Contain("outro diretório de origem");
        foreach (var technical in new[] { DirB, DevX })
        {
            JsonSerializer.Serialize(analyst).Should().NotContain(technical);
            JsonSerializer.Serialize(await Summary(assetId)).Should().NotContain(technical);
        }
        var diag = (await Sources(assetId, "TenantAdmin"))!.Sources.Single().Diagnostics!;
        (diag.DirectoryNamespace, diag.DirectoryDeviceId).Should().Be((DirA, DevX));
        (diag.ConflictDirectoryNamespace, diag.ConflictDirectoryDeviceId).Should().Be((DirB, DevX));
    }

    [Fact]
    public async Task DirectoryAndIdentifierChange_PreservesTheObservedPair()
    {
        var defender = Seed(TenantA, ConnectorCapability.VulnerabilityScanner, DirA);
        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevX)));
        await Sync(defender);
        var assetId = (await Binding(defender, "m-1")).AssetId;

        SetDirectory(defender, DirB);
        _src.DefenderMachines = Page(SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", Q(DevY)));
        await Sync(defender);

        var m1 = await Binding(defender, "m-1");
        m1.AssetId.Should().Be(assetId);
        m1.ConflictKind.Should().Be(AssetBindingConflictKind.DirectoryAndIdentifierChanged);
        (m1.DirectoryNamespace, m1.DirectoryDeviceId).Should().Be((DirA, DevX));
        (m1.ConflictDirectoryNamespace, m1.ConflictDirectoryDeviceId).Should().Be((DirB, DevY));
        (await Sources(assetId))!.Sources.Single().ResolutionLabel.Should().Be("Conflito: diretório e identificador mudaram");
    }

    // ================= Marca de precedência da fonte ===============================================================

    [Fact]
    public async Task OrdinaryConnectorUpdates_NeverClearOrRegressTheSnapshotWatermark()
    {
        var intune = Seed(TenantA, ConnectorCapability.ConfigAnalyzer, DirA);
        // Contexto que carregou o conector ANTES da coleta e só grava DEPOIS — como um carimbo de status atrasado.
        await using var early = NewContext(TenantA);
        var stale = await early.Connectors.SingleAsync(c => c.Id == intune);
        stale.DeviceSnapshotWatermark.Should().BeNull("nenhuma fotografia publicada ainda");

        _src.IntuneDevices = Page(SyntheticDeviceSources.Device("dev-1", Q(DevX)));
        await Sync(intune);
        var published = await Watermark(intune);
        published.Should().NotBeNull();

        stale.DisplayName = "Microsoft Intune · Dispositivos (renomeado, sintético)";
        stale.LastStatus = ConnectorStatus.Degraded;
        await early.SaveChangesAsync();
        (await Watermark(intune)).Should().Be(published, "o change tracker só grava as colunas alteradas");

        SetDirectory(intune, DirA);   // reconfiguração da credencial
        (await Watermark(intune)).Should().Be(published);

        await Sync(intune);
        (await Watermark(intune)).Should().BeAfter(published!.Value, "a marca só avança com uma fotografia mais recente");
    }

    private async Task<DateTimeOffset?> Watermark(Guid connectorId)
    {
        await using var db = NewContext(TenantA);
        return (await db.Connectors.AsNoTracking().SingleAsync(c => c.Id == connectorId)).DeviceSnapshotWatermark;
    }

    // ================= Contrato da autoridade de resolução ========================================================
    // Único grupo que chama a autoridade diretamente: prova que a regra das repetições vale no CONTRATO, também para
    // um chamador que não passe pela fronteira dos conectores — sem regra divergente.

    [Fact]
    public async Task ResolverContract_RepeatedObservations_AreCombinedIndependentlyOfOrder_AndOutOfContractIdsAreRejected()
    {
        var x = new DeviceSourceObservation("dev-1", DeviceDirectoryIdentifiers.ParseDeviceId(DevX), null, "Windows",
            DateTimeOffset.Parse("2026-09-10T08:00:00Z"), DeviceComplianceBucket.Compliant, DeviceEncryptionBucket.Encrypted);
        var y = x with
        {
            DirectoryDeviceId = DeviceDirectoryIdentifiers.ParseDeviceId(DevY),
            SourceLastSeenAt = DateTimeOffset.Parse("2026-09-10T09:00:00Z"),
        };
        DeviceSourceObservations.Consolidate(new[] { x, y }).Single()
            .Should().Be(DeviceSourceObservations.Consolidate(new[] { y, x }).Single());
        DeviceSourceObservations.Consolidate(new[] { x, y, x }).Single()
            .Should().Be(DeviceSourceObservations.Consolidate(new[] { x, x, y }).Single());
        DeviceSourceObservations.Consolidate(new[] { x, x }).Single().Should().Be(x, "repetição equivalente não muda nada");
        DeviceDirectoryIdentifiers.Merge(x.DirectoryDeviceId, DirectoryDeviceIdObservation.NotProvided).Status
            .Should().Be(DirectoryIdentifierStatus.Contradictory, "valor e ausência também divergem");

        var tooLong = x with { ExternalId = new string('d', DeviceSourceObservations.MaxExternalIdLength + 1) };
        foreach (var (tenant, input) in new[] { (TenantA, new[] { x, y, tooLong }), (TenantB, new[] { tooLong, y, x }) })
        {
            var connector = Seed(tenant, ConnectorCapability.ConfigAnalyzer, DirA);
            await using var db = NewContext(tenant);
            var result = await new DeviceIdentityResolver(db).ReconcileSnapshotAsync(
                connector, IntuneLabel, DirA, input, completeSnapshot: true, DateTimeOffset.UtcNow, CancellationToken.None);
            result.RejectedObservations.Should().Be(1);
            result.DeactivationApplied.Should().BeFalse("id recusado torna a passada incompleta");
            result.ContradictoryIdentifiers.Should().Be(1);
            var b = await Binding(connector, "dev-1", tenant);
            b.ResolutionState.Should().Be(AssetBindingResolutionState.Conflict);
            b.ConflictObservedDeviceIds.Should().Be(DevX + "," + DevY);
            b.SourceLastSeenAt.Should().Be(y.SourceLastSeenAt, "os demais fatos seguem uma ordem total, não a de chegada");
        }
    }

    // ================= Normalizador (autoridade única) ===========================================================

    [Theory]
    [InlineData(null, DirectoryIdentifierStatus.NotProvided, null)]
    [InlineData("   ", DirectoryIdentifierStatus.NotProvided, null)]
    [InlineData("00000000-0000-0000-0000-000000000000", DirectoryIdentifierStatus.Invalid, null)]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", DirectoryIdentifierStatus.Invalid, null)]
    [InlineData("11111111-1111-1111-1111-111111111111", DirectoryIdentifierStatus.Invalid, null)]
    [InlineData("{5e1f4a3b-7d6c-4e8f-a091-b2c3d4e5f607}", DirectoryIdentifierStatus.Invalid, null)]
    [InlineData("5e1f4a3b7d6c4e8fa091b2c3d4e5f607", DirectoryIdentifierStatus.Invalid, null)]
    [InlineData("pc-01.demo.example.com", DirectoryIdentifierStatus.Invalid, null)]
    [InlineData(" 5E1F4A3B-7D6C-4E8F-A091-B2C3D4E5F607 ", DirectoryIdentifierStatus.Provided, "5e1f4a3b-7d6c-4e8f-a091-b2c3d4e5f607")]
    public void DirectoryDeviceId_IsAcceptedOnlyWhenValidForTheType(string? raw, DirectoryIdentifierStatus status, string? value)
    {
        var parsed = DeviceDirectoryIdentifiers.ParseDeviceId(raw);
        parsed.Status.Should().Be(status);
        parsed.Value.Should().Be(value);
    }

    [Fact]
    public void DirectoryNamespace_ComesOnlyFromAGuidTenantId_NeverFromADomain()
    {
        DeviceDirectoryIdentifiers.NormalizeNamespace("contoso-sintetico.onmicrosoft.com").Should().BeNull();
        DeviceDirectoryIdentifiers.NormalizeNamespace(null).Should().BeNull();
        DeviceDirectoryIdentifiers.NormalizeNamespace(DirA.ToUpperInvariant()).Should().Be(DirA);
    }

    /// <summary>Troca o diretório (tenant do Entra) da credencial da integração — como uma reconfiguração real.</summary>
    private void SetDirectory(Guid connectorId, string directory)
    {
        using var db = NewContext(TenantA);
        var c = db.Connectors.Single(x => x.Id == connectorId);
        c.EncryptedSettings = SyntheticDeviceSources.Connector(TenantA, c.Capability, directory).EncryptedSettings;
        db.SaveChanges();
    }

    private Guid SeedLegacyAsset(Guid defenderConnector, string externalId)
    {
        // Linhas COMO ERAM antes deste pacote: ativo com atributos curados e binding sem nenhuma coluna de
        // resolução preenchida (NotEvaluated) — o que a migration aditiva deixa em bancos existentes.
        using var db = NewContext(TenantA);
        var asset = new Asset
        {
            Name = "Servidor de arquivos (curado)",
            OwnerName = "Equipe de Infraestrutura",
            Criticality = 3,
            Category = AssetCategory.Hardware,
            DiscoverySource = AssetDiscoverySource.Connector,
            IsActive = true,
        };
        db.Assets.Add(asset);
        db.AssetSourceBindings.Add(new AssetSourceBinding
        {
            AssetId = asset.Id,
            ConnectorConfigId = defenderConnector,
            ExternalId = externalId,
            DisplayName = "fs01.demo.example.com",
            FirstObservedAt = DateTimeOffset.UtcNow.AddDays(-30),
            LastObservedAt = DateTimeOffset.UtcNow.AddDays(-1),
        });
        db.SaveChanges();
        return asset.Id;
    }
}

/// <summary>
/// [AEGIS-ENTITY-RESOLUTION-01] As DUAS fontes reais (Defender e Intune) sobre HTTP SINTÉTICO, compartilhado pela
/// bateria SQLite e pela PostgreSQL: os mesmos conectores, o mesmo executor, as mesmas URLs oficiais. Tudo aqui é
/// dado inventado (GUIDs aleatórios fixos, domínios demo.example.com).
/// </summary>
internal sealed class SyntheticDeviceSources
{
    public const string DirA = "3c9f2d1e-5b4a-4c6d-8e7f-90a1b2c3d4e5";
    public const string DirB = "4d0e3f2a-6c5b-4d7e-9f80-a1b2c3d4e5f6";
    public const string DevX = "5e1f4a3b-7d6c-4e8f-a091-b2c3d4e5f607";
    public const string DevY = "6f2a5b4c-8e7d-4f90-b1a2-c3d4e5f60718";

    private const string TokenJson = """{"access_token":"fake-access-token","expires_in":3600,"token_type":"Bearer"}""";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-10T12:00:00Z");

    // Cada coleta do Intune começa num instante DIFERENTE (e crescente), como na vida real: é esse instante que marca a
    // fotografia e define a precedência entre passadas do mesmo conector. Um relógio parado faria duas fotografias
    // diferentes parecerem a mesma. Estático: instâncias diferentes (ex.: uma por rodada) também avançam.
    private static int _syncs;

    public const string CveCatalog =
        """{"value":[{"id":"CVE-2024-7256","name":"CVE-2024-7256","severity":"High","cvssV3":8}]}""";
    private const string SoftwareCatalog =
        """{"value":[{"id":"google-_-chrome","name":"chrome","vendor":"google","weaknesses":1,"publicExploit":false,"activeAlert":false,"exposedMachines":1,"impactScore":2.5}]}""";

    // Fotografia de cada fonte — o teste monta, o handler lê no momento da requisição.
    public string DefenderMachines { get; set; } = Page();
    public string DefenderRelations { get; set; } = Page();
    public string DefenderCves { get; set; } = Page();
    public string DefenderSoftware { get; set; } = Page();
    public string DefenderInstalls { get; set; } = Page();
    public string IntuneDevices { get; set; } = Page();
    public Func<HttpRequestMessage, (HttpStatusCode, string)>? IntuneRoute { get; set; }

    // Segunda página REAL (via @odata.nextLink na origem oficial): quando definida, a primeira resposta aponta para
    // ela — é assim que se prova que o resultado não depende da ordem das páginas.
    public string? DefenderMachinesPage2 { get; set; }
    public string? IntuneDevicesPage2 { get; set; }

    private const string DefenderMachinesNext = "https://api.security.microsoft.com/api/machines?$skiptoken=p2";
    private const string IntuneDevicesNext = "https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$skiptoken=p2";

    private static string WithNextLink(string page, string next) => page[..^1] + ",\"@odata.nextLink\":\"" + next + "\"}";

    private static (HttpStatusCode, string) Paged(HttpRequestMessage req, string first, string? second, string next) =>
        second is null ? (HttpStatusCode.OK, first)
        : req.RequestUri!.Query.Contains("skiptoken") ? (HttpStatusCode.OK, second)
        : (HttpStatusCode.OK, WithNextLink(first, next));

    public void DefenderWithVulnerabilityAndSoftware(string machineJson)
    {
        using var doc = JsonDocument.Parse(machineJson);
        var id = doc.RootElement.GetProperty("id").GetString()!;
        DefenderMachines = Page(machineJson);
        DefenderRelations = Page(Relation(id));
        DefenderCves = CveCatalog;
        DefenderSoftware = SoftwareCatalog;
        DefenderInstalls = Page(Install(id));
    }

    public static ConnectorConfig Connector(Guid tenant, ConnectorCapability capability, string directory) => new()
    {
        TenantId = tenant,
        Provider = ConnectorProvider.Microsoft,
        Capability = capability,
        DisplayName = capability == ConnectorCapability.VulnerabilityScanner
            ? "Microsoft Defender · Vulnerabilidades (sintético)"
            : "Microsoft Intune · Dispositivos (sintético)",
        AuthType = ConnectorAuthType.OAuthClientCredentials,
        Enabled = true,
        EncryptedSettings = "{\"tenantId\":\"" + directory + "\",\"clientId\":\"app-sintetico\",\"clientSecret\":\"segredo-sintetico\"}",
    };

    public async Task<PullIngestionResult> SyncAsync(
        DbContextOptions<AegisScoreDbContext> options, Guid tenant, Guid connectorId)
    {
        ConnectorConfig config;
        await using (var db = new AegisScoreDbContext(options, new SystemTenantContext(tenant)))
            config = await db.Connectors.AsNoTracking().SingleAsync(c => c.Id == connectorId);

        var registry = new Registry(
            new MicrosoftDefenderVulnerabilityConnector(new DefenderApiClient(new HttpClient(DefenderHandler())), new Passthrough()),
            new MicrosoftIntuneDevicePostureConnector(
                new EntraGraphClient(new HttpClient(IntuneHandler())), new Passthrough(),
                new FakeTimeProvider(Now.AddSeconds(Interlocked.Increment(ref _syncs)))));
        var executor = new EvidenceIngestionExecutor(
            options, new NistSignalMapper(new AegisScoreDbContext(options, new SystemTenantContext(null))),
            new Payload(), registry, NullLogger<EvidenceIngestionExecutor>.Instance, NullLogger<ControlStateWriter>.Instance);
        return (await executor.CollectPullAsync(config, CancellationToken.None))!;
    }

    public static ControllerContext ControllerContextFor(string role) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("role", role) }, "sintetico", "name", "role")),
        },
    };

    // ---- JSON no formato OFICIAL das fontes -----------------------------------------------------------------------

    public static string Q(string s) => "\"" + s + "\"";

    public static string Page(params string[] items) => "{\"value\":[" + string.Join(",", items) + "]}";

    /// <summary>Máquina do Defender (recurso Machine). <paramref name="aadJson"/> é o token JSON cru, ou null para omitir.</summary>
    public static string Machine(string id, string? dns, string? aadJson) =>
        "{\"id\":" + Q(id) + ",\"osPlatform\":\"Windows11\",\"lastSeen\":\"2026-09-10T10:00:00Z\"" +
        (dns is null ? "" : ",\"computerDnsName\":" + Q(dns)) +
        (aadJson is null ? "" : ",\"aadDeviceId\":" + aadJson) + "}";

    /// <summary>managedDevice do Intune. <paramref name="aadJson"/> é o token JSON cru, ou null para omitir.</summary>
    public static string Device(string id, string? aadJson, string compliance = "compliant", bool? encrypted = true) =>
        "{\"id\":" + Q(id) + ",\"complianceState\":" + Q(compliance) +
        ",\"operatingSystem\":\"Windows\",\"lastSyncDateTime\":\"2026-09-10T09:00:00Z\"" +
        (encrypted is null ? "" : ",\"isEncrypted\":" + (encrypted.Value ? "true" : "false")) +
        (aadJson is null ? "" : ",\"azureADDeviceId\":" + aadJson) + "}";

    public static string Relation(string machineId, string cve = "CVE-2024-7256") =>
        "{\"machineId\":" + Q(machineId) + ",\"cveId\":" + Q(cve) +
        ",\"productName\":\"chrome\",\"productVendor\":\"google\",\"productVersion\":\"1.0\",\"severity\":\"High\"}";

    public static string Install(string machineId) =>
        "{\"deviceId\":" + Q(machineId) + ",\"softwareVendor\":\"google\",\"softwareName\":\"chrome\",\"softwareVersion\":\"1.0\"}";

    // ---- Transporte sintético -------------------------------------------------------------------------------------

    private static bool IsToken(HttpRequestMessage req) =>
        req.Method == HttpMethod.Post && req.RequestUri!.AbsoluteUri.Contains("/oauth2/v2.0/token");

    private HttpMessageHandler DefenderHandler() => new Stub(req =>
    {
        if (IsToken(req)) return (HttpStatusCode.OK, TokenJson);
        var p = req.RequestUri!.AbsolutePath;
        if (p.Contains("SoftwareInventoryByMachine")) return (HttpStatusCode.OK, DefenderInstalls);
        if (p.Contains("machinesVulnerabilities")) return (HttpStatusCode.OK, DefenderRelations);
        if (p.Contains("/api/vulnerabilities")) return (HttpStatusCode.OK, DefenderCves);
        if (p.Contains("/api/machines")) return Paged(req, DefenderMachines, DefenderMachinesPage2, DefenderMachinesNext);
        if (p == "/api/Software") return (HttpStatusCode.OK, DefenderSoftware);
        return (HttpStatusCode.NotFound, "{}");
    });

    private HttpMessageHandler IntuneHandler() => new Stub(req =>
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
        if (url.Contains("deviceCompliancePolicies") || url.Contains("deviceConfigurations"))
            return (HttpStatusCode.OK, Page());
        if (url.Contains("managedDevices"))
            return IntuneRoute?.Invoke(req) ?? Paged(req, IntuneDevices, IntuneDevicesPage2, IntuneDevicesNext);
        return (HttpStatusCode.NotFound, """{"error":{"code":"notFound"}}""");
    });

    private sealed class Stub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _route;
        public Stub(Func<HttpRequestMessage, (HttpStatusCode, string)> route) => _route = route;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = _route(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class Registry : IConnectorRegistry
    {
        private readonly IEvidenceConnector[] _all;
        public Registry(params IEvidenceConnector[] all) => _all = all;
        public IReadOnlyList<IEvidenceConnector> All => _all;
        public IEvidenceConnector? Resolve(ConnectorProvider provider, ConnectorCapability capability) =>
            _all.FirstOrDefault(c => c.Provider == provider && c.Capability == capability);
    }

    private sealed class Passthrough : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class Payload : IEvidenceRawPayloadProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
