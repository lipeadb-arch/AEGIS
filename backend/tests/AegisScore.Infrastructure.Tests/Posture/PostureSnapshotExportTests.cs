using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AegisScore.Api.Controllers;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Posture.Export;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Posture;

/// <summary>
/// Testes FOCADOS da exportação executiva (AEGIS-AUD-034): PDF e CSV derivados EXCLUSIVAMENTE da fotografia
/// imutável selecionada pelo id (autoridade única). Cobrem: PDF válido com título/id/hash/números autoritativos;
/// CSV com BOM/cabeçalho/delimitador; proteção contra CSV/Formula Injection; score ausente = "Não avaliado" (não
/// 0%); exportação AEGIS e KNIGHT; isolamento por tenant (404); hash inválido (409); formato inválido (400); e
/// EQUIVALÊNCIA entre detalhe, PDF e CSV (mesma fotografia, hash e números).
/// </summary>
public sealed class PostureSnapshotExportTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaa1111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("bbbb2222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;

    public PostureSnapshotExportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- 1) PDF: começa com %PDF- e contém título, Snapshot ID, hash e números autoritativos --------

    [Fact]
    public async Task Pdf_Aegis_StartsWithMagic_AndContainsTitleIdHashAndAuthoritativeNumbers()
    {
        await using var db = NewContext(TenantA);
        await SeedDefaultLedgerAsync(db);
        var published = await ServiceFor(db).PublishAsync(PostureSnapshotType.AegisScoreNist, null);

        var pdf = await ExporterFor(db).ExportAsync(published.Summary.Id, PostureExportFormat.Pdf);

        pdf!.ContentType.Should().Be("application/pdf");
        pdf.FileName.Should().EndWith(".pdf");
        StartsWithPdfMagic(pdf.Content).Should().BeTrue("um PDF válido começa com %PDF-");

        var text = ExtractPdfText(pdf.Content);
        text.Should().Contain("Executivo").And.Contain("Postura", "o título executivo aparece no relatório");
        text.Should().Contain(published.Summary.Id.ToString("D"), "o Snapshot ID é a autoridade do relatório");
        text.Should().Contain(published.Summary.ContentHash, "o hash SHA-256 completo é impresso");
        text.Should().Contain("66,7", "o score autoritativo (66,7%) vem da fotografia, não recalculado");
        text.Should().Contain("PR.AA-01", "os controles congelados aparecem em ordem estável");
    }

    // ---- 2) CSV: BOM + cabeçalho + delimitador ';' -------------------------------------------------

    [Fact]
    public async Task Csv_Aegis_HasBom_Header_AndSemicolonDelimiter()
    {
        await using var db = NewContext(TenantA);
        await SeedDefaultLedgerAsync(db);
        var published = await ServiceFor(db).PublishAsync(PostureSnapshotType.AegisScoreNist, null);

        var csv = await ExporterFor(db).ExportAsync(published.Summary.Id, PostureExportFormat.Csv);

        csv!.ContentType.Should().Be("text/csv; charset=utf-8");
        csv.FileName.Should().EndWith(".csv");
        HasUtf8Bom(csv.Content).Should().BeTrue("o CSV é UTF-8 COM BOM");

        var (header, rows) = ParseCsv(csv.Content);
        header.Should().StartWith(new[] { "SnapshotId", "ContentHash", "Type", "CapturedAt" });
        header.Should().Contain(new[] { "SubcategoryCode", "Status", "AchievedPoints", "MaxPoints", "EvidenceRefs" });
        Encoding.UTF8.GetString(csv.Content).Should().Contain(";", "o delimitador é ponto-e-vírgula");
        rows.Should().HaveCount(3, "uma linha por controle do catálogo ATIVO completo");
    }

    // ---- 3) Proteção contra CSV/Formula Injection --------------------------------------------------

    [Fact]
    public async Task Csv_Knight_NeutralizesFormulaInjection_InTextCells()
    {
        await using var db = NewContext(TenantA);
        await SeedKnightRunAsync(db, maliciousTitle: "=SUM(1;2)", maliciousEvidence: "@cmd /c calc");
        var published = await ServiceFor(db).PublishAsync(PostureSnapshotType.Knight, null);

        var csv = await ExporterFor(db).ExportAsync(published.Summary.Id, PostureExportFormat.Csv);

        var (header, rows) = ParseCsv(csv!.Content);
        var titleIdx = Array.IndexOf(header, "Title");
        var evidenceIdx = Array.IndexOf(header, "Evidence");
        var malicious = rows.Single(r => r[Array.IndexOf(header, "IndicatorId")] == "AK-ENTRA-001");

        malicious[titleIdx].Should().StartWith("'=", "um texto iniciado por '=' é neutralizado com prefixo '");
        malicious[evidenceIdx].Should().StartWith("'@", "um texto iniciado por '@' é neutralizado com prefixo '");
    }

    // ---- 4) Score ausente = "Não avaliado", nunca 0% ----------------------------------------------

    [Fact]
    public async Task Export_Aegis_MissingScore_ShowsNotEvaluated_NotZeroPercent()
    {
        await using var db = NewContext(TenantA);
        await SeedActiveFrameworkAsync(db);   // catálogo ativo, ledger vazio → score anulável
        var published = await ServiceFor(db).PublishAsync(PostureSnapshotType.AegisScoreNist, null);
        published.Summary.Score.Should().BeNull("pré-condição: sem controle avaliado, o score é anulável");

        var pdf = await ExporterFor(db).ExportAsync(published.Summary.Id, PostureExportFormat.Pdf);
        var pdfText = Deaccent(ExtractPdfText(pdf!.Content));
        pdfText.Should().Contain("Nao avaliado", "o score ausente é rotulado 'Não avaliado'");
        // O resumo executivo confirma o score não avaliado (a cobertura PODE ser 0%, mas o score nunca vira 0%).
        pdfText.Should().Contain("score nao avaliado", "o resumo executivo diz 'score não avaliado', não 0%");
        pdfText.Should().NotContain("score de 0", "o score ausente NUNCA é apresentado como 0%");

        var csv = await ExporterFor(db).ExportAsync(published.Summary.Id, PostureExportFormat.Csv);
        var (header, rows) = ParseCsv(csv!.Content);
        var stateIdx = Array.IndexOf(header, "EvaluationState");
        var scoreIdx = Array.IndexOf(header, "Score");
        rows.Should().OnlyContain(r => r[stateIdx] == "NotEvaluated");
        rows.Should().OnlyContain(r => r[scoreIdx].Length == 0, "score ausente é célula vazia, não '0'");
    }

    // ---- 5) Exportação AEGIS Score/NIST: PDF e CSV com conteúdo determinístico ----------------------

    [Fact]
    public async Task Export_Aegis_ProducesBothFormats_WithControls()
    {
        await using var db = NewContext(TenantA);
        await SeedDefaultLedgerAsync(db);
        var published = await ServiceFor(db).PublishAsync(PostureSnapshotType.AegisScoreNist, null);

        var pdf = await ExporterFor(db).ExportAsync(published.Summary.Id, PostureExportFormat.Pdf);
        var csv = await ExporterFor(db).ExportAsync(published.Summary.Id, PostureExportFormat.Csv);

        pdf!.Content.Length.Should().BeGreaterThan(1000, "o PDF executivo não é trivial");
        var pdfText = ExtractPdfText(pdf.Content);
        pdfText.Should().Contain("NIST", "o instrumento AEGIS Score/NIST é identificado");
        pdfText.Should().Contain("aegis-score-v1", "a versão da fórmula autoritativa aparece");

        var (header, rows) = ParseCsv(csv!.Content);
        rows.Should().HaveCount(3);
        rows.Select(r => r[Array.IndexOf(header, "SubcategoryCode")])
            .Should().BeEquivalentTo(new[] { "PR.AA-01", "PR.AA-02", "PR.AA-03" });
    }

    // ---- 6) Exportação KNIGHT: fonte, indicadores e mapeamentos ------------------------------------

    [Fact]
    public async Task Export_Knight_ProducesBothFormats_WithIndicatorsAndSource()
    {
        await using var db = NewContext(TenantA);
        await SeedKnightRunAsync(db);
        var published = await ServiceFor(db).PublishAsync(PostureSnapshotType.Knight, null);

        var pdf = await ExporterFor(db).ExportAsync(published.Summary.Id, PostureExportFormat.Pdf);
        var csv = await ExporterFor(db).ExportAsync(published.Summary.Id, PostureExportFormat.Csv);

        var pdfText = ExtractPdfText(pdf!.Content);
        pdfText.Should().Contain("KNIGHT", "o instrumento KNIGHT é identificado");
        pdfText.Should().Contain("AK-ENTRA-001").And.Contain("AK-ENTRA-003", "os indicadores congelados aparecem");

        var (header, rows) = ParseCsv(csv!.Content);
        header.Should().Contain(new[] { "IndicatorId", "Severity", "Status", "NistCodes", "MitreTechniques", "Evidence" });
        rows.Should().HaveCount(2);
        rows.Select(r => r[Array.IndexOf(header, "SourceType")]).Should().OnlyContain(v => v == "MicrosoftEntraId");
    }

    // ---- 7) Isolamento por tenant: outro tenant → 404 (null) ---------------------------------------

    [Fact]
    public async Task Export_OtherTenant_ReturnsNull_For404()
    {
        Guid id;
        await using (var dbA = NewContext(TenantA))
        {
            await SeedDefaultLedgerAsync(dbA);
            id = (await ServiceFor(dbA).PublishAsync(PostureSnapshotType.AegisScoreNist, null)).Summary.Id;
        }

        await using var dbB = NewContext(TenantB);
        (await ExporterFor(dbB).ExportAsync(id, PostureExportFormat.Pdf))
            .Should().BeNull("um tenant jamais exporta a fotografia de outro (query filter fail-closed → 404)");
        (await ExporterFor(dbB).ExportAsync(id, PostureExportFormat.Csv)).Should().BeNull();
    }

    // ---- 8) Hash inválido: integridade recusada (409) ----------------------------------------------

    [Fact]
    public async Task Export_TamperedSnapshot_ThrowsIntegrity()
    {
        Guid id;
        await using (var db = NewContext(TenantA))
        {
            await SeedDefaultLedgerAsync(db);
            id = (await ServiceFor(db).PublishAsync(PostureSnapshotType.AegisScoreNist, null)).Summary.Id;
        }

        // Adultera o conteúdo SEM recomputar o hash (o gatilho append-only só existe no PostgreSQL real).
        await using (var db = NewContext(TenantA))
        {
            var snap = await db.PostureSnapshots.SingleAsync(s => s.Id == id);
            snap.AchievedPoints += 7;   // muda o conteúdo hasheado; o ContentHash gravado fica obsoleto
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(TenantA))
        {
            var act = () => ExporterFor(db).ExportAsync(id, PostureExportFormat.Pdf);
            await act.Should().ThrowAsync<PostureSnapshotIntegrityException>("hash divergente bloqueia a exportação (409)");
        }
    }

    // ---- 9) Formato inválido → 400 (parser fechado usado pelo controller) --------------------------

    [Fact]
    public void Format_Parsing_IsClosed_PdfCsvOnly()
    {
        PostureExportFormats.TryParse("pdf", out var pdf).Should().BeTrue(); pdf.Should().Be(PostureExportFormat.Pdf);
        PostureExportFormats.TryParse("CSV", out var csv).Should().BeTrue(); csv.Should().Be(PostureExportFormat.Csv);
        PostureExportFormats.TryParse("xml", out _).Should().BeFalse("formato desconhecido é recusado (400)");
        PostureExportFormats.TryParse("", out _).Should().BeFalse();
        PostureExportFormats.TryParse(null, out _).Should().BeFalse();
    }

    // ---- 10) Autorização: exportar permite os MESMOS leitores (inclusive Analyst) ------------------

    [Fact]
    public void Export_IsNotRoleRestricted_AnalystCanDownload()
    {
        var export = typeof(PostureSnapshotsController).GetMethod(nameof(PostureSnapshotsController.Export))!;
        var methodRoles = export.GetCustomAttributes<AuthorizeAttribute>(true)
            .Where(a => !string.IsNullOrWhiteSpace(a.Roles)).ToList();
        methodRoles.Should().BeEmpty("exportar não restringe papel — vale para quem já lê a fotografia, inclusive Analyst");

        // Contraste: publicar É restrito a Manager/TenantAdmin.
        var publish = typeof(PostureSnapshotsController).GetMethod(nameof(PostureSnapshotsController.Publish))!;
        publish.GetCustomAttributes<AuthorizeAttribute>(true).Should().Contain(a => a.Roles != null && a.Roles.Contains("Manager"));
    }

    // ---- 11) Autoridade única: detalhe, PDF e CSV refletem a MESMA fotografia -----------------------

    [Fact]
    public async Task SingleAuthority_Detail_Pdf_Csv_AllReflectTheSameSnapshot()
    {
        await using var db = NewContext(TenantA);
        await SeedDefaultLedgerAsync(db);
        var svc = ServiceFor(db);
        var id = (await svc.PublishAsync(PostureSnapshotType.AegisScoreNist, null)).Summary.Id;

        var detail = (await svc.GetAsync(id))!;   // o mesmo detalhe que a tela /history consome
        var csv = await ExporterFor(db).ExportAsync(id, PostureExportFormat.Csv);
        var pdf = await ExporterFor(db).ExportAsync(id, PostureExportFormat.Pdf);

        // PDF e CSV carregam o MESMO id e hash da fotografia.
        var pdfText = ExtractPdfText(pdf!.Content);
        pdfText.Should().Contain(id.ToString("D")).And.Contain(detail.Summary.ContentHash);

        var (header, rows) = ParseCsv(csv!.Content);
        int Col(string name) => Array.IndexOf(header, name);
        // Índices em locais para uso dentro das árvores de expressão do OnlyContain (funções locais não entram nelas).
        int snapshotIdCol = Col("SnapshotId"), contentHashCol = Col("ContentHash"),
            coverageCol = Col("Coverage"), stateCol = Col("EvaluationState");
        var expectedId = id.ToString("D");
        var expectedHash = detail.Summary.ContentHash;
        var expectedCoverage = detail.Summary.Coverage.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var expectedState = detail.Summary.EvaluationState;

        rows.Should().OnlyContain(r => r[snapshotIdCol] == expectedId);
        rows.Should().OnlyContain(r => r[contentHashCol] == expectedHash);
        rows.Should().HaveCount(detail.Controls.Count, "uma linha por controle — mesmo universo do detalhe");

        // Cada controle do detalhe corresponde exatamente à linha do CSV (estado, pontos autoritativos).
        foreach (var control in detail.Controls)
        {
            var row = rows.Single(r => r[Col("SubcategoryCode")] == control.SubcategoryCode);
            row[Col("Status")].Should().Be(control.Evaluated ? control.Status! : "NotEvaluated");
            row[Col("AchievedPoints")].Should().Be(control.AchievedPoints.ToString());
            row[Col("MaxPoints")].Should().Be(control.MaxPoints.ToString());
            row[Col("Evaluated")].Should().Be(control.Evaluated ? "true" : "false");
        }

        // O CSV reflete o score/cobertura/estado da MESMA projeção do detalhe (não recalculados).
        rows.Should().OnlyContain(r => r[coverageCol] == expectedCoverage);
        rows.Should().OnlyContain(r => r[stateCol] == expectedState);
    }

    // ---- infraestrutura do teste -------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private IPostureSnapshotService ServiceFor(AegisScoreDbContext db) =>
        new PostureSnapshotService(db, new SystemTenantContext(TenantA), new NistSignalMapper(db));

    private static IPostureSnapshotExporter ExporterFor(AegisScoreDbContext db) => new PostureSnapshotExporter(db);

    private static async Task SeedActiveFrameworkAsync(AegisScoreDbContext db)
    {
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };
        var fn = new NistFunction { FrameworkVersionId = fv.Id, Code = "PR", Name = "PROTECT (PR)" };
        var cat = new NistCategory { Function = fn, Code = "PR.AA", Name = "Identity Management" };
        db.AddRange(fv, fn, cat);
        db.Subcategories.AddRange(
            new NistSubcategory { Category = cat, Code = "PR.AA-01", MaxScorePoints = 20 },
            new NistSubcategory { Category = cat, Code = "PR.AA-02", MaxScorePoints = 10 },
            new NistSubcategory { Category = cat, Code = "PR.AA-03", MaxScorePoints = 10 });
        await db.SaveChangesAsync();
    }

    /// <summary>Framework ativo + ledger: 01=Compliant(20/20), 02=NonCompliant(0/10), 03=SEM estado (não avaliado).</summary>
    private static async Task SeedDefaultLedgerAsync(AegisScoreDbContext db)
    {
        await SeedActiveFrameworkAsync(db);
        var byCode = await db.Subcategories.ToDictionaryAsync(s => s.Code, s => s.Id);

        db.TenantControlStates.AddRange(
            new TenantControlState
            {
                SubcategoryId = byCode["PR.AA-01"], Status = ControlStatus.Compliant, CurrentScore = 20,
                LastVerdictSource = VerdictSource.Telemetry, LastEvaluatedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            },
            new TenantControlState
            {
                SubcategoryId = byCode["PR.AA-02"], Status = ControlStatus.NonCompliant, CurrentScore = 0,
                LastVerdictSource = VerdictSource.Documentary, LastEvaluatedAt = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
            });
        await db.SaveChangesAsync();
    }

    /// <summary>Assessment KNIGHT concluído (fonte Entra) com 2 indicadores — 1 Passed, 1 Exposed (título/evidência opcionais).</summary>
    private static async Task SeedKnightRunAsync(
        AegisScoreDbContext db, string? maliciousTitle = null, string? maliciousEvidence = null)
    {
        var run = new KnightAssessmentRun
        {
            Mode = KnightAssessmentMode.Live, SourceType = KnightSourceType.MicrosoftEntraId,
            SourceState = KnightSourceState.Completed, Source = "Microsoft Entra ID", Status = KnightRunStatus.Completed,
            CatalogVersion = "ak-knight-v2", ScoreFormulaVersion = "knight-score-v1",
            StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            Score = 58, Coverage = 100, PassedCount = 1, ExposedCount = 1,
        };
        run.Indicators.Add(new KnightIndicatorResult
        {
            IndicatorId = "AK-ENTRA-001",
            Title = maliciousTitle ?? "Contas privilegiadas sem MFA — café ☕",
            Category = KnightIndicatorCategory.PrivilegedAccess, Severity = SeverityLevel.Critical,
            Status = KnightIndicatorStatus.Exposed,
            Evidence = maliciousEvidence ?? "2 de 5 sem MFA · 中文.", AffectedObjectCount = 2,
            NistCodes = new() { "PR.AA-01" }, MitreTechniques = new() { "T1078" },
            SourceType = KnightSourceType.MicrosoftEntraId, CollectedAt = DateTimeOffset.UtcNow,
        });
        run.Indicators.Add(new KnightIndicatorResult
        {
            IndicatorId = "AK-ENTRA-003", Title = "Contas privilegiadas com mailbox",
            Category = KnightIndicatorCategory.PrivilegedAccess, Severity = SeverityLevel.Medium,
            Status = KnightIndicatorStatus.Passed, Evidence = "Nenhuma.", AffectedObjectCount = 0,
            SourceType = KnightSourceType.MicrosoftEntraId, CollectedAt = DateTimeOffset.UtcNow,
        });
        db.KnightAssessmentRuns.Add(run);
        await db.SaveChangesAsync();
    }

    // ---- utilidades de inspeção --------------------------------------------------------------------

    private static bool StartsWithPdfMagic(byte[] bytes) =>
        bytes.Length >= 5 && bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D'
        && bytes[3] == (byte)'F' && bytes[4] == (byte)'-';

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static string ExtractPdfText(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        return string.Join(" ", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    private static string Deaccent(string text)
    {
        var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Parser CSV mínimo (RFC 4180 com delimitador ';'): remove BOM, respeita aspas e CR/LF.</summary>
    private static (string[] Header, List<string[]> Rows) ParseCsv(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        var records = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ';') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { /* tratado no \n */ }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); records.Add(row.ToArray()); row = new(); }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); records.Add(row.ToArray()); }

        return (records[0], records.Skip(1).ToList());
    }
}
