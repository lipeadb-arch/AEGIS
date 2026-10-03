using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Reference;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Posture.Export;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using UglyToad.PdfPig;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-CLOSURE-01] Resultado MANUAL estruturado dos controles de referência sem avaliação automatizada: só a
/// referência sem método (verificação manual, sem API publicada ou acesso que o conector não tem) o recebe; a validação diz
/// o que corrigir; substituição e retirada preservam o histórico; a validade vence com o tempo; nada atravessa tenants; a
/// publicação CONGELA os vigentes à parte — sem tocar nota, cobertura da avaliação nem cobertura automatizada do catálogo.
/// </summary>
public sealed class KnightManualResultTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-c105-c105-c105-0000000000a1");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-c105-c105-c105-0000000000b1");
    private const string Justification = "Verificado no portal de administracao em 03/10: configuracao conforme a referencia.";

    private readonly SqliteConnection _connection;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private static readonly RemediationActor Gestor = new(Guid.Parse("11111111-c105-c105-c105-000000000001"), "Gestor Demo");

    public KnightManualResultTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static KnightReferenceStatus First(KnightReferenceDisposition d) =>
        KnightReferenceCatalog.Coverage().Controls.Where(c => c.Disposition == d).OrderBy(c => c.Control.Key, StringComparer.Ordinal).First();

    private static RecordKnightManualResultCommand Cmd(string key, string result = "Compliant", string? evidence = "CHG-0001 (chamado sintetico)",
        Guid? document = null, DateOnly? until = null, string justification = Justification, string responsible = "Responsavel Demo") =>
        new(key, result, justification, responsible, evidence, document, until);

    private KnightManualResultService Service(AegisScoreDbContext db, Guid tenant) => new(db, new SystemTenantContext(tenant), _clock);

    [Fact]
    public async Task SoReferenciaSemAvaliacaoAutomatizada_RecebeResultadoManual()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var svc = Service(db, TenantA);

        foreach (var d in new[] { KnightReferenceDisposition.Implemented, KnightReferenceDisposition.Partial })
        {
            var act = () => svc.RecordAsync(Cmd(First(d).Control.Key), Gestor);
            (await act.Should().ThrowAsync<KnightManualResultValidationException>()).Which.Message.Should().Contain("avaliação automatizada", d.ToString());
        }
        await FluentActions.Awaiting(() => svc.RecordAsync(Cmd("CIS-INEXISTENTE:9.9.9"), Gestor))
            .Should().ThrowAsync<KnightManualResultValidationException>().WithMessage("*desconhecido*");

        foreach (var d in new[] { KnightReferenceDisposition.ManualOnly, KnightReferenceDisposition.ApiLimitation, KnightReferenceDisposition.RequiresAccess })
        {
            var saved = await svc.RecordAsync(Cmd(First(d).Control.Key), Gestor);
            saved.ReferenceDisposition.Should().Be(d.ToString(), "a disposição da referência no momento do registro fica gravada");
            saved.CatalogVersion.Should().Be(KnightCatalog.Version);
            saved.RecordedByName.Should().Be("Gestor Demo");
            saved.ResultLabel.Should().Contain("verificação manual");
        }
        (await svc.CurrentAsync()).Should().HaveCount(3);
    }

    public static IEnumerable<object[]> Invalidos() => new[]
    {
        new object[] { "justificativa curta", "*justificativa*", (Func<string, RecordKnightManualResultCommand>)(k => Cmd(k, justification: "curta")) },
        new object[] { "sem responsável", "*responsável*", (Func<string, RecordKnightManualResultCommand>)(k => Cmd(k, responsible: "  ")) },
        new object[] { "sem evidência", "*evidência*", (Func<string, RecordKnightManualResultCommand>)(k => Cmd(k, evidence: null)) },
        new object[] { "resultado desconhecido", "*Resultado inválido*", (Func<string, RecordKnightManualResultCommand>)(k => Cmd(k, result: "aprovado")) },
        new object[] { "retirada sem vigente", "*Não há resultado vigente*", (Func<string, RecordKnightManualResultCommand>)(k => Cmd(k, result: "Withdrawn", evidence: null)) },
    };

    [Theory]
    [MemberData(nameof(Invalidos))]
    public async Task Validacao_RecusaODadoIncompleto_EDizOQueCorrigir(string caso, string message, Func<string, RecordKnightManualResultCommand> cmd)
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var key = First(KnightReferenceDisposition.ManualOnly).Control.Key;

        await FluentActions.Awaiting(() => Service(db, TenantA).RecordAsync(cmd(key), Gestor))
            .Should().ThrowAsync<KnightManualResultValidationException>(caso).WithMessage(message);
        (await db.KnightManualAssessments.CountAsync()).Should().Be(0, "pedido recusado não grava nada");
    }

    [Fact]
    public async Task ValidadeNoPassado_Recusada()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var yesterday = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime).AddDays(-1);
        await FluentActions.Awaiting(() => Service(db, TenantA).RecordAsync(Cmd(First(KnightReferenceDisposition.ManualOnly).Control.Key, until: yesterday), Gestor))
            .Should().ThrowAsync<KnightManualResultValidationException>().WithMessage("*passado*");
    }

    [Fact]
    public async Task Substituicao_ERetirada_PreservamOHistorico()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var svc = Service(db, TenantA);
        var key = First(KnightReferenceDisposition.ManualOnly).Control.Key;

        await svc.RecordAsync(Cmd(key, "Compliant"), Gestor);
        _clock.Advance(TimeSpan.FromHours(1));
        await svc.RecordAsync(Cmd(key, "NonCompliant", evidence: "CHG-0002"), Gestor);

        var current = (await svc.CurrentAsync()).Should().ContainSingle().Subject;
        current.Result.Should().Be(KnightManualResult.NonCompliant, "o registro mais recente substitui o anterior");
        (await svc.HistoryAsync(key)).Select(h => h.Result).Should().Equal(KnightManualResult.NonCompliant, KnightManualResult.Compliant);

        // Retirar não exige evidência, mas exige justificativa; o resultado deixa de valer e o histórico continua.
        _clock.Advance(TimeSpan.FromHours(1));
        var withdrawn = await svc.RecordAsync(Cmd(key, "Withdrawn", evidence: null), Gestor);
        withdrawn.ValidUntil.Should().BeNull();
        (await svc.CurrentAsync()).Should().BeEmpty();
        (await svc.HistoryAsync(key)).Should().HaveCount(3);
        await FluentActions.Awaiting(() => svc.RecordAsync(Cmd(key, "Withdrawn", evidence: null), Gestor))
            .Should().ThrowAsync<KnightManualResultValidationException>().WithMessage("*Não há resultado vigente*");
    }

    [Fact]
    public async Task Validade_VenceComOTempo_SemSumirDoResultado()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var svc = Service(db, TenantA);
        var key = First(KnightReferenceDisposition.ManualOnly).Control.Key;
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        (await svc.RecordAsync(Cmd(key, until: today.AddDays(10)), Gestor)).Expired.Should().BeFalse();
        _clock.Advance(TimeSpan.FromDays(11));
        var current = (await svc.CurrentAsync()).Should().ContainSingle().Subject;
        current.Expired.Should().BeTrue("vencido continua visível — e identificado como vencido, nunca como vigente");
        current.Result.Should().Be(KnightManualResult.Compliant);
    }

    [Fact]
    public async Task IsolamentoPorTenant_ResultadoEDocumentoDeEvidencia()
    {
        await SeedAsync(TenantA);
        await SeedAsync(TenantB);
        var key = First(KnightReferenceDisposition.ManualOnly).Control.Key;
        Guid docA;
        await using (var db = NewContext(TenantA))
        {
            var doc = new GovernanceDocument { Title = "Politica de acesso (Cliente Demo)", Sha256 = new string('a', 64), FileName = "politica.pdf" };
            db.GovernanceDocuments.Add(doc);
            await db.SaveChangesAsync();
            docA = doc.Id;
            var saved = await Service(db, TenantA).RecordAsync(Cmd(key, evidence: null, document: docA), Gestor);
            saved.EvidenceDocumentTitle.Should().Be("Politica de acesso (Cliente Demo)", "título e hash do documento ficam gravados no registro");
            saved.EvidenceDocumentSha256.Should().Be(new string('a', 64));
        }

        await using (var db = NewContext(TenantB))
        {
            var svc = Service(db, TenantB);
            (await svc.CurrentAsync()).Should().BeEmpty();
            (await svc.HistoryAsync(key)).Should().BeEmpty();
            await FluentActions.Awaiting(() => svc.RecordAsync(Cmd(key, evidence: null, document: docA), Gestor))
                .Should().ThrowAsync<KnightManualResultValidationException>("o documento de outro tenant não existe para este").WithMessage("*não encontrado*");
            await svc.RecordAsync(Cmd(key, "NotApplicable", evidence: "Registro B-1"), Gestor);
        }

        await using (var db = NewContext(TenantA))
            (await Service(db, TenantA).CurrentAsync()).Should().ContainSingle().Which.Result.Should().Be(KnightManualResult.Compliant);
        await using (var raw = NewContext(null))
            (await raw.KnightManualAssessments.IgnoreQueryFilters().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Publicacao_CongelaOsVigentes_SemTocarNotaNemCobertura_EExportacoesSeparamOManual()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await KnightMulticloudReportTests.ServiceFor(db, TenantA, new EntraConfigurationScenario(EntraConfigurationScenario.Variant.NonCompliant).Handler())
            .RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);
        var publisher = new PostureSnapshotService(db, new SystemTenantContext(TenantA), new AegisScore.Infrastructure.Connectors.NistSignalMapper(db));

        var before = await Load(db, (await publisher.PublishAsync(PostureSnapshotType.Knight, null, run.Id)).Summary.Id);

        var svc = Service(db, TenantA);
        var manualKey = First(KnightReferenceDisposition.ManualOnly).Control.Key;
        var apiKey = First(KnightReferenceDisposition.ApiLimitation).Control.Key;
        await svc.RecordAsync(Cmd(manualKey, "Compliant", evidence: "CHG-0001"), Gestor);
        await svc.RecordAsync(Cmd(apiKey, "NonCompliant", evidence: "Registro 42"), Gestor);
        var withdrawnKey = First(KnightReferenceDisposition.RequiresAccess).Control.Key;
        await svc.RecordAsync(Cmd(withdrawnKey, "Compliant"), Gestor);
        await svc.RecordAsync(Cmd(withdrawnKey, "Withdrawn", evidence: null), Gestor);

        var after = await Load(db, (await publisher.PublishAsync(PostureSnapshotType.Knight, null, run.Id)).Summary.Id);
        PostureSnapshotHasher.Verify(after).Should().BeTrue("os resultados manuais entram no conteúdo assinado da fotografia");

        // Nota, cobertura da avaliação e controles: idênticos com ou sem resultado manual.
        after.Score.Should().Be(before.Score);
        after.Coverage.Should().Be(before.Coverage);
        after.NonCompliantCount.Should().Be(before.NonCompliantCount);
        after.Indicators.Select(i => (i.IndicatorId, i.Status)).Should().BeEquivalentTo(before.Indicators.Select(i => (i.IndicatorId, i.Status)));

        var mBefore = KnightReportModelBuilder.Build(before, true);
        var mAfter = KnightReportModelBuilder.Build(after, true);
        mBefore.ReferenceCoverage!.ManualResults.Should().BeEmpty();
        mAfter.ReferenceCoverage!.Total.Should().BeEquivalentTo(mBefore.ReferenceCoverage.Total, "a cobertura automatizada do catálogo não absorve atestação");
        mAfter.ReferenceCoverage.ManualResults!.Select(m => m.ReferenceKey).Should().Equal(new[] { apiKey, manualKey }.OrderBy(k => k, StringComparer.Ordinal),
            "só os VIGENTES são congelados: o retirado não aparece");
        mAfter.Controls.Should().HaveCount(mBefore.Controls.Count, "resultado manual não vira controle");
        mAfter.Kpis.Should().BeEquivalentTo(mBefore.Kpis);
        mAfter.Notes.Should().Contain(n => n.Contains("verificação MANUAL"));

        var exporter = new PostureSnapshotExporter(db);
        var html = Encoding.UTF8.GetString((await exporter.ExportAsync(after.Id, PostureExportFormat.Html))!.Content);
        html.Should().Contain("manualResults").And.Contain("CHG-0001").And.Contain("Resultados de verificação manual");

        var csv = ParseCsv(Encoding.UTF8.GetString((await exporter.ExportAsync(after.Id, PostureExportFormat.Csv))!.Content).TrimStart('﻿'));
        var header = csv[0];
        int Col(string name) => Array.IndexOf(header, name);
        int indicatorCol = Col("IndicatorId"), originCol = Col("Origin"), justificationCol = Col("ManualJustification");
        csv.Skip(1).Should().OnlyContain(r => r.Length == header.Length, "toda linha tem as mesmas colunas do cabeçalho");
        var manualRows = csv.Skip(1).Where(r => r[Col("RowKind")] == "ResultadoManual").ToList();
        manualRows.Should().HaveCount(2);
        manualRows.Should().OnlyContain(r => r[indicatorCol] == "" && r[originCol] == "Manual" && r[justificationCol] == Justification);
        manualRows.Select(r => r[Col("ManualResult")]).Should().BeEquivalentTo("Conforme (verificação manual)", "Não conforme (verificação manual)");
        var automated = csv.Skip(1).Where(r => r[Col("RowKind")] != "ResultadoManual").ToList();
        automated.Should().OnlyContain(r => r[originCol] == "Automatizado");
        automated.Select(r => r[Col("IndicatorId")]).Distinct().Should().HaveCount(mAfter.Controls.Count,
            "a reconciliação de controles conta IndicatorId distintos, e a linha manual não tem IndicatorId");

        var pdfText = Pdf((await exporter.ExportAsync(after.Id, PostureExportFormat.Pdf))!.Content);
        pdfText.Should().Contain("verificação manual (atestação)").And.Contain("CHG-0001");

        // Congelado: substituir e retirar depois da publicação não muda o relatório publicado.
        _clock.Advance(TimeSpan.FromHours(2));
        await svc.RecordAsync(Cmd(manualKey, "NonCompliant", evidence: "CHG-0099"), Gestor);
        await svc.RecordAsync(Cmd(apiKey, "Withdrawn", evidence: null), Gestor);
        var again = Encoding.UTF8.GetString((await new PostureSnapshotExporter(NewContext(TenantA)).ExportAsync(after.Id, PostureExportFormat.Html))!.Content);
        again.Should().Be(html);
        again.Should().NotContain("CHG-0099");
    }

    // ======================================================================================================

    private static async Task<PostureSnapshot> Load(AegisScoreDbContext db, Guid id) =>
        await db.PostureSnapshots.AsNoTracking()
            .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
            .SingleAsync(s => s.Id == id);

    private static string Pdf(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        return string.Join(" ", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    /// <summary>Parser CSV mínimo (';', aspas duplas escapadas) — só para ler o que o escritor gerou.</summary>
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            if (c == '"') quoted = true;
            else if (c == ';') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row.ToArray()); row.Clear(); }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private async Task SeedAsync(Guid tenantId)
    {
        await using (var db = NewContext(null))
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Cliente Demo", Slug = $"t-{tenantId:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using var dbt = NewContext(tenantId);
        dbt.Connectors.Add(new ConnectorConfig
        {
            TenantId = tenantId, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
            DisplayName = "Microsoft Entra ID", Enabled = true, EncryptedSettings = "cifrado",
        });
        await dbt.SaveChangesAsync();
    }
}
