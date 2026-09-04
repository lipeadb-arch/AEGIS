using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Connectors.Microsoft.Intune;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Domain;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-02] Coletor REAL do Microsoft Intune por HTTP SIMULADO (sem rede, sem
/// credenciais reais): exercita o protocolo verdadeiro nas URLs OFICIAIS (login.microsoftonline.com /
/// graph.microsoft.com v1.0) através do transporte ENDURECIDO já validado (<see cref="EntraGraphClient"/>).
///
/// Cobre TRANSPORTE (paginação, nextLink permitido, rejeição de host externo, cancelamento, 401/403/404/429/5xx,
/// falha em página intermediária) e COLETOR (as duas dimensões independentes, ausência de permissão de
/// dispositivos, coleção vazia real, valores nulos, enum novo da Microsoft, PII fora da fotografia e
/// idempotência). Nenhum caminho aqui produz EvidenceSignal.
/// </summary>
public sealed class IntuneDevicePostureCollectorTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string ClientSecret = "SUPER-SECRET-VALUE";
    private const string TokenJson = """{"access_token":"fake-access-token","expires_in":3600,"token_type":"Bearer"}""";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-04T12:00:00Z");

    private static ConnectorConfig Config() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.Parse("aa000000-0000-0000-0000-000000000002"),
        Provider = ConnectorProvider.Microsoft,
        Capability = ConnectorCapability.ConfigAnalyzer,
        Enabled = true,
        EncryptedSettings =
            $$"""{"tenantId":"{{TenantId}}","clientId":"app-client-id","clientSecret":"{{ClientSecret}}"}""",
    };

    private static MicrosoftIntuneDevicePostureConnector NewConnector(HttpMessageHandler handler) =>
        new(new EntraGraphClient(new HttpClient(handler)),
            new PassthroughProtector(),
            new FakeTimeProvider(Now));

    // ---- Fixtures de resposta -------------------------------------------------------------------

    private const string CompliancePoliciesJson = """
    {"value":[
      {"@odata.type":"#microsoft.graph.windows10CompliancePolicy","id":"pol-1","displayName":"Windows — linha de base",
       "description":"NAO DEVE SER PERSISTIDO","lastModifiedDateTime":"2026-08-01T10:00:00Z",
       "assignments":[{"id":"a1","target":{"@odata.type":"#microsoft.graph.allDevicesAssignmentTarget"}}]},
      {"@odata.type":"#microsoft.graph.iosCompliancePolicy","id":"pol-2","displayName":"iOS — linha de base",
       "assignments":[]}
    ]}
    """;

    private const string DeviceConfigurationsJson = """
    {"value":[
      {"@odata.type":"#microsoft.graph.windows10GeneralConfiguration","id":"cfg-1","displayName":"Windows — restrições",
       "assignments":[{"id":"a2","target":{}},{"id":"a3","target":{}}]}
    ]}
    """;

    private const string ManagedDevicesJson = """
    {"value":[
      {"id":"dev-1","azureADDeviceId":"aad-1","complianceState":"compliant","operatingSystem":"Windows",
       "lastSyncDateTime":"2026-09-03T12:00:00Z","isEncrypted":true},
      {"id":"dev-2","azureADDeviceId":"aad-2","complianceState":"noncompliant","operatingSystem":"Windows",
       "lastSyncDateTime":"2026-09-03T12:00:00Z","isEncrypted":false},
      {"id":"dev-3","complianceState":"inGracePeriod","operatingSystem":"iOS",
       "lastSyncDateTime":"2026-01-01T12:00:00Z","isEncrypted":true},
      {"id":"dev-4","complianceState":"unknown","operatingSystem":"Android"}
    ]}
    """;

    private const string EmptyPageJson = """{"value":[]}""";

    /// <summary>Roteador feliz: token + as três coleções (com <c>$expand</c>/<c>$select</c> honrados).</summary>
    private static StubHandler HappyHandler() => new(req =>
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
        if (url.Contains("deviceCompliancePolicies")) return (HttpStatusCode.OK, CompliancePoliciesJson);
        if (url.Contains("deviceConfigurations")) return (HttpStatusCode.OK, DeviceConfigurationsJson);
        if (url.Contains("managedDevices")) return (HttpStatusCode.OK, ManagedDevicesJson);
        return (HttpStatusCode.NotFound, """{"error":{"code":"notFound"}}""");
    });

    // ================= TRANSPORTE ==================================================================

    [Fact]
    public async Task Transport_FollowsNextLink_OnAllowedHost_AndAggregatesEveryPage()
    {
        var calls = new List<string>();
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            calls.Add(url);
            if (url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
            if (url.Contains("managedDevices"))
                return url.Contains("skiptoken")
                    ? (HttpStatusCode.OK, """{"value":[{"id":"dev-9","complianceState":"compliant","operatingSystem":"macOS","lastSyncDateTime":"2026-09-03T12:00:00Z","isEncrypted":true}]}""")
                    : (HttpStatusCode.OK, """
                      {"value":[{"id":"dev-1","complianceState":"compliant","operatingSystem":"Windows","lastSyncDateTime":"2026-09-03T12:00:00Z","isEncrypted":true}],
                       "@odata.nextLink":"https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$skiptoken=abc"}
                      """);
            if (url.Contains("deviceCompliancePolicies")) return (HttpStatusCode.OK, EmptyPageJson);
            if (url.Contains("deviceConfigurations")) return (HttpStatusCode.OK, EmptyPageJson);
            return (HttpStatusCode.NotFound, "{}");
        });

        var snapshot = await NewConnector(handler).CollectDevicePostureAsync(Config(), CancellationToken.None);

        snapshot.Devices.State.Should().Be(DevicePostureDimensionState.Available);
        snapshot.Devices.TotalDevices.Should().Be(2, "as DUAS páginas foram agregadas");
        calls.Should().Contain(u => u.Contains("skiptoken"), "o nextLink oficial foi seguido");
    }

    [Fact]
    public async Task Transport_RejectsNextLink_PointingToForeignHost_WithoutLeakingBearer()
    {
        var authHeadersByHost = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var handler = new StubHandler(req =>
        {
            var host = req.RequestUri!.Host;
            authHeadersByHost[host] = req.Headers.Authorization is not null;
            var url = req.RequestUri.ToString();
            if (url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
            if (url.Contains("managedDevices"))
                return (HttpStatusCode.OK, """
                  {"value":[{"id":"dev-1","complianceState":"compliant","operatingSystem":"Windows","lastSyncDateTime":"2026-09-03T12:00:00Z"}],
                   "@odata.nextLink":"https://evil.example.com/v1.0/deviceManagement/managedDevices?$skiptoken=abc"}
                  """);
            return (HttpStatusCode.OK, EmptyPageJson);
        });

        var snapshot = await NewConnector(handler).CollectDevicePostureAsync(Config(), CancellationToken.None);

        // A allowlist barra o host forjado ANTES da requisição: o bearer nunca chega lá.
        authHeadersByHost.Keys.Should().NotContain("evil.example.com");
        // O que já foi lido NÃO vira vazio: a dimensão degrada para piso.
        snapshot.Devices.State.Should().Be(DevicePostureDimensionState.Partial);
        snapshot.Devices.TotalDevices.Should().Be(1);
    }

    [Fact]
    public async Task Transport_MidPageFailure_PreservesWhatWasRead_AndNeverBecomesEmpty()
    {
        var page = 0;
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
            if (url.Contains("managedDevices"))
            {
                page++;
                return page == 1
                    ? (HttpStatusCode.OK, """
                       {"value":[
                         {"id":"d1","complianceState":"compliant","operatingSystem":"Windows","lastSyncDateTime":"2026-09-03T12:00:00Z"},
                         {"id":"d2","complianceState":"noncompliant","operatingSystem":"Windows","lastSyncDateTime":"2026-09-03T12:00:00Z"}],
                        "@odata.nextLink":"https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$skiptoken=p2"}
                       """)
                    : (HttpStatusCode.InternalServerError, """{"error":{"code":"internalServerError"}}""");
            }
            return (HttpStatusCode.OK, EmptyPageJson);
        });

        var snapshot = await NewConnector(handler).CollectDevicePostureAsync(Config(), CancellationToken.None);

        snapshot.Devices.State.Should().Be(DevicePostureDimensionState.Partial,
            "uma falha em página intermediária torna os agregados um PISO");
        snapshot.Devices.TotalDevices.Should().Be(2, "os dispositivos já lidos NÃO são descartados");
        snapshot.Devices.Groups.Should().NotBeEmpty("a falha nunca converte o resultado em coleção vazia");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, nameof(DevicePostureDimensionState.Unavailable))]
    [InlineData(HttpStatusCode.Forbidden, nameof(DevicePostureDimensionState.NotAuthorized))]
    [InlineData(HttpStatusCode.NotFound, nameof(DevicePostureDimensionState.NotLicensed))]
    [InlineData(HttpStatusCode.TooManyRequests, nameof(DevicePostureDimensionState.Unavailable))]
    [InlineData(HttpStatusCode.InternalServerError, nameof(DevicePostureDimensionState.Unavailable))]
    [InlineData(HttpStatusCode.ServiceUnavailable, nameof(DevicePostureDimensionState.Unavailable))]
    public async Task Transport_ClassifiesEveryFailure_AsTypedState_NeverAsZero(HttpStatusCode status, string expected)
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
            if (url.Contains("managedDevices")) return (status, """{"error":{"code":"failure"}}""");
            return (HttpStatusCode.OK, EmptyPageJson);
        });

        var snapshot = await NewConnector(handler).CollectDevicePostureAsync(Config(), CancellationToken.None);

        snapshot.Devices.State.ToString().Should().Be(expected);
        snapshot.Devices.HasInventory.Should().BeFalse("nenhuma falha produz inventário");
        snapshot.Devices.Groups.Should().BeEmpty();
        // A dimensão de configuração NÃO foi contaminada pela falha da outra.
        snapshot.Configuration.State.Should().Be(DevicePostureDimensionState.Available);
    }

    [Fact]
    public async Task Transport_LicenseErrorCode_IsClassifiedAsNotLicensed_NotAsPermission()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
            if (url.Contains("managedDevices"))
                return (HttpStatusCode.Forbidden, """{"error":{"code":"MissingIntuneLicense"}}""");
            return (HttpStatusCode.OK, EmptyPageJson);
        });

        var snapshot = await NewConnector(handler).CollectDevicePostureAsync(Config(), CancellationToken.None);

        snapshot.Devices.State.Should().Be(DevicePostureDimensionState.NotLicensed,
            "licença ausente é uma lacuna diferente de permissão negada — a ação recomendada não é a mesma");
    }

    [Fact]
    public async Task Transport_HonoursCancellation_ByPropagatingOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
            cts.Cancel();   // cancelamento SOLICITADO no meio da coleta
            return (HttpStatusCode.OK, EmptyPageJson);
        });

        var act = async () => await NewConnector(handler).CollectDevicePostureAsync(Config(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "o cancelamento solicitado é a ÚNICA condição que propaga — nunca vira estado de dado");
    }

    [Fact]
    public async Task Transport_AuthFailure_DegradesBothDimensions_WithoutInventing()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.Unauthorized, """{"error":{"code":"invalid_client"}}"""));

        var snapshot = await NewConnector(handler).CollectDevicePostureAsync(Config(), CancellationToken.None);

        snapshot.Configuration.State.Should().Be(DevicePostureDimensionState.Unavailable);
        snapshot.Devices.State.Should().Be(DevicePostureDimensionState.Unavailable);
        snapshot.IsComplete.Should().BeFalse();
        snapshot.HasAnyInventory.Should().BeFalse();
    }

    // ================= COLETOR =====================================================================

    [Fact]
    public async Task Collect_HappyPath_ProducesBothDimensions_WithHonestAggregates()
    {
        var snapshot = await NewConnector(HappyHandler()).CollectDevicePostureAsync(Config(), CancellationToken.None);

        snapshot.Source.Should().Be("Microsoft Intune");
        snapshot.IsComplete.Should().BeTrue();

        // Dimensão 1: as DUAS famílias, com plataforma derivada do @odata.type oficial.
        snapshot.Configuration.State.Should().Be(DevicePostureDimensionState.Available);
        snapshot.Configuration.Policies.Should().HaveCount(3);
        snapshot.Configuration.Policies.Count(p => p.Kind == DevicePolicyKind.CompliancePolicy).Should().Be(2);
        snapshot.Configuration.Policies.Count(p => p.Kind == DevicePolicyKind.DeviceConfiguration).Should().Be(1);
        snapshot.Configuration.Policies.Single(p => p.ExternalId == "pol-1").PlatformLabel.Should().Be("Windows");
        snapshot.Configuration.Policies.Single(p => p.ExternalId == "pol-2").PlatformLabel.Should().Be("iOS");

        // Atribuição: coleção presente ⇒ afirmação objetiva (com alvo = atribuída; vazia = não atribuída).
        snapshot.Configuration.AssignmentState.Should().Be(DevicePostureDimensionState.Available);
        snapshot.Configuration.Policies.Single(p => p.ExternalId == "pol-1").AssignmentState
            .Should().Be(DevicePolicyAssignmentState.Assigned);
        snapshot.Configuration.Policies.Single(p => p.ExternalId == "pol-2").AssignmentState
            .Should().Be(DevicePolicyAssignmentState.Unassigned);
        snapshot.Configuration.Policies.Single(p => p.ExternalId == "cfg-1").AssignmentCount.Should().Be(2);

        // Dimensão 2: agregados por recorte, com a atividade medida contra a janela oficial.
        snapshot.Devices.State.Should().Be(DevicePostureDimensionState.Available);
        snapshot.Devices.TotalDevices.Should().Be(4);
        snapshot.Devices.DevicesWithDirectoryId.Should().Be(2, "só dois dispositivos trouxeram o id de diretório");
        Sum(snapshot, DeviceComplianceBucket.Compliant).Should().Be(1);
        Sum(snapshot, DeviceComplianceBucket.Noncompliant).Should().Be(1);
        Sum(snapshot, DeviceComplianceBucket.InGracePeriod).Should().Be(1);
        Sum(snapshot, DeviceComplianceBucket.Unknown).Should().Be(1);
        snapshot.Devices.Groups.Where(g => g.Activity == DeviceActivityBucket.Stale).Sum(g => g.DeviceCount)
            .Should().Be(1, "o dispositivo iOS sincronizou pela última vez há mais de 30 dias");
        snapshot.Devices.Groups.Where(g => g.Activity == DeviceActivityBucket.Unknown).Sum(g => g.DeviceCount)
            .Should().Be(1, "sem lastSyncDateTime a atividade é DESCONHECIDA, nunca 'ativo'");
        snapshot.Devices.Groups.Sum(g => g.DeviceCount).Should().Be(snapshot.Devices.TotalDevices,
            "o total é a soma dos grupos — nunca um número paralelo");
    }

    [Fact]
    public async Task Collect_WithoutManagedDevicesPermission_KeepsPoliciesUsable_AndBlocksOnlyDevices()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
            if (url.Contains("deviceCompliancePolicies")) return (HttpStatusCode.OK, CompliancePoliciesJson);
            if (url.Contains("deviceConfigurations")) return (HttpStatusCode.OK, DeviceConfigurationsJson);
            // O tenant tem DeviceManagementConfiguration.Read.All, mas NÃO DeviceManagementManagedDevices.Read.All.
            if (url.Contains("managedDevices"))
                return (HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied"}}""");
            return (HttpStatusCode.NotFound, "{}");
        });

        var snapshot = await NewConnector(handler).CollectDevicePostureAsync(Config(), CancellationToken.None);

        snapshot.Configuration.State.Should().Be(DevicePostureDimensionState.Available,
            "a dimensão de políticas permanece plenamente utilizável");
        snapshot.Configuration.Policies.Should().HaveCount(3);

        snapshot.Devices.State.Should().Be(DevicePostureDimensionState.NotAuthorized);
        snapshot.Devices.TotalDevices.Should().Be(0, "o campo bruto é zero, mas o ESTADO é o que a leitura usa");
        snapshot.Devices.HasInventory.Should().BeFalse("sem inventário a tela nunca pode exibir '0 não conformes'");
        snapshot.Devices.Detail.Should().Contain("DeviceManagementManagedDevices.Read.All",
            "a ação objetiva nomeia a permissão que falta");

        snapshot.IsComplete.Should().BeFalse("uma só dimensão validada nunca é 'plenamente operacional'");
        snapshot.HasAnyInventory.Should().BeTrue("mas a coleta ainda entregou valor real");
    }

    [Fact]
    public async Task Collect_RealEmptyCollections_AreAvailableZero_NotFailure()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.ToString().Contains("/oauth2/v2.0/token")
                ? (HttpStatusCode.OK, TokenJson)
                : (HttpStatusCode.OK, EmptyPageJson));

        var snapshot = await NewConnector(handler).CollectDevicePostureAsync(Config(), CancellationToken.None);

        snapshot.Configuration.State.Should().Be(DevicePostureDimensionState.Available);
        snapshot.Configuration.Policies.Should().BeEmpty();
        snapshot.Devices.State.Should().Be(DevicePostureDimensionState.Available);
        snapshot.Devices.TotalDevices.Should().Be(0);
        snapshot.Devices.HasInventory.Should().BeTrue(
            "zero REAL é um fato coletado — distinto de 'não coletado'");
        snapshot.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Collect_WhenExpandIsRejected_ReadsPoliciesAnyway_AndMarksAssignmentUnknown()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
            // A expansão não é contrato v1.0 garantido: a fonte pode recusá-la com 400.
            if (url.Contains("$expand=assignments"))
                return (HttpStatusCode.BadRequest, """{"error":{"code":"BadRequest"}}""");
            if (url.Contains("deviceCompliancePolicies"))
                return (HttpStatusCode.OK, """{"value":[{"@odata.type":"#microsoft.graph.windows10CompliancePolicy","id":"pol-1","displayName":"P1"}]}""");
            if (url.Contains("deviceConfigurations")) return (HttpStatusCode.OK, EmptyPageJson);
            if (url.Contains("managedDevices")) return (HttpStatusCode.OK, EmptyPageJson);
            return (HttpStatusCode.NotFound, "{}");
        });

        var snapshot = await NewConnector(handler).CollectDevicePostureAsync(Config(), CancellationToken.None);

        snapshot.Configuration.Policies.Should().HaveCount(1, "as políticas continuam sendo lidas sem a expansão");
        snapshot.Configuration.Policies.Single().AssignmentState
            .Should().Be(DevicePolicyAssignmentState.Unknown,
                "ausência da coleção de atribuições é DESCONHECIDO — jamais 'sem atribuição'");
        snapshot.Configuration.AssignmentState.Should().Be(DevicePostureDimensionState.Unavailable,
            "a sub-dimensão de atribuição degrada sozinha, sem derrubar as políticas");
    }

    [Fact]
    public async Task Collect_UnknownComplianceValueFromMicrosoft_DoesNotBreak_AndIsNeverCompliant()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
            if (url.Contains("managedDevices"))
                return (HttpStatusCode.OK, """
                  {"value":[
                    {"id":"d1","complianceState":"algumEstadoNovoDaMicrosoft","operatingSystem":"Windows","lastSyncDateTime":"2026-09-03T12:00:00Z"},
                    {"id":"d2","operatingSystem":"Windows","lastSyncDateTime":"2026-09-03T12:00:00Z"}]}
                  """);
            return (HttpStatusCode.OK, EmptyPageJson);
        });

        var snapshot = await NewConnector(handler).CollectDevicePostureAsync(Config(), CancellationToken.None);

        snapshot.Devices.State.Should().Be(DevicePostureDimensionState.Available, "um enum novo não quebra o coletor");
        snapshot.Devices.TotalDevices.Should().Be(2);
        Sum(snapshot, DeviceComplianceBucket.Unknown).Should().Be(2,
            "estado não reconhecido e estado ausente caem em 'não avaliado'");
        Sum(snapshot, DeviceComplianceBucket.Compliant).Should().Be(0,
            "desconhecimento NUNCA é promovido a conformidade");
    }

    [Fact]
    public async Task Collect_NullAndMalformedRecords_AreCountedAsInvalid_NotSilentlyDropped()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
            if (url.Contains("deviceCompliancePolicies"))
                return (HttpStatusCode.OK, """{"value":[{"id":"ok-1","displayName":"P"},{"displayName":"sem id"},null]}""");
            if (url.Contains("deviceConfigurations")) return (HttpStatusCode.OK, EmptyPageJson);
            if (url.Contains("managedDevices"))
                return (HttpStatusCode.OK, """{"value":[{"id":"d1","complianceState":null,"operatingSystem":null,"isEncrypted":null,"lastSyncDateTime":null},{"noId":true}]}""");
            return (HttpStatusCode.NotFound, "{}");
        });

        var snapshot = await NewConnector(handler).CollectDevicePostureAsync(Config(), CancellationToken.None);

        snapshot.Configuration.InvalidPolicies.Should().Be(2, "registro sem id e registro nulo são inválidos");
        snapshot.Configuration.State.Should().Be(DevicePostureDimensionState.Partial,
            "registros inválidos tornam a dimensão um piso — não um total confiável");

        snapshot.Devices.InvalidDevices.Should().Be(1);
        snapshot.Devices.TotalDevices.Should().Be(1);
        var only = snapshot.Devices.Groups.Single();
        only.Compliance.Should().Be(DeviceComplianceBucket.Unknown);
        only.Encryption.Should().Be(DeviceEncryptionBucket.Unknown, "null NÃO vira 'sem criptografia'");
        only.Activity.Should().Be(DeviceActivityBucket.Unknown, "null NÃO vira 'obsoleto' nem 'ativo'");
        only.OperatingSystem.Should().Be("Não informado");
    }

    [Fact]
    public async Task Collect_NeverCarriesPiiOrPolicyPayload_IntoTheSnapshot()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/oauth2/v2.0/token")) return (HttpStatusCode.OK, TokenJson);
            if (url.Contains("deviceCompliancePolicies")) return (HttpStatusCode.OK, CompliancePoliciesJson);
            if (url.Contains("deviceConfigurations")) return (HttpStatusCode.OK, EmptyPageJson);
            if (url.Contains("managedDevices"))
                // A fonte devolve PII mesmo que o $select peça só o mínimo — o parser não pode aproveitá-la.
                return (HttpStatusCode.OK, """
                  {"value":[{"id":"d1","complianceState":"compliant","operatingSystem":"Windows",
                    "lastSyncDateTime":"2026-09-03T12:00:00Z","isEncrypted":true,
                    "userPrincipalName":"alice@demo.example.com","userDisplayName":"Alice",
                    "emailAddress":"alice@demo.example.com","serialNumber":"SN-12345","imei":"IMEI-9",
                    "phoneNumber":"+550000000000","wiFiMacAddress":"AA:BB:CC:DD:EE:FF","deviceName":"NOTE-ALICE"}]}
                  """);
            return (HttpStatusCode.NotFound, "{}");
        });

        var snapshot = await NewConnector(handler).CollectDevicePostureAsync(Config(), CancellationToken.None);

        var serialized = System.Text.Json.JsonSerializer.Serialize(snapshot);
        foreach (var forbidden in new[]
                 {
                     "alice", "Alice", "SN-12345", "IMEI-9", "+550000000000", "AA:BB:CC:DD:EE:FF",
                     "NOTE-ALICE", "aad-1", "d1", "NAO DEVE SER PERSISTIDO", ClientSecret, "fake-access-token",
                 })
            serialized.Should().NotContain(forbidden, $"a fotografia nunca carrega '{forbidden}'");
    }

    [Fact]
    public async Task Collect_IsIdempotent_TwoIdenticalCollectionsProduceIdenticalFacts()
    {
        var connector = NewConnector(HappyHandler());
        var config = Config();

        var first = await connector.CollectDevicePostureAsync(config, CancellationToken.None);
        var second = await connector.CollectDevicePostureAsync(config, CancellationToken.None);

        first.Configuration.Policies.Should().BeEquivalentTo(second.Configuration.Policies);
        first.Devices.Groups.Should().BeEquivalentTo(second.Devices.Groups);
        first.Devices.TotalDevices.Should().Be(second.Devices.TotalDevices);
    }

    // ================= FRONTEIRA DE AUTORIDADE =====================================================

    [Fact]
    public async Task Connector_NeverEmitsAnyEvidenceSignal()
    {
        var connector = NewConnector(HappyHandler());
        var signals = new List<EvidenceSignal>();
        await foreach (var s in connector.CollectAsync(Config(), CancellationToken.None))
            signals.Add(s);

        signals.Should().BeEmpty(
            "postura de dispositivos é fato operacional consultivo — presença de política jamais gera pontos");
    }

    [Fact]
    public void Connector_IsRegisteredUnder_MicrosoftConfigAnalyzer()
    {
        var connector = NewConnector(HappyHandler());
        connector.Provider.Should().Be(ConnectorProvider.Microsoft);
        connector.Capability.Should().Be(ConnectorCapability.ConfigAnalyzer);
    }

    [Fact]
    public async Task Test_WithOnlyConfigurationProven_NeverReportsFullyHealthy()
    {
        var health = await NewConnector(HappyHandler()).TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Degraded,
            "uma dimensão validada não autoriza declarar o conector plenamente operacional");
        health.Message.Should().Contain("DeviceManagementManagedDevices.Read.All");
    }

    [Fact]
    public async Task Test_WithoutReadableCredentials_IsDegraded_WithoutTouchingTheNetwork()
    {
        var called = false;
        var handler = new StubHandler(_ => { called = true; return (HttpStatusCode.OK, TokenJson); });
        var config = Config();
        config.EncryptedSettings = "";

        var health = await NewConnector(handler).TestAsync(config, CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Degraded);
        called.Should().BeFalse("sem credencial legível não se fala com a fonte (fail-closed)");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static int Sum(AegisScore.Application.Abstractions.DevicePostureSnapshot s, DeviceComplianceBucket bucket) =>
        s.Devices.Groups.Where(g => g.Compliance == bucket).Sum(g => g.DeviceCount);

    private sealed class PassthroughProtector : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    /// <summary>HttpMessageHandler simulado: roteia por requisição (mesmo idioma dos testes do KNIGHT).</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _route;
        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> route) => _route = route;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var (status, body) = _route(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
