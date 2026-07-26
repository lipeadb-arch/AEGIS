using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-AUD-008] Proteção CENTRAL de escrita multi-tenant do <see cref="AegisScoreDbContext"/>.
///
/// Query filters isolam LEITURAS, mas UPDATE/DELETE de entidades rastreadas são emitidos pela chave
/// primária e não passam pelo filtro. O guard no SaveChanges valida, contra a linha AUTORITATIVA no banco,
/// que toda escrita de uma <see cref="ITenantOwned"/> pertence ao tenant ambiente — fail-closed para
/// Added, Modified e Deleted, incluindo o vetor do STUB anexado com Id de outro tenant e TenantId forjado.
///
/// Roda sobre SQLite relacional real (in-memory): exercita o UPDATE/DELETE por chave e o <c>GetDatabaseValues</c>
/// de verdade, sem PostgreSQL. A cobertura equivalente contra PostgreSQL descartável vive em
/// <c>CrossTenantWriteGuardPostgresTests</c>.
/// </summary>
public sealed class CrossTenantWriteGuardTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection;

    public CrossTenantWriteGuardTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // 1) Insert do tenant atual continua funcionando (e é carimbado).
    [Fact]
    public async Task Insert_NoTenantAtual_Funciona_ECarimba()
    {
        await using var db = NewContext(TenantA);
        var doc = NewDoc("ok");
        db.GovernanceDocuments.Add(doc);
        await db.SaveChangesAsync();
        doc.TenantId.Should().Be(TenantA);
    }

    // 2) Insert com TenantId divergente falha.
    [Fact]
    public async Task Insert_ComTenantIdDivergente_Falha()
    {
        await using var db = NewContext(TenantA);
        db.GovernanceDocuments.Add(NewDoc("x", TenantB));
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<TenantSecurityException>();
    }

    // 3) Update legítimo no tenant atual funciona.
    [Fact]
    public async Task Update_LegitimoNoTenantAtual_Funciona()
    {
        var id = await SeedDocAsync(TenantA, "orig");
        await using (var db = NewContext(TenantA))
        {
            var doc = await db.GovernanceDocuments.FirstAsync(d => d.Id == id);
            doc.Title = "novo";
            await db.SaveChangesAsync();
        }
        await using var verify = NewContext(TenantA);
        (await verify.GovernanceDocuments.FirstAsync(d => d.Id == id)).Title.Should().Be("novo");
    }

    // 4) Delete legítimo no tenant atual funciona.
    [Fact]
    public async Task Delete_LegitimoNoTenantAtual_Funciona()
    {
        var id = await SeedDocAsync(TenantA);
        await using (var db = NewContext(TenantA))
        {
            var doc = await db.GovernanceDocuments.FirstAsync(d => d.Id == id);
            db.GovernanceDocuments.Remove(doc);
            await db.SaveChangesAsync();
        }
        await using var verify = NewContext(null);
        (await verify.GovernanceDocuments.IgnoreQueryFilters().AnyAsync(d => d.Id == id)).Should().BeFalse();
    }

    // 5) Entidade do tenant B carregada com IgnoreQueryFilters sob contexto A não pode ser MODIFICADA.
    [Fact]
    public async Task Modify_EntidadeDeB_CarregadaComIgnoreQueryFilters_SobA_Falha()
    {
        var id = await SeedDocAsync(TenantB, "B-orig");
        await using var db = NewContext(TenantA);
        var doc = await db.GovernanceDocuments.IgnoreQueryFilters().FirstAsync(d => d.Id == id);
        doc.Title = "hijack";
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<TenantSecurityException>();
        await AssertBRowIntactAsync(id, "B-orig");
    }

    // 6) Entidade do tenant B carregada com IgnoreQueryFilters sob contexto A não pode ser REMOVIDA.
    [Fact]
    public async Task Delete_EntidadeDeB_CarregadaComIgnoreQueryFilters_SobA_Falha()
    {
        var id = await SeedDocAsync(TenantB, "B-orig");
        await using var db = NewContext(TenantA);
        var doc = await db.GovernanceDocuments.IgnoreQueryFilters().FirstAsync(d => d.Id == id);
        db.GovernanceDocuments.Remove(doc);
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<TenantSecurityException>();
        await AssertBRowIntactAsync(id, "B-orig");
    }

    // 7) Tentativa de alterar o TenantId de A para B falha.
    [Fact]
    public async Task Update_QueTrocaTenantId_DeAParaB_Falha()
    {
        var id = await SeedDocAsync(TenantA, "A-orig");
        await using var db = NewContext(TenantA);
        var doc = await db.GovernanceDocuments.FirstAsync(d => d.Id == id);
        doc.TenantId = TenantB;
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<TenantSecurityException>();

        await using var verify = NewContext(null);
        (await verify.GovernanceDocuments.IgnoreQueryFilters().FirstAsync(d => d.Id == id))
            .TenantId.Should().Be(TenantA);
    }

    // 8) Stub anexado com Id do tenant B e TenantId falsificado como A NÃO consegue atualizar a linha.
    [Fact]
    public async Task Update_StubComIdDeB_ETenantIdFalsificadoA_NaoAtualiza()
    {
        var id = await SeedDocAsync(TenantB, "B-orig");
        await using var db = NewContext(TenantA);
        var stub = NewDoc("hijack", TenantA);
        stub.Id = id;   // ID pertence ao tenant B; TenantId forjado como A
        db.Attach(stub);
        db.Entry(stub).State = EntityState.Modified;
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<TenantSecurityException>();
        await AssertBRowIntactAsync(id, "B-orig");
    }

    // 9) O mesmo stub NÃO consegue excluir a linha.
    [Fact]
    public async Task Delete_StubComIdDeB_ETenantIdFalsificadoA_NaoRemove()
    {
        var id = await SeedDocAsync(TenantB, "B-orig");
        await using var db = NewContext(TenantA);
        var stub = NewDoc("x", TenantA);
        stub.Id = id;
        db.Attach(stub);
        db.Entry(stub).State = EntityState.Deleted;
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<TenantSecurityException>();
        await AssertBRowIntactAsync(id, "B-orig");
    }

    // 10) Contexto SEM tenant não modifica nem remove entidade tenant-owned.
    [Fact]
    public async Task SemTenant_NaoModificaNemRemove()
    {
        var id = await SeedDocAsync(TenantA, "A-orig");

        await using (var db = NewContext(null))
        {
            var doc = await db.GovernanceDocuments.IgnoreQueryFilters().FirstAsync(d => d.Id == id);
            doc.Title = "x";
            var modify = async () => await db.SaveChangesAsync();
            await modify.Should().ThrowAsync<TenantSecurityException>();
        }
        await using (var db = NewContext(null))
        {
            var doc = await db.GovernanceDocuments.IgnoreQueryFilters().FirstAsync(d => d.Id == id);
            db.GovernanceDocuments.Remove(doc);
            var delete = async () => await db.SaveChangesAsync();
            await delete.Should().ThrowAsync<TenantSecurityException>();
        }
    }

    // 11) Guid.Empty não modifica nem remove.
    [Fact]
    public async Task GuidEmpty_NaoModificaNemRemove()
    {
        var id = await SeedDocAsync(TenantA, "A-orig");

        await using (var db = NewContext(Guid.Empty))
        {
            var doc = await db.GovernanceDocuments.IgnoreQueryFilters().FirstAsync(d => d.Id == id);
            doc.Title = "x";
            var modify = async () => await db.SaveChangesAsync();
            await modify.Should().ThrowAsync<TenantSecurityException>();
        }
        await using (var db = NewContext(Guid.Empty))
        {
            var doc = await db.GovernanceDocuments.IgnoreQueryFilters().FirstAsync(d => d.Id == id);
            db.GovernanceDocuments.Remove(doc);
            var delete = async () => await db.SaveChangesAsync();
            await delete.Should().ThrowAsync<TenantSecurityException>();
        }
    }

    // 12) Entidade GLOBAL (não ITenantOwned) continua podendo ser gravada e alterada SEM tenant ambiente.
    [Fact]
    public async Task EntidadeGlobal_SemTenant_PodeSerGravadaEAlterada()
    {
        Guid accId;
        await using (var db = NewContext(null))
        {
            var acc = new IdentityAccount { Email = "user@example.com", PasswordHash = "hash1" };
            db.IdentityAccounts.Add(acc);
            await db.SaveChangesAsync();   // insert global sem tenant → ok
            accId = acc.Id;
        }
        await using (var db = NewContext(null))
        {
            var acc = await db.IdentityAccounts.FirstAsync(a => a.Id == accId);
            acc.PasswordHash = "hash2";
            var act = async () => await db.SaveChangesAsync();   // update global sem tenant → ok
            await act.Should().NotThrowAsync();
        }
    }

    // 13) Overloads síncronos e assíncronos, inclusive os que recebem acceptAllChangesOnSuccess, não dão bypass.
    [Fact]
    public async Task Overloads_ComAcceptAllChangesOnSuccess_NaoOferecemBypass()
    {
        var id = await SeedDocAsync(TenantB, "B-orig");

        // SaveChanges(bool) SÍNCRONO — insert com TenantId divergente.
        await using (var db = NewContext(TenantA))
        {
            db.GovernanceDocuments.Add(NewDoc("x", TenantB));
            Action act = () => db.SaveChanges(acceptAllChangesOnSuccess: false);
            act.Should().Throw<TenantSecurityException>();
        }

        // SaveChangesAsync(bool, ct) ASSÍNCRONO — update cross-tenant por stub anexado.
        await using (var db = NewContext(TenantA))
        {
            var stub = NewDoc("hijack", TenantA);
            stub.Id = id;
            db.Attach(stub);
            db.Entry(stub).State = EntityState.Modified;
            var act = async () => await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, CancellationToken.None);
            await act.Should().ThrowAsync<TenantSecurityException>();
        }

        // 14) Após as tentativas rejeitadas, a linha do tenant B permanece logicamente inalterada.
        await AssertBRowIntactAsync(id, "B-orig");
    }

    // ---- infraestrutura do teste ----------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private static GovernanceDocument NewDoc(string title = "doc", Guid tenantId = default) => new()
    {
        Title = title,
        Type = GovernanceDocumentType.Politica,
        Source = DocumentSource.UploadManual,
        AnalysisStatus = AiAnalysisStatus.Pending,
        AnalysisQueuedAt = DateTimeOffset.UtcNow,
        TenantId = tenantId,   // default = Guid.Empty (carimbado no save), ou explícito p/ os testes de forja
    };

    private async Task<Guid> SeedDocAsync(Guid tenant, string title = "seed")
    {
        await using var db = NewContext(tenant);
        var doc = NewDoc(title);
        db.GovernanceDocuments.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
    }

    /// <summary>Prova, sob contexto neutro (sem filtro), que a linha do tenant B sobreviveu byte a byte à tentativa.</summary>
    private async Task AssertBRowIntactAsync(Guid id, string expectedTitle)
    {
        await using var verify = NewContext(null);
        var row = await verify.GovernanceDocuments.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == id);
        row.Should().NotBeNull();
        row!.TenantId.Should().Be(TenantB);
        row.Title.Should().Be(expectedTitle);
    }
}
