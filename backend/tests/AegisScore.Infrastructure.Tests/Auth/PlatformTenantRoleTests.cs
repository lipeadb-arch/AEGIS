using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AegisScore.Api.Auth;
using AegisScore.Domain;
using AegisScore.Infrastructure.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// [AEGIS-AUD-011] Separação dos DOIS eixos de papel: tenant-scoped (<see cref="TenantRole"/>, claim
/// <c>role</c>) e global (<see cref="PlatformRole"/>, claim <c>platform_role</c>). Prova a forma do enum,
/// a emissão das claims pelo <see cref="JwtTokenService"/> e a autorização REAL — a policy de plataforma
/// (<see cref="PlatformAuthorization"/>) avaliada por <see cref="IAuthorizationService"/> contra
/// <see cref="ClaimsPrincipal"/> reais, não por reflexão sobre atributos.
/// </summary>
public sealed class PlatformTenantRoleTests
{
    // ---- Forma do enum tenant-scoped --------------------------------------------

    [Fact]
    public void TenantRole_NaoContemPlatformAdmin_EPreservaValores012()
    {
        Enum.GetNames<TenantRole>().Should().BeEquivalentTo("Analyst", "Manager", "TenantAdmin");
        Enum.GetNames<TenantRole>().Should().NotContain("PlatformAdmin",
            "autoridade global não é papel de membership");
        ((int)TenantRole.Analyst).Should().Be(0);
        ((int)TenantRole.Manager).Should().Be(1);
        ((int)TenantRole.TenantAdmin).Should().Be(2);
    }

    // ---- JWT: os dois eixos, corretamente ---------------------------------------

    [Fact]
    public void Jwt_AnalystComPlatformAdminGlobal_CarregaOsDoisEixos()
    {
        var (user, account) = Membership(TenantRole.Analyst, PlatformRole.PlatformAdmin);
        var jwt = Decode(Jwt().CreateAccessToken(user, account).Token);

        jwt.Claims.Should().Contain(c => c.Type == "role" && c.Value == "Analyst",
            "o eixo tenant é o papel DO tenant ativo");
        jwt.Claims.Should().Contain(c => c.Type == "platform_role" && c.Value == "PlatformAdmin",
            "o eixo global vem da identidade, não do membership");
    }

    [Fact]
    public void Jwt_TenantAdminSemPapelGlobal_NaoCarregaClaimGlobal()
    {
        var (user, account) = Membership(TenantRole.TenantAdmin, PlatformRole.None);
        var jwt = Decode(Jwt().CreateAccessToken(user, account).Token);

        jwt.Claims.Should().Contain(c => c.Type == "role" && c.Value == "TenantAdmin");
        jwt.Claims.Should().NotContain(c => c.Type == "platform_role",
            "sem autoridade global a claim é AUSENTE — nunca platform_role=None");
    }

    [Fact]
    public void Jwt_PlatformRoleNuncaEhDerivadaDeUserRole()
    {
        // TenantAdmin no tenant, mas SEM papel global → nenhuma claim global. Prova que platform_role não
        // "vaza" de um papel de tenant elevado.
        var (user, account) = Membership(TenantRole.TenantAdmin, PlatformRole.None);
        var jwt = Decode(Jwt().CreateAccessToken(user, account).Token);
        jwt.Claims.Any(c => c.Type == "platform_role").Should().BeFalse();
    }

    // ---- Policy REAL: plataforma vs tenant --------------------------------------

    [Fact]
    public async Task Policy_TenantAdminSozinho_FalhaNaPolicyDePlataforma()
    {
        var principal = Principal(role: "TenantAdmin", platformRole: null);
        (await Authorize(principal, PlatformAuthorization.PolicyName)).Succeeded
            .Should().BeFalse("ser TenantAdmin no tenant não concede autoridade de plataforma");
    }

    [Fact]
    public async Task Policy_PlatformAdminGlobal_PassaMesmoSendoAnalystNoTenant()
    {
        var principal = Principal(role: "Analyst", platformRole: "PlatformAdmin");
        (await Authorize(principal, PlatformAuthorization.PolicyName)).Succeeded
            .Should().BeTrue("a autoridade global independe do papel no tenant ativo");
    }

    [Fact]
    public async Task Policy_PlatformAdminGlobal_NaoPassaPorRequireRoleTenantAdmin()
    {
        // O eixo global NÃO concede poderes de TenantAdmin: [Authorize(Roles="TenantAdmin")] continua
        // olhando a claim `role`, que aqui é Analyst.
        var principal = Principal(role: "Analyst", platformRole: "PlatformAdmin");
        (await Authorize(principal, TenantAdminRolePolicy)).Succeeded
            .Should().BeFalse("platform_role não substitui o papel de tenant exigido por [Authorize(Roles=...)]");
    }

    [Fact]
    public async Task Policy_TenantAdmin_PassaPorRequireRoleTenantAdmin()
    {
        // Sanidade: quem É TenantAdmin no tenant passa no gate tenant-scoped (mas não no de plataforma).
        var principal = Principal(role: "TenantAdmin", platformRole: null);
        (await Authorize(principal, TenantAdminRolePolicy)).Succeeded.Should().BeTrue();
        (await Authorize(principal, PlatformAuthorization.PolicyName)).Succeeded.Should().BeFalse();
    }

    // ---- Fixture ----------------------------------------------------------------

    private const string TenantAdminRolePolicy = "TenantAdminRole";

    private static JwtTokenService Jwt() => new(Options.Create(new JwtOptions
    {
        SigningKey = "aegis-test-signing-key-com-mais-de-32-bytes", Issuer = "aegis-score", Audience = "aegis-score",
    }));

    private static JwtSecurityToken Decode(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token);

    private static (User, IdentityAccount) Membership(TenantRole role, PlatformRole platformRole)
    {
        var account = new IdentityAccount
        {
            Id = Guid.NewGuid(), Email = "ana@demo.example.com",
            PasswordHash = "x", PlatformRole = platformRole,
        };
        var user = new User
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(),
            IdentityAccountId = account.Id, DisplayName = "Ana", Role = role,
        };
        return (user, account);
    }

    /// <summary>Principal com a claim de papel do MESMO tipo que o JWT bearer usa (RoleClaimType="role").</summary>
    private static ClaimsPrincipal Principal(string role, string? platformRole)
    {
        var claims = new List<Claim> { new("role", role) };
        if (platformRole is not null) claims.Add(new Claim("platform_role", platformRole));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test", nameType: "name", roleType: "role"));
    }

    private static async Task<AuthorizationResult> Authorize(ClaimsPrincipal principal, string policy)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(o =>
        {
            PlatformAuthorization.AddPlatformPolicy(o);            // a policy REAL do Program.cs
            o.AddPolicy(TenantAdminRolePolicy, p => p.RequireRole("TenantAdmin"));   // espelha [Authorize(Roles="TenantAdmin")]
        });
        await using var provider = services.BuildServiceProvider();
        var authz = provider.GetRequiredService<IAuthorizationService>();
        return await authz.AuthorizeAsync(principal, policy);
    }
}
