using System;
using System.Linq;
using AegisScore.Api.Contracts;
using AegisScore.Api.Controllers;
using AegisScore.Application.Services;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// Contrato das MUTAÇÕES de usuário (editar/desativar/reativar): elas devolvem <see cref="TenantUserDto"/> —
/// o MESMO da listagem —, então o frontend recebe <c>hasLocalCredential</c> e nunca rotula erroneamente uma
/// conta local como "provedor corporativo" após uma mutação. O DTO é tenant-scoped (sem <c>TenantId</c>) e
/// jamais carrega senha/hash. Regressão do bug em que essas rotas devolviam <c>UserDto</c> (sem o campo).
/// </summary>
public sealed class TenantUserContractTests
{
    [Fact]
    public void ToTenantUserDto_CarregaHasLocalCredential_ContaLocal()
    {
        var summary = new UserSummary(
            Guid.NewGuid(), Guid.NewGuid(), "ana@demo.example.com", "Ana",
            TenantRole.Manager, IsActive: true, DateTimeOffset.UtcNow, LastLoginAt: null,
            HasLocalCredential: true);

        var dto = UsersController.ToTenantUserDto(summary);

        dto.HasLocalCredential.Should().BeTrue("a mutação precisa carregar hasLocalCredential (era o bug)");
        dto.Email.Should().Be("ana@demo.example.com");
        dto.DisplayName.Should().Be("Ana");
        dto.Role.Should().Be("Manager", "papel viaja como nome na resposta");
        dto.IsActive.Should().BeTrue();
    }

    [Fact]
    public void ToTenantUserDto_FederatedOnly_HasLocalCredentialFalse()
    {
        var summary = new UserSummary(
            Guid.NewGuid(), Guid.NewGuid(), "fed@demo.example.com", "Fed",
            TenantRole.Analyst, true, DateTimeOffset.UtcNow, null, HasLocalCredential: false);

        UsersController.ToTenantUserDto(summary).HasLocalCredential.Should().BeFalse();
    }

    [Fact]
    public void TenantUserDto_EhTenantScoped_ESemSegredo()
    {
        var props = typeof(TenantUserDto).GetProperties().Select(p => p.Name).ToList();

        props.Should().Contain("HasLocalCredential");
        props.Should().NotContain("TenantId", "o tenant é implícito no contexto — não trafega no DTO");
        props.Any(p => p.Contains("Password", StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse("o DTO nunca expõe senha");
        props.Any(p => p.Contains("Hash", StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse("o DTO nunca expõe hash");
    }
}
