using System.IO;
using System.Security.Claims;
using AegisScore.Api;
using AegisScore.Infrastructure.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// [AEGIS-AUD-018] Autoridade central fail-closed do <c>X-Tenant</c> (TenantConsistencyMiddleware). O tenant
/// vem SEMPRE da claim assinada; um header ausente é aceito (a claim é forte), um MALFORMADO é 400, um
/// DIVERGENTE é 403, e um token autenticado sem tenant válido é 403 — nunca se cai em Guid.Empty, no primeiro
/// tenant ou no anterior. A família <c>/api/v1/auth</c> (login/refresh/troca federada/switch/seleção)
/// atravessa sem a verificação, para não quebrar a autenticação nem a troca federada (sem tenant_id).
/// </summary>
public sealed class XTenantFailClosedTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string OutroTenant = "22222222-2222-2222-2222-222222222222";
    private const string ApiPath = "/api/v1/scoring/dashboard";   // rota tenant-scoped qualquer

    [Fact]
    public async Task ClaimValida_SemHeader_Passa()
    {
        var (status, next) = await RunAsync(Authenticated(Tenant), ApiPath, xTenant: null);
        next.Should().BeTrue("a claim assinada é autoritativa — o X-Tenant é opcional");
        status.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task ClaimValida_HeaderIgual_Passa()
    {
        var (status, next) = await RunAsync(Authenticated(Tenant), ApiPath, xTenant: Tenant);
        next.Should().BeTrue();
        status.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task HeaderDivergente_Rejeita403_SemChamarNext()
    {
        var (status, next) = await RunAsync(Authenticated(Tenant), ApiPath, xTenant: OutroTenant);
        next.Should().BeFalse("acesso cross-tenant não pode alcançar o controller");
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task HeaderMalformado_Rejeita400_SemChamarNext()
    {
        var (status, next) = await RunAsync(Authenticated(Tenant), ApiPath, xTenant: "não-é-guid");
        next.Should().BeFalse("um header quebrado denuncia cliente defeituoso/hostil — fail-closed");
        status.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task TokenAutenticadoSemTenantValido_Rejeita403()
    {
        // Um JWT local sem tenant_id nunca deveria existir: barra fail-closed (não cai em Guid.Empty).
        var (status, next) = await RunAsync(AuthenticatedNoTenant(), ApiPath, xTenant: Tenant);
        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task FamiliaDeAuth_NaoEhBarrada_MesmoComHeaderDivergente()
    {
        // A troca/seleção de tenant vive sob /auth e MUDA o tenant; a troca federada nem tem tenant_id. A
        // família de auth atravessa sem a checagem — do contrário quebraria autenticação e federação.
        var (status, next) = await RunAsync(
            Authenticated(Tenant), "/api/v1/auth/switch-tenant", xTenant: OutroTenant);
        next.Should().BeTrue("a família de auth gerencia o próprio tenant");
        status.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Anonimo_Passa_MesmoComHeaderArbitrario()
    {
        // Login/refresh são anônimos (o tenant deles vem do header e é isolado pelo query filter). Não barrar.
        var (status, next) = await RunAsync(Anonymous(), "/api/v1/auth/login", xTenant: "qualquer-coisa");
        next.Should().BeTrue();
        status.Should().Be(StatusCodes.Status200OK);
    }

    // ---- Harness ---------------------------------------------------------------------------------

    private static ClaimsPrincipal Authenticated(string tenantId) =>
        new(new ClaimsIdentity(
            new[] { new Claim(JwtTokenService.TenantClaim, tenantId), new Claim("sub", "user-1") },
            authenticationType: "TestAuth"));

    private static ClaimsPrincipal AuthenticatedNoTenant() =>
        new(new ClaimsIdentity(new[] { new Claim("sub", "user-1") }, authenticationType: "TestAuth"));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static async Task<(int Status, bool NextCalled)> RunAsync(
        ClaimsPrincipal user, string path, string? xTenant)
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var middleware = new TenantConsistencyMiddleware(next, NullLogger<TenantConsistencyMiddleware>.Instance);

        var ctx = new DefaultHttpContext { User = user };
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        if (xTenant is not null)
            ctx.Request.Headers["X-Tenant"] = xTenant;
        ctx.Response.Body = new MemoryStream();   // WriteAsJsonAsync precisa de um corpo gravável

        await middleware.InvokeAsync(ctx);
        return (ctx.Response.StatusCode, nextCalled);
    }
}
