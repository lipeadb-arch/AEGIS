using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

public sealed class EntraGraphDiagnosticsTests
{
    private static readonly KnightEntraIdConfiguration Cfg = new(
        "11111111-2222-3333-4444-555555555555", "app", "secret");

    [Fact]
    public async Task Forbidden_ExposesOnlySanitizedDiagnostics()
    {
        var handler = new ForbiddenHandler();
        var client = new EntraGraphClient(new HttpClient(handler));

        var act = async () =>
        {
            await foreach (var _ in client.GetPagedAsync("token", Cfg,
                "users?$filter=userType eq 'Guest'&$select=id,signInActivity", CancellationToken.None))
            { }
        };

        var ex = (await act.Should().ThrowAsync<EntraGraphException>()).Which;
        ex.Kind.Should().Be(EntraGraphErrorKind.InsufficientPermission);
        ex.HttpStatusCode.Should().Be(403);
        ex.GraphErrorCode.Should().Be("Authorization_RequestDenied");
        ex.EndpointPath.Should().Be("/v1.0/users");
        ex.Message.Should().NotContain("secret-token");
        ex.Message.Should().NotContain("raw-sensitive-message");
        ex.EndpointPath.Should().NotContain("$");
    }

    [Fact]
    public async Task Collector_SurfacesSanitizedHttpCodeGraphCodeAndEndpoint()
    {
        var collector = new EntraIdKnightCollector(new AlwaysForbiddenGraphClient());
        var result = await collector.CollectAsync(new KnightCollectionContext(Guid.NewGuid(), Cfg));

        result.State.Should().Be(KnightSourceState.InsufficientPermission);
        result.Capabilities.Should().NotBeEmpty();
        result.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.InsufficientPermission);
        result.Capabilities.Should().OnlyContain(c => c.Detail != null &&
            c.Detail.Contains("HTTP 403") &&
            c.Detail.Contains("Graph: Authorization_RequestDenied") &&
            c.Detail.Contains("endpoint: /v1.0/test"));
    }

    private sealed class ForbiddenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    """{"error":{"code":"Authorization_RequestDenied","message":"raw-sensitive-message"}}""",
                    Encoding.UTF8,
                    "application/json")
            });
    }

    private sealed class AlwaysForbiddenGraphClient : IEntraGraphClient
    {
        public Task<string> AcquireTokenAsync(IMicrosoftGraphCredentials config, CancellationToken ct) =>
            Task.FromResult("token");

        public async IAsyncEnumerable<JsonElement> GetPagedAsync(
            string token, IMicrosoftGraphCredentials config, string relativeUrl,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw Failure();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public Task<JsonElement> GetJsonAsync(
            string token, IMicrosoftGraphCredentials config, string relativeUrl, CancellationToken ct) =>
            Task.FromException<JsonElement>(Failure());

        private static EntraGraphException Failure() => new(
            EntraGraphErrorKind.InsufficientPermission,
            "graph retornou 403",
            httpStatusCode: 403,
            graphErrorCode: "Authorization_RequestDenied",
            endpointPath: "/v1.0/test");
    }
}
