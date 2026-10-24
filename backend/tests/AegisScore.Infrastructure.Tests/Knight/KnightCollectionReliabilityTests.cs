using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Connectors.Microsoft;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Services;
using AegisScore.Domain;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Regressões dos defeitos de TRANSPORTE da coleta do Entra ID, pelo caminho do produto: o
/// coletor resolvido do DI real (<c>AddMicrosoftConnectors</c>), com o pipeline de resiliência configurado lá. Só o manipulador
/// primário do <see cref="HttpClient"/> é substituído (respostas no formato documentado do Graph, sem rede).
///
/// O defeito reproduzido: o pipeline padrão lança <see cref="TimeoutRejectedException"/> (tempo da tentativa/total) e
/// <see cref="BrokenCircuitException"/> (circuito aberto), e nenhum dos dois era reconhecido — viravam "erro inesperado nesta
/// coleta". A leitura N+1 de MFA por usuário, limitada ou falhando, abria o circuito do cliente do Graph e TODAS as leituras
/// seguintes (as beta, o tempo limite de sessão, os dois riscos de identidade) falhavam em sequência com a mesma mensagem — o
/// padrão observado no assessment de 24/10. Os tempos são encurtados pela MESMA seção de configuração do ambiente
/// (<c>Knight:Http</c>) que a produção usa; a lógica do pipeline é a de produção.
/// </summary>
public sealed class KnightCollectionReliabilityTests
{
    private static readonly KnightEntraIdConfiguration Cfg = new(
        AzureTenantId: "11111111-2222-3333-4444-555555555555", ClientId: "app-client-id", ClientSecret: "SEGREDO-SINTETICO-NAO-USAR");

    private static readonly Guid Tenant = Guid.Parse("aa000000-0000-0000-0000-0000000000c1");

