using AegisScore.Infrastructure.Auth;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// [AEGIS-AUD-007] O fail-fast da configuração de federação e a sanitização da config pública. Em
/// Federated/Hybrid a config obrigatória é validada ANTES de servir; em Local nada é exigido. A projeção
/// pública nunca carrega segredo (não há segredo — só identificadores públicos), e em Local não expõe
/// authority/client id.
/// </summary>
public sealed class FederationOptionsTests
{
    private static FederationOptions Complete(FederationMode mode) => new()
    {
        Mode = mode,
        TenantId = "11111111-1111-1111-1111-111111111111",
        ApiClientId = "22222222-2222-2222-2222-222222222222",
        ApiScope = "api://22222222-2222-2222-2222-222222222222/access_as_user",
        SpaClientId = "33333333-3333-3333-3333-333333333333",
    };

    [Theory]
    [InlineData(FederationMode.Federated)]
    [InlineData(FederationMode.Hybrid)]
    public void EnabledMode_ComConfigCompleta_Valida(FederationMode mode)
    {
        var act = () => Complete(mode).Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(nameof(FederationOptions.TenantId))]
    [InlineData(nameof(FederationOptions.ApiClientId))]
    [InlineData(nameof(FederationOptions.ApiScope))]
    [InlineData(nameof(FederationOptions.SpaClientId))]
    public void Federated_ComConfigIncompleta_FalhaAntesDeServir(string faltando)
    {
        var opt = Complete(FederationMode.Federated);
        switch (faltando)
        {
            case nameof(FederationOptions.TenantId): opt.TenantId = " "; break;
            case nameof(FederationOptions.ApiClientId): opt.ApiClientId = null; break;
            case nameof(FederationOptions.ApiScope): opt.ApiScope = ""; break;
            case nameof(FederationOptions.SpaClientId): opt.SpaClientId = null; break;
        }

        var act = () => opt.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{faltando}*");
    }

    [Fact]
    public void Local_NaoExigeNada_MesmoSemConfig()
    {
        var act = () => new FederationOptions { Mode = FederationMode.Local }.Validate();
        act.Should().NotThrow("Local é o modo sem federação — dev/demonstração seguem intactos");
    }

    [Fact]
    public void ModosDefinemLoginPorSenhaEFederacao()
    {
        new FederationOptions { Mode = FederationMode.Local }.PasswordLoginEnabled.Should().BeTrue();
        new FederationOptions { Mode = FederationMode.Local }.FederationEnabled.Should().BeFalse();

        new FederationOptions { Mode = FederationMode.Federated }.PasswordLoginEnabled.Should().BeFalse();
        new FederationOptions { Mode = FederationMode.Federated }.FederationEnabled.Should().BeTrue();

        new FederationOptions { Mode = FederationMode.Hybrid }.PasswordLoginEnabled.Should().BeTrue();
        new FederationOptions { Mode = FederationMode.Hybrid }.FederationEnabled.Should().BeTrue();
    }

    [Fact]
    public void AuthorityEIssuersDerivamDoTenant()
    {
        var opt = Complete(FederationMode.Federated);
        opt.Authority.Should().Be("https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0");
        opt.ValidIssuers.Should().Contain(opt.Authority);
        opt.ValidAudiences.Should().Contain(opt.ApiClientId!);
        opt.ValidAudiences.Should().Contain($"api://{opt.ApiClientId}");
    }

    [Fact]
    public void ConfigPublica_Federated_ExpoeSoIdentificadoresPublicos()
    {
        var pub = Complete(FederationMode.Federated).ToPublicConfig();

        pub.Enabled.Should().BeTrue();
        pub.PasswordLoginEnabled.Should().BeFalse("Federated desabilita login por senha");
        pub.Authority.Should().Be("https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0");
        pub.SpaClientId.Should().Be("33333333-3333-3333-3333-333333333333");
        pub.Scope.Should().Be("api://22222222-2222-2222-2222-222222222222/access_as_user");
        // Sanidade: o record público NÃO tem nenhum campo de segredo (secret/clientSecret/key). (O flag
        // PasswordLoginEnabled é política de UI, não segredo — por isso a checagem mira "secret"/"key".)
        typeof(FederationPublicConfig).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("Key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigPublica_Local_NaoExpoeAuthorityNemClientId()
    {
        var pub = new FederationOptions { Mode = FederationMode.Local }.ToPublicConfig();

        pub.Enabled.Should().BeFalse();
        pub.PasswordLoginEnabled.Should().BeTrue();
        pub.Authority.Should().BeNull();
        pub.SpaClientId.Should().BeNull();
        pub.Scope.Should().BeNull();
    }
}
