using AegisScore.Application.Abstractions;
using AegisScore.Application.Documents;
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
/// (banco relacional real: exercita o join probatório, o query filter, a FK de origem e o escritor único).
/// </summary>
public sealed class DocumentEvidenceReconcilerTests : IDisposable
{
    private const int MaxPoints = 20;                 // 50% = 10 exato
    private const string Code = "GV.PO-01";
    private const double Threshold = DocumentEvidencePolicy.MinConfidenceForScore;   // 0.70
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
    public async Task Reconcile_SemEvidenciaElegivelRestante_RetraiEstadoDocumental()
    {
        // Documento vigente sustentava o estado, mas NENHUM mapping probatório sobrou (reanálise a zero).
        var doc = await SeedDocumentAsync();
        await SeedDocumentaryStateAsync(originDoc: doc);
        await SeedCoverageAsync(CoverageStatus.Coberto, CoverageEvidenceSource.Document, originDoc: doc);

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
        // O estado apontava para o documento A; A perdeu a prova (mapping sem trecho) e o documento B ainda
        // sustenta o controle acima do limiar — a reconciliação repointa para B.
        var docA = await SeedDocumentAsync("Doc A", "a.pdf");
        var docB = await SeedDocumentAsync("Doc B", "b.pdf");
        await SeedMappingAsync(docA, evidenceQuote: null);   // A não prova mais nada
        await SeedMappingAsync(docB, evidenceQuote: "A revisão da política é aprovada anualmente pela diretoria.", confidence: 0.8);
        await SeedDocumentaryStateAsync(originDoc: docA);

        await ReconcileAsync();

        await using var assert = NewContext();
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.MitigatedByThirdParty, "evidência documental nunca dá conformidade plena");
        state.CurrentScore.Should().Be(MaxPoints / 2, "crédito documental é 50%");
        state.LastVerdictSource.Should().Be(VerdictSource.Documentary);
        state.OriginDocumentId.Should().Be(docB, "a origem passa a ser o documento que ainda sustenta o controle");
    }

    // ---- Limiar de confiança (0,70) — só evidência elegível altera o score ----------

    [Fact]
    public async Task Reconcile_ConfiancaAbaixoDoLimiar_NaoAltera_Score_MasMantemRastreabilidadeECoberturaParcial()
    {
        // Trecho literal presente, mas confiança 0,69 (< limiar): rastreável + cobertura Parcial, porém
        // ZERO estado no ledger — o score permanece Não avaliado.
        var doc = await SeedDocumentAsync();
        await SeedMappingAsync(doc, evidenceQuote: "Trecho probatório literal sobre a política de segurança.", confidence: 0.69);

        await ReconcileAsync();

        await using var assert = NewContext();
        (await assert.TenantControlStates.AnyAsync())
            .Should().BeFalse("confiança abaixo do limiar não cria crédito no score (Não avaliado)");
        (await assert.DocumentControlMappings.AnyAsync(m => m.EvidenceQuote != null))
            .Should().BeTrue("o mapping com trecho literal permanece para RASTREABILIDADE");
        var cov = await assert.SubcategoryCoverages.SingleAsync();
        cov.Status.Should().Be(CoverageStatus.Parcial, "abaixo do limiar a cobertura é Parcial");
        cov.EvidenceSource.Should().Be(CoverageEvidenceSource.Document);
    }

    [Fact]
    public async Task Reconcile_ConfiancaNoLimiar_ConcedeCreditoParcial()
    {
        var doc = await SeedDocumentAsync();
        await SeedMappingAsync(doc, evidenceQuote: "Trecho probatório literal sobre a política de segurança.", confidence: Threshold);

        await ReconcileAsync();

        await using var assert = NewContext();
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.MitigatedByThirdParty);
        state.CurrentScore.Should().Be(MaxPoints / 2, "no limiar (0,70) a evidência documental concede crédito parcial (50%)");
        state.OriginDocumentId.Should().Be(doc);
        (await assert.SubcategoryCoverages.SingleAsync()).Status.Should().Be(CoverageStatus.Coberto);
    }

    [Fact]
    public async Task Reconcile_QuedaAbaixoDoLimiar_NaoPreservaCreditoAnterior()
    {
        // Estado documental vigente (50%); a reanálise rebaixa a confiança para 0,69 — o crédito NÃO é
        // preservado: abaixo do limiar retrai.
        var doc = await SeedDocumentAsync();
        await SeedMappingAsync(doc, evidenceQuote: "Trecho probatório literal sobre a política de segurança.", confidence: 0.69);
        await SeedDocumentaryStateAsync(originDoc: doc);   // resquício de quando pontuava

        await ReconcileAsync();

        await using var assert = NewContext();
        (await assert.TenantControlStates.AnyAsync())
            .Should().BeFalse("confiança abaixo do limiar não PRESERVA crédito no score");
    }

    // ---- Telemetria é preservada integralmente --------------------------------------

    [Fact]
    public async Task Reconcile_EstadoDeTelemetria_EhPreservadoIntegralmente()
    {
        await SeedTelemetryCompliantAsync();   // Compliant, 100%, fonte Telemetry, sem origem documental

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
            originDoc: null, originInterview: interview);

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

    // ---- Atomicidade: a reconciliação participa da transação do chamador -------------

    [Fact]
    public async Task Reconcile_DentroDeTransacaoRevertida_NaoPersisteNada()
    {
        var doc = await SeedDocumentAsync();
        await SeedMappingAsync(doc, evidenceQuote: "Trecho probatório literal sobre a política de segurança.", confidence: 0.8);
        await SeedDocumentaryStateAsync(originDoc: doc);

        // Simula o meio de uma operação (worker/exclusão): remove o mapping e reconcilia (que retrairia o
        // estado) DENTRO de uma transação; então faz ROLLBACK, como se algo falhasse depois.
        await using (var db = NewContext())
        {
            var tenantCtx = new SystemTenantContext(Tenant);
            IControlStateWriter writer = new ControlStateWriter(db, tenantCtx, NullLogger<ControlStateWriter>.Instance);
            var reconciler = new DocumentEvidenceReconciler(db, tenantCtx, writer, NullLogger<DocumentEvidenceReconciler>.Instance);

            await using var tx = await db.Database.BeginTransactionAsync();
            var mappings = await db.DocumentControlMappings.ToListAsync();
            db.DocumentControlMappings.RemoveRange(mappings);
            await db.SaveChangesAsync();
            await reconciler.ReconcileAsync(Tenant, new[] { Code });
            await tx.RollbackAsync();   // falha simulada após a reconciliação
        }

        // Rollback integral: mapping E estado permanecem — nada de estado parcialmente atualizado.
        await using var assert = NewContext();
        (await assert.DocumentControlMappings.CountAsync()).Should().Be(1, "o rollback restaura o mapping removido");
        var state = await assert.TenantControlStates.SingleAsync();
        state.OriginDocumentId.Should().Be(doc, "o rollback preserva o estado documental que a reconciliação teria retraído");
    }

    // ---- Invariante de FK: OriginDocumentId nunca aponta para documento inexistente --

    [Fact]
    public async Task TenantControlState_ComOriginDocumentIdInexistente_ViolaFK()
    {
        await using var db = NewContext();
        var subId = await db.Subcategories.Where(s => s.Code == Code).Select(s => s.Id).SingleAsync();
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = subId,
            Status = ControlStatus.MitigatedByThirdParty,
            CurrentScore = MaxPoints / 2,
            LastVerdictSource = VerdictSource.Documentary,
            OriginDocumentId = Guid.NewGuid(),   // documento inexistente
        });

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("a FK Restrict impede origem documental pendurada");
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

    private async Task<Guid> SeedDocumentAsync(string title = "Política de Segurança", string fileName = "psi.pdf")
    {
        await using var db = NewContext();
        var doc = new GovernanceDocument { Title = title, FileName = fileName };
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