    /// <summary>Tempos curtos, mesmos parâmetros (e o mesmo validador) da configuração de produção.</summary>
    private static Dictionary<string, string?> FastHttp(int breakSeconds = 30, string failureRatio = "0.5") => new()
    {
        ["AttemptTimeout"] = "00:00:00.200",
        ["TotalRequestTimeout"] = "00:00:00.700",
        ["MaxRetryAttempts"] = "1",
        ["RetryBaseDelay"] = "00:00:00.010",
        ["CircuitSamplingDuration"] = "00:00:30",
        ["CircuitMinimumThroughput"] = "4",
        ["CircuitFailureRatio"] = failureRatio,
        ["CircuitBreakDuration"] = $"00:00:{breakSeconds:00}",
    };

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>?> _special;
        private readonly EntraConfigurationScenario _scenario = new(EntraConfigurationScenario.Variant.Compliant);
        public int Calls;

        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>?> special) => _special = special;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (_special(request, cancellationToken) is { } special) return await special;
            var (code, body) = _scenario.Respond(request);
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static (ServiceProvider Provider, IKnightCollector Collector) Build(HttpMessageHandler handler, Dictionary<string, string?> http)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(http).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMicrosoftConnectors(http: config);
        // Só o manipulador PRIMÁRIO é trocado: o pipeline de resiliência registrado pelo produto continua na frente dele.
        services.AddHttpClient<IEntraGraphClient, EntraGraphClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        // Sem ValidateOnBuild: o teste de conexão do mesmo pacote depende da Infrastructure, que não entra aqui.
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetServices<IKnightCollector>().Single(c => c.Source == KnightSourceType.MicrosoftEntraId);
        return (provider, collector);
    }

    private static Task<HttpResponseMessage> Status(HttpStatusCode code, string? retryAfterSeconds = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent("""{"error":{"code":"x","message":"y"}}""", Encoding.UTF8, "application/json") };
        if (retryAfterSeconds is not null) r.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds);
        return Task.FromResult(r);
    }

    private static async Task<HttpResponseMessage> Hang(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        throw new InvalidOperationException("inalcançável");
    }

    private static KnightCapabilityStatus Cap(KnightCollectionResult r, KnightCapability c) => r.Capabilities.Single(x => x.Capability == c);

    private static void NoGenericUnexpected(KnightCollectionResult r) =>
        r.Capabilities.Where(c => c.Detail is not null)
            .Should().NotContain(c => c.Detail!.Contains("inesperado", StringComparison.OrdinalIgnoreCase),
                "nenhuma falha de transporte pode virar \"erro inesperado\" atribuído à fonte");

    [Fact]
    public void Configuracao_DoAmbiente_ChegaAoPipeline_EOTempoNativoDoHttpClientFicaDesligado()
    {
        var (provider, _) = Build(new ScriptedHandler((_, _) => null), new Dictionary<string, string?>());
        using var _p = provider;
        var options = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>().Get("IEntraGraphClient-standard");

        // Padrões documentados em KnightHttpOptions: 30 s por tentativa, 120 s no total, 3 novas tentativas.
        options.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        options.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(120));
        options.Retry.MaxRetryAttempts.Should().Be(3);
        options.CircuitBreaker.SamplingDuration.Should().BeGreaterThanOrEqualTo(options.AttemptTimeout.Timeout * 2);

        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("IEntraGraphClient");
        http.Timeout.Should().Be(Timeout.InfiniteTimeSpan, "o Polly é a única autoridade de tempo — o tempo nativo cortava as novas tentativas");
    }

    [Fact]
    public void Circuito_NaoContaLimiteDeTaxa_SoFalhaDoServico()
    {
        KnightHttpResilience.CountsForCircuit(Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)))
            .Should().BeFalse("429 diz \"espere\", não \"o serviço caiu\" — é atendido pelo Retry-After");
        KnightHttpResilience.CountsForCircuit(Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))).Should().BeTrue();
        KnightHttpResilience.CountsForCircuit(Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))).Should().BeFalse();
        KnightHttpResilience.CountsForCircuit(Outcome.FromException<HttpResponseMessage>(new HttpRequestException("rede"))).Should().BeTrue();
    }

    /// <summary>Tempo esgotado no pipeline: a leitura vira indisponibilidade por TEMPO, com diagnóstico — e as outras seguem.</summary>
    [Fact]
    public async Task TempoEsgotadoNoPipeline_ViraIndisponibilidadePorTempo_ComDiagnostico_ESemDerrubarAsOutras()
    {
        var handler = new ScriptedHandler((req, ct) => req.RequestUri!.AbsoluteUri.Contains("/beta/admin/forms") ? Hang(ct) : null);
        var (provider, collector) = Build(handler, FastHttp());
        using var _p = provider;

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        var forms = Cap(result, KnightCapability.M365FormsSettings);
        forms.Outcome.Should().Be(KnightCapabilityOutcome.Unavailable);
        forms.Diagnostic!.Kind.Should().Be(KnightFailureKind.Timeout);
        forms.Diagnostic.Code.Should().Be("TimeoutRejected");
        forms.Diagnostic.CorrelationId.Should().NotBeNullOrWhiteSpace();
        forms.Diagnostic.DurationMs.Should().BeGreaterThan(0);
        forms.Detail.Should().Contain("tempo esgotado");

        // As leituras antes e depois dela continuam coletadas: uma falha não apaga as outras.
        Cap(result, KnightCapability.M365AppsAndServicesSettings).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        Cap(result, KnightCapability.ActivityBasedTimeoutPolicy).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        Cap(result, KnightCapability.PrivilegedRoleInventory).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        result.State.Should().Be(KnightSourceState.PartialCollection);
        NoGenericUnexpected(result);
    }

    /// <summary>
    /// O padrão de 24/10: falhas do serviço na leitura N+1 de MFA por usuário abrem o circuito do cliente do Graph, e as leituras
    /// seguintes encontram o circuito aberto. Agora cada uma diz isso (CircuitOpen), em vez de "erro inesperado".
    /// </summary>
    [Fact]
    public async Task CircuitoAbertoPorFalhasDoServico_AsLeiturasSeguintesDizemCircuitoAberto_NuncaErroInesperado()
    {
        var handler = new ScriptedHandler((req, _) =>
            req.RequestUri!.AbsoluteUri.EndsWith("/authentication/requirements", StringComparison.Ordinal)
                ? Status(HttpStatusCode.ServiceUnavailable)
                : null);
        // Proporção baixa (como a do padrão da fachada): as leituras bem-sucedidas anteriores não diluem as falhas a ponto de o
        // circuito não abrir.
        var (provider, collector) = Build(handler, FastHttp(breakSeconds: 30, failureRatio: "0.1"));
        using var _p = provider;

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        foreach (var c in new[]
                 {
                     KnightCapability.AuthenticationMethodsPolicyPreview, KnightCapability.M365AppsAndServicesSettings,
                     KnightCapability.M365FormsSettings, KnightCapability.ActivityBasedTimeoutPolicy,
                 })
        {
            var cap = Cap(result, c);
            cap.Outcome.Should().Be(KnightCapabilityOutcome.Unavailable, c.ToString());
            cap.Diagnostic!.Kind.Should().Be(KnightFailureKind.CircuitOpen, c.ToString());
            cap.Detail.Should().Contain("circuito");
        }
        // Os riscos de identidade (lidos por último) também dizem a causa certa.
        Cap(result, KnightCapability.IdentityRiskyUsers).Detail.Should().Contain("circuito");
        Cap(result, KnightCapability.IdentityRiskDetections).Diagnostic!.Kind.Should().Be(KnightFailureKind.CircuitOpen);
        // E o que veio ANTES do circuito abrir continua coletado.
        Cap(result, KnightCapability.PrivilegedRoleInventory).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        Cap(result, KnightCapability.ConditionalAccessPolicies).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        NoGenericUnexpected(result);
    }

    /// <summary>Limite de taxa na leitura N+1 não abre o circuito: as leituras seguintes do mesmo cliente são coletadas.</summary>
    [Fact]
    public async Task LimiteDeTaxaNaLeituraPorUsuario_NaoAbreOCircuito_AsSeguintesSaoColetadas()
    {
        var handler = new ScriptedHandler((req, _) =>
            req.RequestUri!.AbsoluteUri.EndsWith("/authentication/requirements", StringComparison.Ordinal)
                ? Status(HttpStatusCode.TooManyRequests, "0")
                : null);
        var (provider, collector) = Build(handler, FastHttp());
        using var _p = provider;

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        Cap(result, KnightCapability.AuthenticationMethodsPolicyPreview).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        Cap(result, KnightCapability.M365FormsSettings).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        Cap(result, KnightCapability.ActivityBasedTimeoutPolicy).Outcome.Should().Be(KnightCapabilityOutcome.Collected);

        // A leitura por usuário fica PARCIAL e declarada: nenhuma conta lida, todas como não lidas — a regra não aprova.
        var mfa = Cap(result, KnightCapability.PerUserMfaStates);
        mfa.Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        mfa.Detail.Should().Contain("Leitura parcial");
        mfa.Diagnostic!.Kind.Should().Be(KnightFailureKind.Incomplete);
        var inventory = result.TenantConfiguration!.Read<EntraPerUserMfaInventory>().Items.Single();
        inventory.Read.Should().Be(0);
        inventory.Unread.Should().Be(EntraConfigurationScenario.EnabledUserIds.Length);
        inventory.Complete.Should().BeFalse();
        NoGenericUnexpected(result);
    }

    /// <summary>Falha sistêmica no MEIO da leitura por usuário: o que foi lido é preservado e a parcialidade fica explícita.</summary>
    [Fact]
    public async Task FalhaDepoisDeLeituraParcialPorUsuario_PreservaAsContasLidas_EDeclaraAInterrupcao()
    {
        var served = 0;
        var handler = new ScriptedHandler((req, _) =>
        {
            if (!req.RequestUri!.AbsoluteUri.EndsWith("/authentication/requirements", StringComparison.Ordinal)) return null;
            // As duas primeiras contas respondem; depois o transporte cai.
            return Interlocked.Increment(ref served) <= 2 ? null : throw new HttpRequestException("conexão recusada (sintético)");
        });
        var (provider, collector) = Build(handler, FastHttp());
        using var _p = provider;

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg));

        var mfa = Cap(result, KnightCapability.PerUserMfaStates);
        mfa.Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        mfa.Detail.Should().Contain("Leitura interrompida").And.Contain("preservadas");
        mfa.Diagnostic!.Kind.Should().Be(KnightFailureKind.Incomplete);
        var inventory = result.TenantConfiguration!.Read<EntraPerUserMfaInventory>().Items.Single();
        inventory.Read.Should().BeGreaterThanOrEqualTo(1);
        inventory.Complete.Should().BeFalse("enumeração interrompida nunca é evidência de ausência");
        inventory.Limitation.Should().Contain("não lida");
    }

    /// <summary>Cancelamento pedido pelo usuário propaga — não vira estado de capacidade nem coleta "parcial".</summary>
    [Fact]
    public async Task CancelamentoDoUsuario_Propaga_SemVirarFalhaDeCapacidade()
    {
        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHandler((req, ct) =>
        {
            if (!req.RequestUri!.AbsoluteUri.Contains("/beta/admin/forms")) return null;
            cts.Cancel();
            return Hang(ct);
        });
        var (provider, collector) = Build(handler, FastHttp());
        using var _p = provider;

        await FluentActions.Awaiting(() => collector.CollectAsync(new KnightCollectionContext(Tenant, Cfg), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Classificação ÚNICA: cada exceção com o próprio tipo técnico e nenhum segredo no motivo.</summary>
    [Theory]
    [InlineData("timeout", KnightCapabilityOutcome.Unavailable, KnightFailureKind.Timeout)]
    [InlineData("circuit", KnightCapabilityOutcome.Unavailable, KnightFailureKind.CircuitOpen)]
    [InlineData("isolated", KnightCapabilityOutcome.Unavailable, KnightFailureKind.CircuitOpen)]
    [InlineData("http", KnightCapabilityOutcome.Unavailable, KnightFailureKind.Transport)]
    [InlineData("taskcanceled", KnightCapabilityOutcome.Unavailable, KnightFailureKind.Timeout)]
    [InlineData("json", KnightCapabilityOutcome.Error, KnightFailureKind.Contract)]
    [InlineData("internal", KnightCapabilityOutcome.Error, KnightFailureKind.Internal)]
    public void Classificador_SeparaTempoCircuitoTransporteContratoEDefeitoInterno(string kind, KnightCapabilityOutcome outcome, KnightFailureKind failure)
    {
        const string secret = "Bearer eyJ-SEGREDO-NA-MENSAGEM";
        Exception ex = kind switch
        {
            "timeout" => new TimeoutRejectedException(secret),
            "circuit" => new BrokenCircuitException(secret),
            "isolated" => new IsolatedCircuitException(secret),
            "http" => new HttpRequestException(secret),
            "taskcanceled" => new TaskCanceledException(secret),
            "json" => new JsonException(secret),
            _ => new InvalidOperationException(secret),
        };

        var f = KnightFailureClassifier.Classify(ex, "Microsoft Graph", "read", 12, "corr-1");

        f.Outcome.Should().Be(outcome);
        f.Diagnostic.Kind.Should().Be(failure);
        f.Diagnostic.CorrelationId.Should().Be("corr-1");
        f.Reason.Should().NotContain("SEGREDO").And.NotContain("Bearer", "a mensagem da exceção nunca entra no motivo");
        if (failure == KnightFailureKind.Internal)
            f.Reason.Should().Contain("Não é atribuída ao provedor");
        KnightFailureClassifier.IsCallerCancellation(ex, CancellationToken.None).Should().BeFalse();
    }

    [Fact]
    public void Classificador_PreservaHttp_LicencaSoComCodigo_EAutorizacaoSemInventarCausa()
    {
        var license = KnightFailureClassifier.FromHttp(new EntraGraphException(EntraGraphErrorKind.Unavailable, "x", 400,
            "AadPremiumLicenseRequired", "/v1.0/roleManagement/directory/roleAssignmentSchedules"), "Microsoft Graph", "read");
        license.Outcome.Should().Be(KnightCapabilityOutcome.LimitedByLicense);
        license.Diagnostic.HttpStatus.Should().Be(400);

        var fabric = KnightFailureClassifier.FromHttp(new EntraGraphException(EntraGraphErrorKind.InsufficientPermission, "x", 403,
            "InsufficientScopes", "/v1/admin/tenantsettings", "6f1a2b3c-0000-4000-8000-000000000001"), "Microsoft Fabric", "read");
        fabric.Outcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        fabric.Reason.Should().Contain("HTTP 403").And.Contain("InsufficientScopes");
        fabric.Diagnostic.ServiceRequestId.Should().Be("6f1a2b3c-0000-4000-8000-000000000001");

        var throttled = KnightFailureClassifier.FromHttp(new EntraGraphException(EntraGraphErrorKind.Throttled, "x", 429), "Microsoft Graph", "read");
        throttled.Outcome.Should().Be(KnightCapabilityOutcome.Throttled);
        var auth = KnightFailureClassifier.FromHttp(new EntraGraphException(EntraGraphErrorKind.AuthFailure, "x", 401), "Microsoft Graph", "read");
        auth.Outcome.Should().Be(KnightCapabilityOutcome.AuthenticationFailure);
        var down = KnightFailureClassifier.FromHttp(new EntraGraphException(EntraGraphErrorKind.Unavailable, "x", 503), "Microsoft Graph", "read");
        down.Outcome.Should().Be(KnightCapabilityOutcome.Unavailable);
        down.Diagnostic.Kind.Should().Be(KnightFailureKind.HttpStatus);
    }

    [Fact]
    public void IdentificadorDeRequisicao_SoGuid_EDiagnosticoNuloNaoEntraNoJson()
    {
        EntraGraphException.SanitizeRequestId("abc; Bearer x").Should().BeNull();
        EntraGraphException.SanitizeRequestId(" 6F1A2B3C-0000-4000-8000-000000000001 ").Should().Be("6f1a2b3c-0000-4000-8000-000000000001");

        var json = JsonSerializer.Serialize(new[] { new KnightCapabilityStatus(KnightCapability.GuestAccounts, KnightCapabilityOutcome.Collected) },
            KnightCapabilitiesJson.Options);
        json.Should().NotContain("diagnostic", "capacidade coletada não carrega diagnóstico — e o JSON das coletas antigas não muda");

        var failed = new KnightCapabilityStatus(KnightCapability.GuestAccounts, KnightCapabilityOutcome.Unavailable, "x",
            new KnightCapabilityDiagnostic("read", KnightFailureKind.Timeout, 30_000, CorrelationId: "c1"));
        var back = KnightCapabilitiesJson.Deserialize(JsonSerializer.Serialize(new[] { failed }, KnightCapabilitiesJson.Options)).Single();
        back.Diagnostic!.Kind.Should().Be(KnightFailureKind.Timeout);
        back.Diagnostic.CorrelationId.Should().Be("c1");
    }

    // ── Compatibilidade dos contratos opcionais com dados gravados antes deste pacote ────────────────────────────────

    /// <summary>
    /// CapabilitiesJson gravado antes do pacote (sem <c>diagnostic</c>) continua legível, e uma capacidade sem diagnóstico é
    /// serializada EXATAMENTE como antes — o texto gravado é o que entra no hash da fotografia e na impressão digital da aquisição.
    /// </summary>
    [Fact]
    public void CapacidadeSemDiagnostico_LegivelESerializadaComoAntes()
    {
        const string antigo = "[{\"capability\":\"ExchangeOrganizationConfig\",\"outcome\":\"Error\",\"detail\":\"Falha anterior.\"}]";

        var lidas = KnightCapabilitiesJson.Deserialize(antigo);

        lidas.Should().ContainSingle(c => c.Capability == KnightCapability.ExchangeOrganizationConfig
            && c.Outcome == KnightCapabilityOutcome.Error && c.Detail == "Falha anterior." && c.Diagnostic == null);
        JsonSerializer.Serialize(lidas, KnightCapabilitiesJson.Options).Should().Be(antigo);
        JsonSerializer.Serialize(lidas, AegisScore.Application.Identity.Adm.IdentityEvidenceFactsJson.Options)
            .Should().NotContain("diagnostic", "a aquisição de identidade também grava sem o campo quando ele não existe");
    }

    /// <summary>Inventário Azure gravado antes do pacote (sem <c>visible</c>) vale como assinatura visível — o padrão anterior.</summary>
    [Fact]
    public void InventarioAzureSemVisible_ValeComoVisivel()
    {
        const string antigo = "{\"subscriptionId\":\"00000000-0000-4000-8000-0000000000b1\",\"displayName\":\"Produção\",\"state\":\"Enabled\","
            + "\"inScope\":true,\"reads\":[]}";

        var inventario = JsonSerializer.Deserialize<AzureSubscriptionInventory>(antigo,
            AegisScore.Application.Identity.Adm.IdentityEvidenceFactsJson.Options)!;

        inventario.Visible.Should().BeTrue();
        inventario.InScope.Should().BeTrue();
        inventario.Reads.Should().BeEmpty();
    }
}
