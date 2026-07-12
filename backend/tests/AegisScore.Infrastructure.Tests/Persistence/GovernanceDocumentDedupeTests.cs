using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// Dedupe de documentos de governança NO NÍVEL DO BANCO. O índice ÚNICO parcial (TenantId, Sha256)
/// WHERE Sha256 IS NOT NULL é a invariante que torna idempotente a corrida read-then-write dos dois
/// caminhos de ingestão (Upload manual e PolicyIngestionWorker.SyncTenantAsync): mesmo que duas
/// gravações concorrentes passem AMBAS pelo AnyAsync antes de qualquer commit, o banco REJEITA a
/// segunda com <see cref="DbUpdateException"/> — é isso que dispensou o SemaphoreSlim paliativo do worker.
///
/// Roda sobre SQLite in-memory (relacional real: exercita o índice único e o filtro parcial de verdade,
/// sem PostgreSQL). <c>EnsureCreated</c> materializa o índice a partir do modelo, não das migrations.
/// </summary>
public sealed class GovernanceDocumentDedupeTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;

    public GovernanceDocumentDedupeTests()
    {
        // Banco in-memory vive enquanto a conexão estiver aberta; xUnit instancia a classe por caso de
        // teste, então cada teste recebe um banco limpo e isolado.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task CorridaReadThenWrite_MesmoHash_SegundaGravacaoRejeitadaEViraIdempotente()
    {
        const string sha = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";

        await using var ctxA = NewContext(TenantA);
        await using var ctxB = NewContext(TenantA);

        // A CORRIDA: os dois fluxos leem ANTES de qualquer commit — nenhum vê o documento e ambos decidem inserir.
        (await ctxA.GovernanceDocuments.AnyAsync(d => d.Sha256 == sha)).Should().BeFalse();
        (await ctxB.GovernanceDocuments.AnyAsync(d => d.Sha256 == sha)).Should().BeFalse();

        // O primeiro fluxo grava e vence.
        ctxA.GovernanceDocuments.Add(NewDoc(sha));
        await ctxA.SaveChangesAsync();

        // O segundo tenta gravar o MESMO (TenantId, Sha256) → o índice único o rejeita fisicamente.
        ctxB.GovernanceDocuments.Add(NewDoc(sha));
        var gravarDuplicata = async () => await ctxB.SaveChangesAsync();
        await gravarDuplicata.Should().ThrowAsync<DbUpdateException>();

        // Idempotência imposta pelo banco: sobra EXATAMENTE um documento com aquele hash.
        await using var verify = NewContext(TenantA);
        (await verify.GovernanceDocuments.CountAsync(d => d.Sha256 == sha)).Should().Be(1);
    }

    [Fact]
    public async Task IndiceParcial_PermiteVariosDocumentosSemHash_NoMesmoTenant()
    {
        // O caminho /connect registra o documento ANTES de anexar o binário (Sha256 == null). O filtro
        // parcial (WHERE Sha256 IS NOT NULL) mantém esses registros fora da unicidade — vários convivem.
        await using var db = NewContext(TenantA);
        db.GovernanceDocuments.Add(NewDoc(null, "Doc sem binário 1"));
        db.GovernanceDocuments.Add(NewDoc(null, "Doc sem binário 2"));

        var gravar = async () => await db.SaveChangesAsync();
        await gravar.Should().NotThrowAsync();

        (await db.GovernanceDocuments.CountAsync(d => d.Sha256 == null)).Should().Be(2);
    }

    [Fact]
    public async Task IndiceTenantLeading_PermiteMesmoHashEmTenantsDiferentes()
    {
        const string sha = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef0";

        await using (var dbA = NewContext(TenantA))
        {
            dbA.GovernanceDocuments.Add(NewDoc(sha));
            await dbA.SaveChangesAsync();
        }

        // Mesmo conteúdo, OUTRO tenant: a unicidade é (TenantId, Sha256), então NÃO colide — isolamento preservado.
        await using var dbB = NewContext(TenantB);
        dbB.GovernanceDocuments.Add(NewDoc(sha));
        var gravar = async () => await dbB.SaveChangesAsync();
        await gravar.Should().NotThrowAsync();

        await using var verify = NewContext(null);
        (await verify.GovernanceDocuments.IgnoreQueryFilters().CountAsync(d => d.Sha256 == sha)).Should().Be(2);
    }

    // ---- infraestrutura do teste ----------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    /// <summary>Documento válido mínimo; sem TenantId — carimbado no SaveChanges (fail-closed), como no worker/controller.</summary>
    private static GovernanceDocument NewDoc(string? sha, string title = "Política de Segurança") => new()
    {
        Title = title,
        Type = GovernanceDocumentType.Politica,
        Source = DocumentSource.Integracao,
        Sha256 = sha,
        AnalysisStatus = AiAnalysisStatus.Queued,
        AnalysisQueuedAt = DateTimeOffset.UtcNow,
    };
}
