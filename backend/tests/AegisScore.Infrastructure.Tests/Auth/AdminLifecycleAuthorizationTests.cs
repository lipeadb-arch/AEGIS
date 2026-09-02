using System.Reflection;
using AegisScore.Api.Auth;
using AegisScore.Api.Contracts;
using AegisScore.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// [AEGIS-MVP-ADMIN-LIFECYCLE-01] O contrato de AUTORIZAÇÃO do ciclo de vida administrativo é parte da
/// correção. Sem harness HTTP, o WIRING é verificado por reflexão (a semântica das policies/papéis é
/// exercitada pelos testes de claims existentes):
///  - o ciclo de vida dos TENANTS (<see cref="PlatformTenantsController"/>) exige a POLICY de plataforma —
///    nenhum papel de tenant o alcança;
///  - o ciclo de vida dos CONECTORES (editar/habilitar/desabilitar/desconectar) exige o papel de tenant
///    <c>TenantAdmin</c>, enquanto listar/testar/sincronizar seguem abertos a qualquer autenticado;
///  - o slug do tenant é IMUTÁVEL — nem sequer trafega no corpo da renomeação.
/// </summary>
public sealed class AdminLifecycleAuthorizationTests
{
    // ---- Tenants: superfície de plataforma --------------------------------------

    [Fact]
    public void CicloDeVidaDeTenants_ExigePolicyDePlataforma_NaoPapelDeTenant()
    {
        var authorize = typeof(PlatformTenantsController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        authorize.Should().ContainSingle("a superfície é protegida a nível de classe");
        authorize[0].Policy.Should().Be(PlatformAuthorization.PolicyName,
            "administrar tenants é autoridade GLOBAL, não papel de tenant");
        authorize[0].Roles.Should().BeNull("não é gated por papel de tenant");
    }

    [Fact]
    public void CicloDeVidaDeTenants_TemTemplateEVerbosEsperados()
    {
        typeof(PlatformTenantsController).GetCustomAttribute<RouteAttribute>()!
            .Template.Should().Be("api/v1/platform/tenants");

        typeof(PlatformTenantsController).GetMethod(nameof(PlatformTenantsController.List))!
            .GetCustomAttribute<HttpGetAttribute>().Should().NotBeNull("o catálogo é um GET");
        typeof(PlatformTenantsController).GetMethod(nameof(PlatformTenantsController.Rename))!
            .GetCustomAttribute<HttpPutAttribute>()!.Template.Should().Be("{tenantId:guid}");
        typeof(PlatformTenantsController).GetMethod(nameof(PlatformTenantsController.Suspend))!
            .GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("{tenantId:guid}/suspend");
        typeof(PlatformTenantsController).GetMethod(nameof(PlatformTenantsController.Reactivate))!
            .GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("{tenantId:guid}/reactivate");
    }

    [Fact]
    public void RenomearTenant_NaoAceitaSlug_OSlugEhImutavel()
    {
        // O corpo da renomeação carrega SÓ o nome — o slug não é parâmetro, então não há como alterá-lo por
        // esta superfície. Records geram EqualityContract; filtramos as propriedades declaradas de dados.
        var props = typeof(RenameTenantRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name)
            .ToList();

        props.Should().ContainSingle().Which.Should().Be("Name", "só o nome de exibição trafega");
        props.Should().NotContain("Slug", "o slug é imutável neste pacote — não pode entrar no corpo");
    }

    // ---- Conectores: ciclo de vida administrativo exige TenantAdmin -------------

    [Theory]
    [InlineData(nameof(ConnectorsController.Update))]
    [InlineData(nameof(ConnectorsController.Disable))]
    [InlineData(nameof(ConnectorsController.Enable))]
    [InlineData(nameof(ConnectorsController.Disconnect))]
    public void CicloDeVidaDeConector_ExigeTenantAdmin(string action)
    {
        var authorize = typeof(ConnectorsController).GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>();
        authorize.Should().NotBeNull($"{action} altera o estado de uma integração — é ato de administrador do ambiente");
        authorize!.Roles.Should().Be("TenantAdmin", $"{action} exige o papel de administrador do tenant");
    }

    [Theory]
    [InlineData(nameof(ConnectorsController.List))]
    [InlineData(nameof(ConnectorsController.Test))]
    [InlineData(nameof(ConnectorsController.Sync))]
    public void OperarConector_NaoExigeTenantAdmin(string action)
    {
        // Listar/testar/sincronizar seguem abertos a qualquer autenticado (a classe é [Authorize]); só a MUTAÇÃO
        // de estado é privilegiada. O invariante: nenhum destes métodos ganhou um gate por papel.
        typeof(ConnectorsController).GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>()
            .Should().BeNull($"{action} não é gated por papel de tenant");
    }
}
