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
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Services;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-GAPS-01] Contrato das respostas paginadas do cliente REST (Resource Manager, Fabric, Key Vault).
/// Antes: um 200 sem a lista <c>value</c> virava coleção vazia, e o teto de páginas encerrava a leitura em silêncio com
/// continuação pendente. Agora: lista vazia válida ≠ resposta fora do contrato ≠ leitura interrompida — e o que já foi lido
/// continua entregue a quem consome, sem ser declarado completo.
/// </summary>
public sealed class KnightRestContractTests
{
    private const string Arm = "https://management.azure.com";

    private static async Task<(List<string> Items, Exception? Error)> DrainAsync(MicrosoftRestClient client, string url, int maxPages = 200)
    {
        var ids = new List<string>();
        try
        {
            await foreach (var item in client.GetPagedAsync("tok", url, CancellationToken.None, maxPages))
                ids.Add(item.TryGetProperty("id", out var v) ? v.GetString() ?? "" : "");
            return (ids, null);
        }
        catch (Exception ex)
        {
            return (ids, ex);
        }
    }

    [Fact]
    public async Task ColecaoVaziaValida_TerminaSemErro()
    {
        var (items, error) = await DrainAsync(Client(("/subscriptions", """{"value":[]}""")), $"{Arm}/subscriptions?api-version=2022-12-01");
        error.Should().BeNull("lista vazia com o contrato documentado é coleção vazia legítima");
        items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"notValue":1}""")]
    [InlineData("""{"value":{"id":"x"}}""")]
    [InlineData("""[{"id":"1"}]""")]
    [InlineData("")]
    public async Task RespostaSemListaValue_EhFalhaDeContrato_NuncaColecaoVazia(string body)
    {
        var (items, error) = await DrainAsync(Client(("/subscriptions", body)), $"{Arm}/subscriptions?api-version=2022-12-01");
        items.Should().BeEmpty();
        var g = error.Should().BeOfType<EntraGraphException>().Subject;
        g.Kind.Should().Be(EntraGraphErrorKind.Contract);
        g.EndpointPath.Should().Be("/subscriptions");
        g.HttpStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task ProximoLinkComTipoInvalido_EhFalhaDeContrato()
    {
        var (_, error) = await DrainAsync(Client(("/subscriptions", """{"value":[{"id":"a"}],"nextLink":42}""")), $"{Arm}/subscriptions?api-version=2022-12-01");
        error.Should().BeOfType<EntraGraphException>().Which.Kind.Should().Be(EntraGraphErrorKind.Contract);
    }

    [Fact]
    public async Task TetoDePaginas_ComContinuacao_EhLeituraIncompleta_EPreservaOJaLido()
    {
        // Cada página aponta para a próxima: o teto (2) chega com continuação pendente.
        var client = Client(("/subscriptions", """{"value":[{"id":"p1"}],"nextLink":"https://management.azure.com/subscriptions?api-version=2022-12-01&$skiptoken=2"}"""));
        var (items, error) = await DrainAsync(client, $"{Arm}/subscriptions?api-version=2022-12-01", maxPages: 2);

        items.Should().Equal(new[] { "p1", "p1" }, "as páginas lidas antes do teto continuam entregues");
        var g = error.Should().BeOfType<EntraGraphException>().Subject;
        g.Kind.Should().Be(EntraGraphErrorKind.Incomplete, "parar no teto com continuação não é fim da coleção");
        g.EndpointPath.Should().Be("/subscriptions");

        var failure = KnightFailureClassifier.Classify(g, "Microsoft Azure", "read");
        failure.Outcome.Should().NotBe(KnightCapabilityOutcome.Collected);
        failure.Diagnostic.Kind.Should().Be(KnightFailureKind.Incomplete);
    }

    [Fact]
    public async Task TetoDePaginas_SemContinuacao_TerminaNormalmente()
    {
        var (items, error) = await DrainAsync(Client(("/subscriptions", """{"value":[{"id":"p1"}]}""")), $"{Arm}/subscriptions?api-version=2022-12-01", maxPages: 1);
        error.Should().BeNull();
        items.Should().Equal("p1");
    }

    [Fact]
    public async Task ContinuacaoParaOutroHost_ContinuaRecusada()
    {
        var client = Client(("/subscriptions", """{"value":[],"nextLink":"https://evil.example.com/subscriptions"}"""));
        var (_, error) = await DrainAsync(client, $"{Arm}/subscriptions?api-version=2022-12-01");
        error.Should().BeOfType<EntraGraphException>().Which.Kind.Should().Be(EntraGraphErrorKind.Contract);
    }

    [Fact]
    public async Task CancelamentoDoChamador_Propaga()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = Client(("/subscriptions", """{"value":[{"id":"a"}]}"""));
        var act = async () =>
        {
            await foreach (var _ in client.GetPagedAsync("tok", $"{Arm}/subscriptions?api-version=2022-12-01", cts.Token)) { }
        };
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Fabric_ContinuationToken_SegueNoMesmoEndpoint_EVazioValidoTermina()
    {
        var client = Client(
            ("/v1/admin/tenantsettings", """{"tenantSettings":[],"value":[{"id":"s1"}],"continuationToken":"abc"}"""),
            ("/v1/admin/tenantsettings?continuationToken=abc", """{"value":[{"id":"s2"}]}"""));
        var (items, error) = await DrainAsync(client, "https://api.fabric.microsoft.com/v1/admin/tenantsettings");
        error.Should().BeNull();
        items.Should().Equal("s1", "s2");
    }

    [Fact]
    public async Task Fabric_RespostaSemLista_NaoViraConfiguracaoVazia()
    {
        // O coletor do Fabric consome o mesmo cliente: uma resposta fora do contrato vira falha declarada da capacidade,
        // nunca "nenhuma configuração" (o que deixaria os controles sem a limitação real).
        var http = new HttpClient(new Routes(("/v1/admin/tenantsettings", """{"tenantSettings":[{"settingName":"X"}]}""")));
        var collector = new FabricKnightCollector(new FixedToken(), new MicrosoftRestClient(http));
        var result = await collector.CollectAsync(new KnightCollectionContext(Guid.NewGuid(),
            new KnightMicrosoftServiceConfiguration(KnightSourceType.MicrosoftFabric, "dir", "app", "segredo", null, null)));

        var cap = result.Capabilities.Single(c => c.Capability == KnightCapability.FabricTenantSettings);
        cap.Outcome.Should().Be(KnightCapabilityOutcome.Error);
        cap.Diagnostic!.Kind.Should().Be(KnightFailureKind.Contract);
        cap.Detail.Should().Contain("contrato").And.Contain("/v1/admin/tenantsettings");
        result.TenantConfiguration!.Read<FabricTenantSetting>().Collected.Should().BeFalse();
    }

    // ---- Infra -----------------------------------------------------------------------------------------------

    internal static MicrosoftRestClient Client(params (string PathAndQuery, string Body)[] routes) =>
        new(new HttpClient(new Routes(routes)), (_, _) => Task.CompletedTask);

    internal sealed class FixedToken : IMicrosoftAppTokenClient
    {
        public Task<string> AcquireAsync(IMicrosoftGraphCredentials credentials, string scope, CancellationToken ct = default) =>
            Task.FromResult("tok");
    }

    /// <summary>Rotas por caminho (com query quando a rota a declara); sem rota → 404.</summary>
    internal sealed class Routes : HttpMessageHandler
    {
        private readonly (string PathAndQuery, string Body)[] _routes;
        public Routes(params (string, string)[] routes) => _routes = routes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            var withQuery = Uri.UnescapeDataString(uri.PathAndQuery);
            var match = _routes.FirstOrDefault(r => r.PathAndQuery.Contains('?') && withQuery == r.PathAndQuery);
            if (match.PathAndQuery is null) match = _routes.FirstOrDefault(r => !r.PathAndQuery.Contains('?') && uri.AbsolutePath == r.PathAndQuery);
            return Task.FromResult(match.PathAndQuery is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"code":"NotFound"}}""", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(match.Body, Encoding.UTF8, "application/json") });
        }
    }
}
