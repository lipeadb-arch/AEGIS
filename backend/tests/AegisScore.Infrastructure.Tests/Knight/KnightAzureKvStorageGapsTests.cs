using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-AZURE-KV-STORAGE-GAPS-01] AK-AZ-KV-008 e AK-AZ-STO-005/006/017 pelo percurso inteiro (respostas sintéticas do
/// Resource Manager → coletor real → ADM → releitura → regra). Cada teste isola a população na assinatura A (a B fica vazia)
/// para que o estado do controle reflita só o caso montado.
/// </summary>
public sealed class KnightAzureKvStorageGapsTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0505-0505-0505-0000000000a5");
    private static readonly string StorageListA = $"/subscriptions/{AzureScenario.SubA}/providers/Microsoft.Storage/storageAccounts";
    private static readonly string StorageListB = $"/subscriptions/{AzureScenario.SubB}/providers/Microsoft.Storage/storageAccounts";
    private static readonly string VaultListA = $"/subscriptions/{AzureScenario.SubA}/providers/Microsoft.KeyVault/vaults";
    private static readonly string VaultListB = $"/subscriptions/{AzureScenario.SubB}/providers/Microsoft.KeyVault/vaults";
    private static readonly object Standard = new { name = "Standard_LRS", tier = "Standard" };

    private readonly SqliteConnection _connection;
    private readonly ITestOutputHelper _output;

    public KnightAzureKvStorageGapsTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ==== AK-AZ-KV-008 ==================================================================================

    private static string VaultId(string name) => AzureScenario.ResourceId(AzureScenario.SubA, "Microsoft.KeyVault", "vaults", name);

    /// <summary>Cofre conforme em tudo, SEM a lista embutida de conexões (como o provedor responde quando não há nenhuma).</summary>
    private static object Vault(string name, object? embedded = null)
    {
        var props = new Dictionary<string, object?>
        {
            ["enablePurgeProtection"] = true, ["enableSoftDelete"] = true, ["enableRbacAuthorization"] = true,
            ["publicNetworkAccess"] = "Disabled", ["networkAcls"] = new { defaultAction = "Deny" }, ["vaultUri"] = $"https://{name}.vault.azure.net/",
        };
        if (embedded is not null) props["privateEndpointConnections"] = embedded;
        return AzureScenario.Resource(VaultId(name), "Microsoft.KeyVault/vaults", props);
    }

    private static object Conn(string name, string status) => new
    {
        id = $"{VaultId("kv-demo-rbac")}/privateEndpointConnections/{name}", name, type = "Microsoft.KeyVault/vaults/privateEndpointConnections",
        properties = new
        {
            provisioningState = "Succeeded",
            privateEndpoint = new { id = AzureScenario.ResourceId(AzureScenario.SubA, "Microsoft.Network", "privateEndpoints", name) },
            privateLinkServiceConnectionState = new { status, description = "sintético", actionsRequired = "None" },
        },
    };

    private static AzureScenario VaultScenario(object vault) =>
        new AzureScenario(AzureScenario.Variant.Compliant).Override(VaultListA, new { value = new[] { vault } }).Override(VaultListB, new { value = Array.Empty<object>() });

    [Fact]
    public async Task Kv008_ListaLidaVazia_Reprova_SemListaEmbutida()
    {
        var scenario = VaultScenario(Vault("kv-demo-rbac"))
            .Override($"{VaultId("kv-demo-rbac")}/privateEndpointConnections", new { value = Array.Empty<object>() });
        var (run, svc) = await RunAsync(scenario);

        var kv = Indicator(run, "AK-AZ-KV-008");
        kv.Status.Should().Be(KnightIndicatorStatus.Exposed, "a subcoleção lida e vazia comprova a ausência de endpoint privado");
        var affected = await Objects(svc, run, "AK-AZ-KV-008", KnightObjectRelation.Affected);
        affected.Should().ContainSingle().Which.Detail.Should().Contain("lista lida vazia");
        scenario.Requests.Should().Contain(u => u.Contains("/vaults/kv-demo-rbac/privateEndpointConnections?api-version=2023-07-01", StringComparison.Ordinal),
            "a leitura é a PrivateEndpointConnections_ListByResource, em versão estável");
    }

    [Fact]
    public async Task Kv008_LeituraRecusada_FicaSemConclusao_NuncaSemEndpoint()
    {
        var scenario = VaultScenario(Vault("kv-demo-rbac")).Refuse($"{VaultId("kv-demo-rbac")}/privateEndpointConnections");
        var (run, svc) = await RunAsync(scenario);

        var kv = Indicator(run, "AK-AZ-KV-008");
        kv.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, "403 é lacuna de acesso, não lista vazia");
        (await Objects(svc, run, "AK-AZ-KV-008", KnightObjectRelation.Affected)).Should().BeEmpty();
        (await Objects(svc, run, "AK-AZ-KV-008", KnightObjectRelation.Evidence)).Should().ContainSingle()
            .Which.Detail.Should().Contain("não lidas").And.Contain("leitura recusada");
        run.Capabilities.Single(c => c.Capability == KnightCapability.AzureKeyVault).Detail.Should().Contain("leitura(s) filha(s) recusada(s)");
        // Os demais controles do cofre continuam avaliados: a recusa é só desta leitura.
        Indicator(run, "AK-AZ-KV-007").Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    [Fact]
    public async Task Kv008_ConexaoAprovadaNaSubcolecao_Aprova()
    {
        var scenario = VaultScenario(Vault("kv-demo-rbac"))
            .Override($"{VaultId("kv-demo-rbac")}/privateEndpointConnections", new { value = new[] { Conn("pe-pendente", "Pending"), Conn("pe-app", "Approved") } });
        var (run, svc) = await RunAsync(scenario);

        Indicator(run, "AK-AZ-KV-008").Status.Should().Be(KnightIndicatorStatus.Passed);
        (await Objects(svc, run, "AK-AZ-KV-008", KnightObjectRelation.Evidence)).Should().ContainSingle()
            .Which.Detail.Should().Contain("1 conexão(ões) de endpoint privado aprovada(s)");
    }

    [Fact]
    public async Task Kv008_SoPendenteERejeitada_Reprova_ComOsEstadosDitos()
    {
        var scenario = VaultScenario(Vault("kv-demo-rbac"))
            .Override($"{VaultId("kv-demo-rbac")}/privateEndpointConnections", new { value = new[] { Conn("pe-a", "Pending"), Conn("pe-b", "Rejected") } });
        var (run, svc) = await RunAsync(scenario);

        Indicator(run, "AK-AZ-KV-008").Status.Should().Be(KnightIndicatorStatus.Exposed);
        (await Objects(svc, run, "AK-AZ-KV-008", KnightObjectRelation.Affected)).Should().ContainSingle()
            .Which.Detail.Should().Contain("Pending: 1").And.Contain("Rejected: 1");
    }

    [Fact]
    public async Task Kv008_SubcolecaoRecusada_MasAprovadaNaListaEmbutida_Aprova()
    {
        var scenario = VaultScenario(Vault("kv-demo-rbac", embedded: new[] { Conn("pe-app", "Approved") }))
            .Refuse($"{VaultId("kv-demo-rbac")}/privateEndpointConnections");
        var (run, _) = await RunAsync(scenario);

        Indicator(run, "AK-AZ-KV-008").Status.Should().Be(KnightIndicatorStatus.Passed,
            "a conexão aprovada informada pelo próprio cofre é evidência positiva, mesmo com a subcoleção recusada");
    }

    /// <summary>Mais itens que o teto da leitura filha (2000): a lista gravada é parcial (pe:truncated).</summary>
    private static object[] Many(int count, string status, int approvedAt = -1) =>
        Enumerable.Range(0, count).Select(i => Conn($"pe-{i:0000}", i == approvedAt ? "Approved" : status)).ToArray();

    [Fact]
    public async Task Kv008_ListaTruncadaSemAprovada_FicaSemConclusao()
    {
        var scenario = VaultScenario(Vault("kv-demo-rbac"))
            .Override($"{VaultId("kv-demo-rbac")}/privateEndpointConnections", new { value = Many(2001, "Pending") });
        var (run, svc) = await RunAsync(scenario);

        Indicator(run, "AK-AZ-KV-008").Status.Should().Be(KnightIndicatorStatus.NotEvaluated,
            "sem aprovada entre as lidas, a parte não lida ainda pode ter uma — lista parcial não reprova");
        (await Objects(svc, run, "AK-AZ-KV-008", KnightObjectRelation.Affected)).Should().BeEmpty();
        (await Objects(svc, run, "AK-AZ-KV-008", KnightObjectRelation.Evidence)).Should().ContainSingle()
            .Which.Detail.Should().Contain("lista de conexões de endpoint privado parcial").And.Contain("2000 lida(s)");
    }

    [Fact]
    public async Task Kv008_ListaTruncadaComAprovada_Aprova()
    {
        var scenario = VaultScenario(Vault("kv-demo-rbac"))
            .Override($"{VaultId("kv-demo-rbac")}/privateEndpointConnections", new { value = Many(2001, "Pending", approvedAt: 5) });
        var (run, _) = await RunAsync(scenario);

        Indicator(run, "AK-AZ-KV-008").Status.Should().Be(KnightIndicatorStatus.Passed, "conexão aprovada lida é evidência positiva");
    }

    [Fact]
    public async Task Kv008_ConexaoSemEstado_FicaSemConclusao_ESemEstadoComAprovada_Aprova()
    {
        var stateless = new { name = "pe-sem-estado", properties = new { privateEndpoint = new { id = "/x/privateEndpoints/pe-sem-estado" } } };
        var (semEstado, svc) = await RunAsync(VaultScenario(Vault("kv-demo-rbac"))
            .Override($"{VaultId("kv-demo-rbac")}/privateEndpointConnections", new { value = new object[] { stateless, Conn("pe-b", "Rejected") } }));
        Indicator(semEstado, "AK-AZ-KV-008").Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        (await Objects(svc, semEstado, "AK-AZ-KV-008", KnightObjectRelation.Evidence)).Should().ContainSingle()
            .Which.Detail.Should().Contain("1 conexão(ões) de endpoint privado sem estado informado");

        var (comAprovada, _) = await RunAsync(VaultScenario(Vault("kv-demo-rbac"))
            .Override($"{VaultId("kv-demo-rbac")}/privateEndpointConnections", new { value = new object[] { stateless, Conn("pe-app", "Approved") } }));
        Indicator(comAprovada, "AK-AZ-KV-008").Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    // ==== blobServices/default fora do contrato ===========================================================

    [Theory]
    [InlineData("")]
    [InlineData("""{"id":"x","name":"default"}""")]
    [InlineData("""{"id":"x","name":"default","properties":"texto"}""")]
    [InlineData("""{"id":"x","name":"default","properties":null}""")]
    [InlineData("<html>não é JSON</html>")]
    public async Task Sto005e006_RespostaSemPropertiesValido_DiagnosticoDeContrato_SemConclusao(string body)
    {
        var scenario = StorageScenario(Account("st-contrato", "StorageV2", Standard)).Override($"{AccountId("st-contrato")}/blobServices/default", body);
        var (run, svc) = await RunAsync(scenario);

        foreach (var id in new[] { "AK-AZ-STO-005", "AK-AZ-STO-006" })
        {
            Indicator(run, id).Status.Should().Be(KnightIndicatorStatus.NotEvaluated, id + ": corpo sem properties não comprova proteção inativa");
            (await Objects(svc, run, id, KnightObjectRelation.Evidence)).Should().ContainSingle()
                .Which.Detail.Should().Contain("serviço de blobs não lida (resposta fora do contrato documentado)", id);
        }
        run.Capabilities.Single(c => c.Capability == KnightCapability.AzureStorage).Detail.Should()
            .Contain("fora do contrato documentado");
    }

    [Fact]
    public async Task Sto005e006_PropertiesVazioValido_OmissaoContinuaProtecaoInativa()
    {
        var scenario = Blob(StorageScenario(Account("st-vazio", "StorageV2", Standard)), "st-vazio", new { });
        var (run, svc) = await RunAsync(scenario);

        foreach (var id in new[] { "AK-AZ-STO-005", "AK-AZ-STO-006" })
        {
            Indicator(run, id).Status.Should().Be(KnightIndicatorStatus.Exposed, id);
            (await Objects(svc, run, id, KnightObjectRelation.Affected)).Should().ContainSingle()
                .Which.Detail.Should().Contain("ausente na configuração lida do serviço de blobs", id);
        }
    }

    // ==== AK-AZ-STO-005 / 006 ===========================================================================

    private static string AccountId(string name) => AzureScenario.ResourceId(AzureScenario.SubA, "Microsoft.Storage", "storageAccounts", name);

    private static object Account(string name, string kind, object? sku, bool? hns = null, string? created = null, bool? crossTenant = false)
    {
        var props = new Dictionary<string, object?>
        {
            ["minimumTlsVersion"] = "TLS1_2", ["allowBlobPublicAccess"] = false, ["supportsHttpsTrafficOnly"] = true,
            ["allowSharedKeyAccess"] = false, ["publicNetworkAccess"] = "Disabled",
        };
        if (hns is not null) props["isHnsEnabled"] = hns;
        if (created is not null) props["creationTime"] = created;
        if (crossTenant is not null) props["allowCrossTenantReplication"] = crossTenant;
        return AzureScenario.Resource(AccountId(name), "Microsoft.Storage/storageAccounts", props, kind, sku);
    }

    private static AzureScenario StorageScenario(params object[] accounts) =>
        new AzureScenario(AzureScenario.Variant.Compliant).Override(StorageListA, new { value = accounts }).Override(StorageListB, new { value = Array.Empty<object>() });

    private static AzureScenario Blob(AzureScenario s, string account, object properties) =>
        s.Override($"{AccountId(account)}/blobServices/default", new { id = $"{AccountId(account)}/blobServices/default", name = "default", properties });

    [Fact]
    public async Task Sto005e006_CamposOmitidosComLeituraConcluida_Reprovam_ComAOmissaoDita()
    {
        var scenario = Blob(StorageScenario(Account("st-omitido", "StorageV2", Standard)), "st-omitido",
            new { deleteRetentionPolicy = new { enabled = true, days = 7 } });
        var (run, svc) = await RunAsync(scenario);

        Indicator(run, "AK-AZ-STO-005").Status.Should().Be(KnightIndicatorStatus.Exposed);
        (await Objects(svc, run, "AK-AZ-STO-005", KnightObjectRelation.Affected)).Should().ContainSingle()
            .Which.Detail.Should().Contain("exclusão reversível de contêineres: desabilitada").And.Contain("política ausente");
        Indicator(run, "AK-AZ-STO-006").Status.Should().Be(KnightIndicatorStatus.Exposed);
        (await Objects(svc, run, "AK-AZ-STO-006", KnightObjectRelation.Affected)).Should().ContainSingle()
            .Which.Detail.Should().Contain("controle de versão: desabilitado").And.Contain("propriedade ausente");
        Indicator(run, "AK-AZ-STO-004").Status.Should().Be(KnightIndicatorStatus.Passed, "o campo presente continua lido como antes");
    }

    [Fact]
    public async Task Sto005e006_ServicoDeBlobsRecusado_FicaSemConclusao_ComACausa()
    {
        var scenario = StorageScenario(Account("st-recusado", "StorageV2", Standard)).Refuse($"{AccountId("st-recusado")}/blobServices/default");
        var (run, svc) = await RunAsync(scenario);

        foreach (var id in new[] { "AK-AZ-STO-005", "AK-AZ-STO-006" })
        {
            Indicator(run, id).Status.Should().Be(KnightIndicatorStatus.NotEvaluated, id);
            (await Objects(svc, run, id, KnightObjectRelation.Evidence)).Should().ContainSingle()
                .Which.Detail.Should().Contain("serviço de blobs não lida (leitura recusada pelo Resource Manager)", id);
        }
    }

    [Fact]
    public async Task Sto005e006_FormaInesperada_FicaSemConclusao()
    {
        var scenario = StorageScenario(Account("st-texto", "StorageV2", Standard), Account("st-sem-enabled", "StorageV2", Standard));
        Blob(scenario, "st-texto", new { containerDeleteRetentionPolicy = "sim", isVersioningEnabled = "talvez" });
        Blob(scenario, "st-sem-enabled", new { containerDeleteRetentionPolicy = new { days = 7 }, isVersioningEnabled = 1 });
        var (run, svc) = await RunAsync(scenario);

        Indicator(run, "AK-AZ-STO-005").Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        var c = await Objects(svc, run, "AK-AZ-STO-005", KnightObjectRelation.Evidence);
        c.Should().Contain(o => o.DisplayName!.StartsWith("st-texto") && o.Detail!.Contains("política em formato inesperado"));
        c.Should().Contain(o => o.DisplayName!.StartsWith("st-sem-enabled") && o.Detail!.Contains("sem o campo enabled"));
        Indicator(run, "AK-AZ-STO-006").Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        (await Objects(svc, run, "AK-AZ-STO-006", KnightObjectRelation.Evidence)).Should().HaveCount(2)
            .And.OnlyContain(o => o.Detail!.Contains("valor em formato inesperado"));
    }

    [Fact]
    public async Task Sto006_TiposSemSuporte_FicamForaDaPopulacao_EStO005ContinuaValendo()
    {
        var scenario = StorageScenario(
            Account("st-v1", "Storage", Standard),
            Account("st-premium", "StorageV2", new { name = "Premium_LRS", tier = "Premium" }),
            Account("st-datalake", "StorageV2", Standard, hns: true),
            Account("st-blocos", "BlockBlobStorage", new { name = "Premium_LRS", tier = "Premium" }));
        foreach (var n in new[] { "st-v1", "st-premium", "st-datalake" })
            Blob(scenario, n, new { containerDeleteRetentionPolicy = new { enabled = true, days = 7 } });
        Blob(scenario, "st-blocos", new { containerDeleteRetentionPolicy = new { enabled = true, days = 7 }, isVersioningEnabled = true });
        var (run, svc) = await RunAsync(scenario);

        Indicator(run, "AK-AZ-STO-006").Status.Should().Be(KnightIndicatorStatus.Passed,
            "v1, v2 premium e namespace hierárquico não suportam controle de versão; só o blob de blocos premium é avaliado");
        (await Objects(svc, run, "AK-AZ-STO-006", KnightObjectRelation.Evidence)).Should().ContainSingle()
            .Which.DisplayName.Should().StartWith("st-blocos");
        Indicator(run, "AK-AZ-STO-005").Status.Should().Be(KnightIndicatorStatus.Passed);
        (await Objects(svc, run, "AK-AZ-STO-005", KnightObjectRelation.Evidence)).Should().HaveCount(4,
            "a exclusão reversível de contêineres vale para os quatro tipos");
    }

    [Fact]
    public async Task Sto006_SoContasSemSuporte_NaoSeAplica()
    {
        var scenario = StorageScenario(Account("st-v1", "Storage", Standard));
        Blob(scenario, "st-v1", new { containerDeleteRetentionPolicy = new { enabled = true, days = 7 } });
        var (run, _) = await RunAsync(scenario);

        Indicator(run, "AK-AZ-STO-006").Status.Should().Be(KnightIndicatorStatus.NotApplicable);
    }

    [Fact]
    public async Task Sto006_CamadaNaoInformada_FicaSemConclusao()
    {
        var scenario = StorageScenario(Account("st-sem-camada", "StorageV2", sku: null));
        Blob(scenario, "st-sem-camada", new { containerDeleteRetentionPolicy = new { enabled = true, days = 7 } });
        var (run, svc) = await RunAsync(scenario);

        Indicator(run, "AK-AZ-STO-006").Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        (await Objects(svc, run, "AK-AZ-STO-006", KnightObjectRelation.Evidence)).Should().ContainSingle()
            .Which.Detail.Should().Contain("camada de desempenho da conta não informada");
    }

    // ==== AK-AZ-STO-017 =================================================================================

    [Fact]
    public async Task Sto017_OmissaoSegueOPadraoDaDataDeCriacao_ESemDataFicaDesconhecido()
    {
        var scenario = StorageScenario(
            Account("st-explicita", "StorageV2", Standard, created: "2025-01-10T12:00:00Z", crossTenant: true),
            Account("st-antiga", "StorageV2", Standard, created: "2022-05-01T09:00:00.0000000Z", crossTenant: null),
            Account("st-nova", "StorageV2", Standard, created: "2024-03-01T09:00:00Z", crossTenant: null),
            Account("st-sem-data", "StorageV2", Standard, crossTenant: null),
            Account("st-dia-da-mudanca", "StorageV2", Standard, created: "2023-12-15T18:00:00Z", crossTenant: null));
        var (run, svc) = await RunAsync(scenario);

        Indicator(run, "AK-AZ-STO-017").Status.Should().Be(KnightIndicatorStatus.Exposed);
        var affected = await Objects(svc, run, "AK-AZ-STO-017", KnightObjectRelation.Affected);
        affected.Select(o => o.DisplayName!.Split(' ')[0]).Should().BeEquivalentTo("st-explicita", "st-antiga");
        affected.Single(o => o.DisplayName!.StartsWith("st-antiga")).Detail.Should()
            .Contain("não definida — padrão documentado para conta criada em 01/05/2022: permitida");
        affected.Single(o => o.DisplayName!.StartsWith("st-explicita")).Detail.Should().Contain("permitida (definida na conta)");
        var evidence = await Objects(svc, run, "AK-AZ-STO-017", KnightObjectRelation.Evidence);
        evidence.Single(o => o.DisplayName!.StartsWith("st-nova")).Detail.Should().Contain("conta criada em 01/03/2024: bloqueada");
        evidence.Single(o => o.DisplayName!.StartsWith("st-sem-data")).Detail.Should().Contain("data de criação da conta não informada");
        evidence.Single(o => o.DisplayName!.StartsWith("st-dia-da-mudanca")).Detail.Should().Contain("dia da mudança do padrão documentado");
    }

    [Fact]
    public async Task Sto017_ContaNovaSemValor_Aprova_EContaSemDataSozinha_NaoConclui()
    {
        var (nova, _) = await RunAsync(StorageScenario(Account("st-nova", "StorageV2", Standard, created: "2024-03-01T09:00:00Z", crossTenant: null)));
        Indicator(nova, "AK-AZ-STO-017").Status.Should().Be(KnightIndicatorStatus.Passed);

        var (semData, _) = await RunAsync(StorageScenario(Account("st-sem-data", "StorageV2", Standard, crossTenant: null)));
        Indicator(semData, "AK-AZ-STO-017").Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    // ==== Histórico =====================================================================================

    [Fact]
    public void RegrasRevisadas_IdentificadasPelaVersao_SemCriterioNovoEmFotografiaV12()
    {
        KnightCatalog.Version.Should().Be("ak-knight-v13");
        foreach (var id in new[] { "AK-AZ-KV-008", "AK-AZ-STO-005", "AK-AZ-STO-006", "AK-AZ-STO-017" })
        {
            var d = KnightCatalog.Indicators.Single(i => i.Id == id);
            d.Version.Should().Be("2", id);
            KnightControlPresentations.For(id, d.Category, d.Severity, KnightIndicatorStatus.Passed, KnightSourceType.MicrosoftAzure,
                d.NistCodes, d.MitreTechniques, "ak-knight-v12").Criterion.Should().BeNull(id + ": fotografia v12 não recebe critério novo");
        }
        // Pendências de acesso separadas: não mudam neste pacote.
        foreach (var id in new[] { "AK-AZ-KV-010", "AK-AZ-MON-003", "AK-AZ-MON-004" })
            KnightCatalog.Indicators.Single(i => i.Id == id).Version.Should().Be("1", id);
    }

    // ====================================================================================================

    private async Task<(KnightAssessment Run, IAegisKnightAssessmentService Svc)> RunAsync(AzureScenario scenario)
    {
        await SeedAsync();
        var db = NewContext(Tenant);
        var svc = KnightAzureFlowTests.ServiceFor(db, Tenant, scenario);
        var run = await svc.RunAssessmentAsync(KnightSourceType.MicrosoftAzure);
        foreach (var i in run.Indicators.Where(i => i.IndicatorId is "AK-AZ-KV-008" or "AK-AZ-STO-005" or "AK-AZ-STO-006" or "AK-AZ-STO-017"))
            _output.WriteLine($"{i.IndicatorId} {i.Status} [{i.AffectedObjectCount}/{i.EvidenceObjectCount}] {i.NotEvaluatedReason ?? i.Evidence}");
        return (run, svc);
    }

    private static KnightIndicatorView Indicator(KnightAssessment run, string id) => run.Indicators.Single(i => i.IndicatorId == id);

    private static async Task<IReadOnlyList<KnightAffectedObjectView>> Objects(IAegisKnightAssessmentService svc, KnightAssessment run, string id,
        KnightObjectRelation relation) =>
        (await svc.GetAffectedObjectsAsync(run.Id, id, 1, 50, null, default, relation))!.Items;

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private async Task SeedAsync()
    {
        await using (var db = NewContext(null))
        {
            if (await db.Tenants.AnyAsync(t => t.Id == Tenant)) return;
            db.Tenants.Add(new Tenant { Id = Tenant, Name = "Cliente Demo", Slug = $"t-{Tenant:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using var dbt = NewContext(Tenant);
        dbt.Connectors.Add(new ConnectorConfig
        {
            TenantId = Tenant, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
            DisplayName = "Microsoft 365", Enabled = true, EncryptedSettings = "cifrado",
        });
        await dbt.SaveChangesAsync();
    }
}
