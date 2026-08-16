using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Documents;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Documents;

/// <summary>
/// Testes da rotina ÚNICA de reconciliação documental (<see cref="DocumentEvidenceReconciler"/>) — a que
/// exclusão e reanálise usam para RETRAIR/RECALCULAR sem deixar ledger e cobertura órfãos. SQLite in-memory
/// (banco relacional real: exercita o join probatório, o query filter e o escritor único de verdade).
/// </summary>
public sealed class DocumentEvidenceReconcilerTests : IDisposable
{
    private const int MaxPoints = 20;                 // 50% = 10 exato
    private const string Code = "GV.PO-01";
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;

    public DocumentEvidenceReconcilerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
        SeedCatalog(ctx);
    }

    public void Dispose() => _connection.Dispose();

    // ---- Retração: exclusão/reanálise sem evidência restante -------------------------

    [Fact]
    public async Task Reconcile_SemEvidenciaDocumentalRestante_RetraiEstadoDocumental()
    {
        // Estado documental vigente, mas NENHUM mapping probatório sobrou (documento excluído/reanalisado a zero).
        await SeedDocumentaryStateAsync(originDoc: Guid.NewGuid());
        await SeedCoverageAsync(CoverageStatus.Coberto, CoverageEvidenceSource.Document, originDoc: Guid.NewGuid());

        await ReconcileAsync();

        await using var assert = NewContext();
        (await assert.TenantControlStates.AnyAsync()).Should().BeFalse("sem prova documental, o estado é retraído → Não avaliado");
        (await assert.SubcategoryCoverages.AnyAsync()).Should().BeFalse("cobertura exclusivamente documental sem documento válido desaparece");
    }

    [Fact]
    public async Task Reconcile_ReanaliseQuePassaAZeroEvidencias_RetraiEstado()
    {
        // Reanálise: o documento AINDA existe, mas deixou de ter mapping PROBATÓRIO (perdeu o trecho literal).
        var doc = await SeedDocumentAsync();
        await SeedMappingAsync(doc, evidenceQuote: null);   // mapping legado/não probatório: ignorado
        await SeedDocumentaryStateAsync(originDoc: doc);

        await ReconcileAsync();

        await using var assert = NewContext();
        (await assert.TenantControlStates.AnyAsync())
            .Should().BeFalse("mapping sem trecho literal não sustenta o controle — retrai");
    }

    // ---- Recálculo: outro documento válido sustenta ---------------------------------

    [Fact]
    public async Task Reconcile_ComOutroDocumentoValido_RecalculaComEsseDocumento()
    {
        var deleted = Guid.NewGuid();
        var surviving = await SeedDocumentAsync();
        await SeedMappingAsync(surviving, evidenceQuote: "A revisão da política é aprovada anualmente pela diretoria.", confidence: 0.8);
        await SeedDocumentaryStateAsync(originDoc: deleted);   // origem antiga (documento excluído)

        await ReconcileAsync();

        await using var assert = NewContext();
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.MitigatedByThirdParty, "evidência documental nunca dá conformidade plena");
        state.CurrentScore.Should().Be(MaxPoints / 2, "crédito documental é 50%");
        state.LastVerdictSource.Should().Be(VerdictSource.Documentary);
        state.OriginDocumentId.Should().Be(surviving, "a origem passa a ser o documento que ainda sustenta o controle");
    }

    // ---- Telemetria é preservada integralmente --------------------------------------

    [Fact]
    public async Task Reconcile_EstadoDeTelemetria_EhPreservadoIntegralmente()
    {
        await SeedTelemetryCompliantAsync();   // Compliant, 100%, fonte Telemetry, sem mapping documental

        await ReconcileAsync();

        await using var assert = NewContext();
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.Compliant, "documento jamais retrai nem rebaixa telemetria");
        state.CurrentScore.Should().Be(MaxPoints);
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry);
    }

    // ---- Cobertura: Both sem documento volta para Interview (entrevista intacta) -----

    [Fact]
    public async Task Reconcile_CoberturaBothSemDocumento_VoltaParaInterview()
    {
        var interview = Guid.NewGuid();
        await SeedCoverageAsync(CoverageStatus.Coberto, CoverageEvidenceSource.Both,
            originDoc: Guid.NewGuid(), originInterview: interview);

        await ReconcileAsync();

        await using var assert = NewContext();
        var cov = await assert.SubcategoryCoverages.SingleAsync();
        cov.EvidenceSource.Should().Be(CoverageEvidenceSource.Interview, "Both sem documento volta para Interview");
        cov.OriginDocumentId.Should().BeNull("a parte documental foi descartada");
        cov.OriginInterviewSessionId.Should().Be(interview, "a evidência de entrevista nunca é apagada");
    }

    [Fact]
    public async Task Reconcile_ComDocumentoValidoEEntrevista_MarcaBoth()
    {
        var interview = Guid.NewGuid();
        var doc = await SeedDocumentAsync();
        await SeedMappingAsync(doc, evidenceQuote: "O acesso privilegiado é revisado trimestralmente com registro em ata.", confidence: 0.9);
        await SeedCoverageAsync(CoverageStatus.Parcial, CoverageEvidenceSource.Interview, originInterview: interview);

        await ReconcileAsync();

        await using var assert = NewContext();
        var cov = await assert.SubcategoryCoverages.SingleAsync();
        cov.EvidenceSource.Should().Be(CoverageEvidenceSource.Both);
        cov.Status.Should().Be(CoverageStatus.Coberto, "documento com confiança alta eleva a cobertura, sem rebaixar a entrevista");
        cov.OriginInterviewSessionId.Should().Be(interview);
    }

    // ---- infraestrutura do teste ----------------------------------------------------

    private async Task ReconcileAsync()
    {
        await using var db = NewContext();
        var tenantCtx = new SystemTenantContext(Tenant);
        IControlStateWriter writer = new ControlStateWriter(db, tenantCtx, NullLogger<ControlStateWriter>.Instance);
        var reconciler = new DocumentEvidenceReconciler(
            db, tenantCtx, writer, NullLogger<DocumentEvidenceReconciler>.Instance);
        await reconciler.ReconcileAsync(Tenant, new[] { Code });
    }

    private async Task<Guid> SeedDocumentAsync()
    {
        await using var db = NewContext();
        var doc = new GovernanceDocument { Title = "Política de Segurança", FileName = "psi.pdf" };
        db.GovernanceDocuments.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
    }

    private async Task SeedMappingAsync(Guid documentId, string? evidenceQuote, double confidence = 0.8)
    {
        await using var db = NewContext();
        db.DocumentControlMappings.Add(new DocumentControlMapping
        {
            GovernanceDocumentId = documentId,
            SubcategoryCode = Code,
            Confidence = confidence,
            EvidenceQuote = evidenceQuote,
            Evidence = "racional da análise",
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedDocumentaryStateAsync(Guid originDoc)
    {
        await using var db = NewContext();
        var subId = await db.Subcategories.Where(s => s.Code == Code).Select(s => s.Id).SingleAsync();
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = subId,
            Status = ControlStatus.MitigatedByThirdParty,
            CurrentScore = MaxPoints / 2,
            LastVerdictSource = VerdictSource.Documentary,
            OriginDocumentId = originDoc,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedTelemetryCompliantAsync()
    {
        await using var db = NewContext();
        var subId = await db.Subcategories.Where(s => s.Code == Code).Select(s => s.Id).SingleAsync();
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = subId,
            Status = ControlStatus.Compliant,
            CurrentScore = MaxPoints,
            LastVerdictSource = VerdictSource.Telemetry,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedCoverageAsync(
        CoverageStatus status, CoverageEvidenceSource source, Guid? originDoc = null, Guid? originInterview = null)
    {
        await using var db = NewContext();
        db.SubcategoryCoverages.Add(new SubcategoryCoverage
        {
            SubcategoryCode = Code,
            Status = status,
            EvidenceSource = source,
            OriginDocumentId = originDoc,
            OriginInterviewSessionId = originInterview,
        });
        await db.SaveChangesAsync();
    }

    private AegisScoreDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(Tenant));

    private static void SeedCatalog(AegisScoreDbContext ctx)
    {
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };
        var fn = new NistFunction { Code = "GV", Name = "GOVERN" };
        var cat = new NistCategory { Code = "GV.PO", Name = "Policy" };
        cat.Subcategories.Add(new NistSubcategory
        {
            Code = Code,
            Description = "Organizational cybersecurity policy is established.",
            MaxScorePoints = MaxPoints,
        });
        fn.Categories.Add(cat);
        fv.Functions.Add(fn);
        ctx.FrameworkVersions.Add(fv);
        ctx.SaveChanges();
    }
}
