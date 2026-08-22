using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-MVP-POSTURE-02] Validação ESTRUTURAL fail-closed das páginas do transporte compartilhado
/// (<see cref="EntraGraphClient.GetPagedAsync"/>): uma resposta 200 OK com corpo error/{}/raiz array/<c>value</c>
/// de tipo errado, ou <c>@odata.nextLink</c> de tipo inesperado, NÃO é uma página completa — nunca produz coleção
/// vazia em silêncio nem deixa <c>InvalidOperationException</c>/<c>JsonException</c> escapar. Sempre uma
/// <see cref="EntraGraphException"/>(<see cref="EntraGraphErrorKind.Unavailable"/>) SANITIZADA.
/// </summary>
public sealed class EntraGraphClientPagingTests
{
    private static readonly KnightEntraIdConfiguration Cfg = new(
        AzureTenantId: "11111111-2222-3333-4444-555555555555", ClientId: "app", ClientSecret: "SUPER-SECRET");
    private const string Token = "tok";

    private static async Task<List<string>> DrainAsync(string pageBody)
    {
        var client = new EntraGraphClient(new HttpClient(new PageStub(pageBody)));
        var ids = new List<string>();
        await foreach (var item in client.GetPagedAsync(Token, Cfg, "foo", CancellationToken.None))
            ids.Add(item.TryGetProperty("id", out var v) ? v.GetString() ?? "" : "");
        return ids;
    }

    private static async Task AssertFailsClosed(string pageBody)
    {
        var act = async () => await DrainAsync(pageBody);
        (await act.Should().ThrowAsync<EntraGraphException>()).Which.Kind.Should().Be(EntraGraphErrorKind.Unavailable);
    }

    [Fact]
    public Task Page_WithoutValue_FailsClosed() => AssertFailsClosed("""{"notValue":1}""");

    [Fact]
    public Task Page_ValueWrongType_FailsClosed() => AssertFailsClosed("""{"value":{"x":1}}""");

    [Fact]
    public Task Page_RootWrongType_FailsClosed() => AssertFailsClosed("""[{"id":"1"}]""");

    [Fact]
    public Task Page_NextLinkWrongType_FailsClosed() => AssertFailsClosed("""{"value":[],"@odata.nextLink":123}""");

    [Fact]
    public async Task Page_ValidWithNullNextLink_Yields()
    {
        // Controle positivo: value array com nextLink null encerra a paginação e devolve os itens.
        var ids = await DrainAsync("""{"value":[{"id":"1"},{"id":"2"}],"@odata.nextLink":null}""");
        ids.Should().Equal("1", "2");
    }

    /// <summary>Devolve SEMPRE 200 OK com o corpo dado (o transporte exige 200; o teste foca no corpo).</summary>
    private sealed class PageStub : HttpMessageHandler
    {
        private readonly string _body;
        public PageStub(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }
}
