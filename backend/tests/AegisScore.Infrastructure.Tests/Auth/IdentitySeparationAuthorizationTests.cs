using System.Reflection;
using AegisScore.Api.Auth;
using AegisScore.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// [AEGIS-AUD-010 / AUD-011] O contrato de AUTORIZAÇÃO das superfícies separadas de identidade é parte da
/// correção, não detalhe cosmético — afrouxar um atributo reabriria o vetor. Sem harness de integração HTTP,
/// o WIRING é verificado por reflexão (a semântica da policy em si é exercitada por
/// <see cref="PlatformTenantRoleTests"/>, com claims/policy reais):
///  - a rota GLOBAL (<see cref="PlatformIdentitiesController"/>) exige a POLICY de plataforma
///    (<see cref="PlatformAuthorization.PolicyName"/>), NÃO um papel de tenant;
///  - a concessão de acesso (<see cref="UsersController"/>) exige o papel de tenant <c>TenantAdmin</c>;
///  - a rota legada <c>POST /api/v1/users</c> (que deixava o TenantAdmin criar identidade global) NÃO existe.
/// </summary>
public sealed class IdentitySeparationAuthorizationTests
{
    // ---- Rota global: exige a POLICY de plataforma (não um papel de tenant) ------

    [Fact]
    public void RotaGlobalDeIdentidade_ExigePolicyDePlataforma_NaoPapelDeTenant()
    {
        var authorize = typeof(PlatformIdentitiesController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ToList();

        authorize.Should().ContainSingle("a superfície global é protegida a nível de classe");
        // [AEGIS-AUD-011] Policy global (platform_role=PlatformAdmin), não [Authorize(Roles=...)].
        authorize[0].Policy.Should().Be(PlatformAuthorization.PolicyName,
            "criar identidade global exige a autoridade GLOBAL, não um papel de tenant");
        authorize[0].Roles.Should().BeNull("a rota de plataforma não é mais gated por papel de tenant");
    }

    [Fact]
    public void CriacaoDeTenant_ExigePolicyDePlataforma()
    {
        // A outra superfície de plataforma migrada para a policy no AUD-011.
        var create = typeof(TenantsController).GetMethod(nameof(TenantsController.Create))!;
        var authorize = create.GetCustomAttribute<AuthorizeAttribute>();

        authorize.Should().NotBeNull();
        authorize!.Policy.Should().Be(PlatformAuthorization.PolicyName);
        authorize.Roles.Should().BeNull();
    }

    [Fact]
    public void RotaGlobalDeIdentidade_TemTemplateEMetodoEsperados()
    {
        typeof(PlatformIdentitiesController).GetCustomAttribute<RouteAttribute>()!
            .Template.Should().Be("api/v1/platform/identities");

        var provision = typeof(PlatformIdentitiesController).GetMethod(nameof(PlatformIdentitiesController.Provision))!;
        provision.GetCustomAttribute<HttpPostAttribute>().Should().NotBeNull("o provisionamento é um POST");
    }

    // ---- Concessão de acesso: exige TenantAdmin ---------------------------------

    [Fact]
    public void ConcessaoDeAcesso_ExigeTenantAdmin()
    {
        var grant = typeof(UsersController).GetMethod(nameof(UsersController.GrantAccess))!;
        var authorize = grant.GetCustomAttribute<AuthorizeAttribute>();

        authorize.Should().NotBeNull("a concessão de acesso é uma escrita privilegiada");
        authorize!.Roles.Should().Be("TenantAdmin");
    }

    // ---- A rota legada não pode sobreviver --------------------------------------

    [Fact]
    public void UsersController_NaoTemMaisRotaDeCriacaoDeIdentidadeNaRaiz()
    {
        // A antiga POST /api/v1/users (RAIZ) criava a IdentityAccount por um TenantAdmin. O invariante de
        // segurança é: nenhum POST deste controller pode mapear para a RAIZ (template vazio/nulo). As ações
        // administrativas legítimas (concessão + desativar/reativar) têm templates PRÓPRIOS, nunca a raiz.
        var postTemplates = typeof(UsersController).GetMethods()
            .Select(m => m.GetCustomAttribute<HttpPostAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Template)
            .ToList();

        postTemplates.Should().NotBeEmpty();
        postTemplates.Should().OnlyContain(t => !string.IsNullOrEmpty(t),
            "nenhum POST mapeia para a raiz — a criação de identidade por um TenantAdmin não pode voltar");
        postTemplates.Should().Contain("access", "a concessão de acesso a identidade preexistente permanece");
    }

    [Fact]
    public void UsersController_NaoTemAcaoCreate()
    {
        typeof(UsersController).GetMethod("Create")
            .Should().BeNull("a criação de identidade saiu desta superfície (virou provisionamento global)");
    }

    // ---- Onboarding: exige SIMULTANEAMENTE PlatformAdmin (global) e TenantAdmin ---

    [Fact]
    public void OnboardingDeUsuario_ExigeSimultaneamentePlatformAdminETenantAdmin()
    {
        // A ÚNICA superfície que cria identidade global E concede acesso exige AS DUAS autoridades: a policy
        // global de plataforma E o papel tenant-scoped TenantAdmin. Dois [Authorize] de classe = E lógico —
        // um TenantAdmin SEM autoridade global é recusado (403), e não pode materializar identidade global.
        var authorize = typeof(PlatformTenantUsersController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        authorize.Should().HaveCount(2, "as duas autoridades se combinam com E lógico");
        authorize.Should().Contain(a => a.Policy == PlatformAuthorization.PolicyName,
            "criar identidade global exige a autoridade GLOBAL de plataforma");
        authorize.Should().Contain(a => a.Roles == "TenantAdmin",
            "conceder acesso exige o papel de administrador do tenant ambiente");
    }

    [Fact]
    public void OnboardingDeUsuario_TemTemplateEMetodoEsperados()
    {
        typeof(PlatformTenantUsersController).GetCustomAttribute<RouteAttribute>()!
            .Template.Should().Be("api/v1/platform/tenant-users");
        typeof(PlatformTenantUsersController).GetMethod(nameof(PlatformTenantUsersController.Onboard))!
            .GetCustomAttribute<HttpPostAttribute>().Should().NotBeNull("o onboarding é um POST");
    }

    // ---- Administração de acessos (listar/editar/desativar/reativar): TenantAdmin -

    [Theory]
    [InlineData(nameof(UsersController.List))]
    [InlineData(nameof(UsersController.Update))]
    [InlineData(nameof(UsersController.Deactivate))]
    [InlineData(nameof(UsersController.Reactivate))]
    public void AdministracaoDeAcessos_ExigeTenantAdmin(string action)
    {
        var authorize = typeof(UsersController).GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>();
        authorize.Should().NotBeNull($"{action} é administração de acessos do tenant");
        authorize!.Roles.Should().Be("TenantAdmin", $"{action} exige o papel de administrador do tenant");
    }
}
