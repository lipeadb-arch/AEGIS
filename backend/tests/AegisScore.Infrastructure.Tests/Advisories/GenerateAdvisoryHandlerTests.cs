using AegisScore.Application.Abstractions;
using AegisScore.Application.Advisories;
using AegisScore.Infrastructure.Advisories;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Advisories;

/// <summary>
/// Testes do <see cref="GenerateAdvisoryHandler"/> sobre SQLite in-memory, exercitando o fluxo real
/// IA (Stub canned) → persistência → DTO. Travam o contrato consultivo: o texto vem do motor (não do
/// cliente), o TenantId é carimbado fail-closed no SaveChanges e o isolamento entre tenants é imposto
/// pelo Global Query Filter, não por um <c>Where</c> explícito.
/// </summary>
public sealed class GenerateAdvisoryHandlerTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;

    public GenerateAdvisoryHandlerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task HandleAsync_PersisteEDevolveAdvisoryComTextoCannedDoStub()
    {
        await using var db = NewContext(TenantA);
        var handler = new GenerateAdvisoryHandler(db, new StubAssessmentService());

        var dto = await handler.HandleAsync(new GenerateAdvisoryCommand("PR.AA-01"));

        dto.SubcategoryCode.Should().Be("PR.AA-01");
        dto.Title.Should().Contain("MFA", "o Stub redige texto canned ancorado no código do controle");
        dto.DocumentedRisk.Should().NotBeNullOrWhiteSpace();
        dto.TechnicalSteps.Should().Contain("Conditional Access");

        // Persistido de verdade: uma linha no ledger de advisories do tenant.
        await using var verify = NewContext(TenantA);
        (await verify.RemediationAdvisories.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_CarimbaOTenantAmbiente_FailClosed()
    {
        await using var db = NewContext(TenantA);
        var handler = new GenerateAdvisoryHandler(db, new StubAssessmentService());

        var dto = await handler.HandleAsync(new GenerateAdvisoryCommand("PR.DS-01"));

        // O TenantId não foi atribuído pelo handler — o StampTenant o carimbou no SaveChanges.
        await using var verify = NewContext(null);
        var stored = await verify.RemediationAdvisories.IgnoreQueryFilters().SingleAsync(a => a.Id == dto.Id);
        stored.TenantId.Should().Be(TenantA);
    }

    [Fact]
    public async Task HandleAsync_NaoEnxergaAdvisoryDeOutroTenant()
    {
        await using (var dbA = NewContext(TenantA))
            await new GenerateAdvisoryHandler(dbA, new StubAssessmentService())
                .HandleAsync(new GenerateAdvisoryCommand("PR.AA-01"));
        await using (var dbB = NewContext(TenantB))
            await new GenerateAdvisoryHandler(dbB, new StubAssessmentService())
                .HandleAsync(new GenerateAdvisoryCommand("PR.IR-01"));

        // Sem nenhum .Where(TenantId): o Global Query Filter é quem isola.
        await using var readA = NewContext(TenantA);
        var advisoriesA = await readA.RemediationAdvisories.ToListAsync();
        advisoriesA.Should().ContainSingle().Which.SubcategoryCode.Should().Be("PR.AA-01");
    }

    [Fact]
    public async Task HandleAsync_SemTenantResolvido_LancaFailClosed()
    {
        await using var db = NewContext(null);   // nenhum tenant ambiente
        var handler = new GenerateAdvisoryHandler(db, new StubAssessmentService());

        var act = async () => await handler.HandleAsync(new GenerateAdvisoryCommand("PR.AA-01"));

        // Fail-CLOSED: gravar entidade multi-tenant sem tenant resolvido é violação de invariante, não 201.
        await act.Should().ThrowAsync<TenantSecurityException>();
    }

    // ---- infraestrutura do teste ----------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));
}
