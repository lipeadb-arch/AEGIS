using System.Security.Claims;
using AegisScore.Infrastructure.Auth;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// [AEGIS-AUD-007] Autorização da troca federada (a regra que a policy e o controller compartilham). Prova
/// que só um token DELEGADO do SPA configurado, com o scope certo e do tenant certo, é aceito — e que a
/// identidade sai canonicalizada. Não precisa de tenant real: usa principals controlados pelos testes.
/// </summary>
public sealed class FederatedPrincipalValidatorTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Spa = "33333333-3333-3333-3333-333333333333";
    private const string ApiClient = "22222222-2222-2222-2222-222222222222";
    private const string Scope = "access_as_user";

    private static FederationOptions Fed() => new()
    {
        Mode = FederationMode.Federated,
        TenantId = Tid,
        ApiClientId = ApiClient,
        ApiScope = $"api://{ApiClient}/{Scope}",
        SpaClientId = Spa,
    };

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "EntraId"));

    private static (string, string)[] ValidClaims() => new[]
    {
        ("scp", Scope), ("azp", Spa), ("tid", Tid), ("oid", Oid), ("preferred_username", "ana@demo.example.com"),
    };

    [Fact]
    public void TokenDelegadoValido_Aceito_ComIdentidadeCanonica()
    {
        FederatedPrincipalValidator.TryValidate(Principal(ValidClaims()), Fed(), out var id).Should().BeTrue();
        id.TenantId.Should().Be(Tid);
        id.ObjectId.Should().Be(Oid);
        id.Email.Should().Be("ana@demo.example.com");
    }

    [Fact]
    public void TokenAppOnly_SemScp_Recusado_RolesNaoSubstitui()
    {
        // App-only (client credentials) traz `roles`, não `scp`. Não pode trocar.
        var p = Principal(("roles", "Access.All"), ("azp", Spa), ("tid", Tid), ("oid", Oid));
        FederatedPrincipalValidator.TryValidate(p, Fed(), out _).Should().BeFalse();
    }

    [Fact]
    public void ScopeErrado_Recusado()
    {
        var p = Principal(("scp", "User.Read"), ("azp", Spa), ("tid", Tid), ("oid", Oid));
        FederatedPrincipalValidator.TryValidate(p, Fed(), out _).Should().BeFalse();
    }

    [Fact]
    public void ScopeCorretoNaListaDeEspacos_Aceito()
    {
        var p = Principal(("scp", $"User.Read {Scope} openid"), ("azp", Spa), ("tid", Tid), ("oid", Oid));
        FederatedPrincipalValidator.TryValidate(p, Fed(), out _).Should().BeTrue();
    }

    [Fact]
    public void AzpDeOutroCliente_Recusado()
    {
        var p = Principal(("scp", Scope), ("azp", "44444444-4444-4444-4444-444444444444"), ("tid", Tid), ("oid", Oid));
        FederatedPrincipalValidator.TryValidate(p, Fed(), out _).Should().BeFalse();
    }

    [Fact]
    public void AppidV1_DoSpa_Aceito()
    {
        // Token v1 usa `appid` no lugar de `azp`.
        var p = Principal(("scp", Scope), ("appid", Spa), ("tid", Tid), ("oid", Oid));
        FederatedPrincipalValidator.TryValidate(p, Fed(), out _).Should().BeTrue();
    }

    [Fact]
    public void SemAzpNemAppid_Recusado()
    {
        var p = Principal(("scp", Scope), ("tid", Tid), ("oid", Oid));
        FederatedPrincipalValidator.TryValidate(p, Fed(), out _).Should().BeFalse();
    }

    [Fact]
    public void TidDiferente_Recusado()
    {
        var p = Principal(("scp", Scope), ("azp", Spa), ("tid", "99999999-9999-9999-9999-999999999999"), ("oid", Oid));
        FederatedPrincipalValidator.TryValidate(p, Fed(), out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("tid", "não-é-guid")]
    [InlineData("oid", "12345")]
    public void TidOuOidMalformado_Recusado(string claim, string valor)
    {
        var claims = ValidClaims().Select(c => c.Item1 == claim ? (c.Item1, valor) : c).ToArray();
        FederatedPrincipalValidator.TryValidate(Principal(claims), Fed(), out _).Should().BeFalse();
    }

    [Fact]
    public void GuidsSaoCanonicalizados_ParaFormatoD()
    {
        var p = Principal(("scp", Scope), ("azp", Spa.ToUpperInvariant()),
            ("tid", Tid.ToUpperInvariant()), ("oid", Oid.ToUpperInvariant()));
        FederatedPrincipalValidator.TryValidate(p, Fed(), out var id).Should().BeTrue();
        id.TenantId.Should().Be(Tid, "canonicalizado para minúsculo 'D'");
        id.ObjectId.Should().Be(Oid);
    }

    [Fact]
    public void FederacaoDesligada_Recusado()
    {
        FederatedPrincipalValidator.TryValidate(Principal(ValidClaims()),
            new FederationOptions { Mode = FederationMode.Local }, out _).Should().BeFalse();
    }

    [Fact]
    public void PrincipalNaoAutenticado_Recusado()
    {
        // Sem authenticationType, IsAuthenticated é false.
        var anon = new ClaimsPrincipal(new ClaimsIdentity(
            ValidClaims().Select(c => new Claim(c.Item1, c.Item2))));
        FederatedPrincipalValidator.TryValidate(anon, Fed(), out _).Should().BeFalse();
    }
}
