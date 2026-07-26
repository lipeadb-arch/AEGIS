using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;   // reutiliza PostgresProbe (infra AEGIS_TEST_PG já existente)
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-AUD-008] Como este é um controle de segurança multi-tenant de severidade ALTA, a proteção de
/// escrita é validada também contra um PostgreSQL DESCARTÁVEL real — o vetor mais perigoso (UPDATE/DELETE
/// por STUB anexado com Id de outro tenant) tem que provar, no banco de produção, que a linha estrangeira
/// permanece intacta.
///
/// Gated por <c>AEGIS_TEST_PG</c> e reutiliza a <see cref="PostgresProbe"/> já existente (banco descartável
/// por teste; <c>aegis_dev</c> nunca é tocado). Sem a variável, o teste registra a ausência e retorna.
/// </summary>
public sealed class CrossTenantWriteGuardPostgresTests
{
    private readonly ITestOutputHelper _output;

    public CrossTenantWriteGuardPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task StubCrossTenant_UpdateEDelete_Bloqueados_LinhaEstrangeiraIntacta_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }

        var opt = pg.DbOptions();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.EnsureCreatedAsync();

        var tenantB = Guid.NewGuid();
        var tenantA = Guid.NewGuid();

        // Semeia uma linha do tenant B.
        Guid id;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantB)))
        {
            var doc = NewDoc("B-orig");
            db.GovernanceDocuments.Add(doc);
            await db.SaveChangesAsync();
            id = doc.Id;
        }

        // UPDATE cross-tenant por STUB anexado com Id de B e TenantId forjado como A → bloqueado.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var stub = NewDoc("hijack");
            stub.Id = id;
            stub.TenantId = tenantA;
            db.Attach(stub);
            db.Entry(stub).State = EntityState.Modified;
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<TenantSecurityException>();
        }

        // DELETE cross-tenant pelo mesmo tipo de stub → bloqueado.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var stub = NewDoc("x");
            stub.Id = id;
            stub.TenantId = tenantA;
            db.Attach(stub);
            db.Entry(stub).State = EntityState.Deleted;
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<TenantSecurityException>();
        }

        // A linha do tenant B permanece intacta (dono e conteúdo preservados) no PostgreSQL real.
        await using (var verify = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var row = await verify.GovernanceDocuments.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == id);
            row.Should().NotBeNull();
            row!.TenantId.Should().Be(tenantB);
            row.Title.Should().Be("B-orig");
        }
    }

    private static GovernanceDocument NewDoc(string title) => new()
    {
        Title = title,
        Type = GovernanceDocumentType.Politica,
        Source = DocumentSource.UploadManual,
        AnalysisStatus = AiAnalysisStatus.Pending,
        AnalysisQueuedAt = DateTimeOffset.UtcNow,
    };
}
