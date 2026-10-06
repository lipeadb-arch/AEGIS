using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AegisScore.Domain;

namespace AegisScore.Application.Posture.Export;

/// <summary>
/// [AEGIS-AUD-034] Escritor CSV PURO (sem EF/rede/relógio) da fotografia imutável de postura. Produz um arquivo
/// UTF-8 com BOM, delimitador <c>;</c>, números em formato INVARIANTE, timestamps em ISO 8601 (UTC) e uma linha por
/// controle NIST (AEGIS Score) ou por indicador (KNIGHT). O schema é ESTÁVEL e previsível: colunas fixas, itens
/// ordenados de forma determinística (código/id ordinal).
///
/// Segurança: TODA célula textual é protegida contra CSV/Formula Injection — um texto que comece (após espaços/
/// controles ignorados pelo Excel) por <c>= + - @</c>, ou por TAB/CR/LF, é neutralizado com prefixo <c>'</c> antes
/// de qualquer aspa. Números e timestamps gerados pelo SISTEMA não passam pelo neutralizador (não são texto externo)
/// — nunca começam por caractere perigoso. Aspas, delimitador, CR/LF e Unicode são escapados corretamente.
/// </summary>
public static class PostureSnapshotCsvWriter
{
    public static byte[] Write(PostureSnapshot snapshot)
    {
        var csv = new CsvBuilder();
        if (snapshot.Type == PostureSnapshotType.Knight)
            WriteKnight(csv, snapshot);
        else
            WriteAegis(csv, snapshot);
        return csv.ToUtf8WithBom();
    }

    // ---- AEGIS Score / NIST — uma linha por controle (do catálogo ATIVO completo) --------------------

    private static void WriteAegis(CsvBuilder csv, PostureSnapshot s)
    {
        csv.Text("SnapshotId").Text("ContentHash").Text("Type").Timestamp("CapturedAt")
           .Text("SchemaVersion").Text("FormulaVersion").Text("CatalogVersion")
           .Text("EvaluationState").Text("Score").Text("Coverage")
           .Text("FunctionCode").Text("SubcategoryCode").Text("Evaluated").Text("Status")
           .Text("AchievedPoints").Text("MaxPoints").Text("VerdictSource").Text("EvaluatedAt")
           .Text("EvidenceRefs").EndRow();

        var state = EvaluationState(s.Score);
        foreach (var c in s.Controls.OrderBy(c => c.SubcategoryCode, StringComparer.Ordinal))
        {
            csv.Text(s.Id.ToString("D")).Text(s.ContentHash).Text(s.Type.ToString()).TimestampValue(s.CapturedAt)
               .Text(s.SchemaVersion).Text(s.FormulaVersion).Text(s.CatalogVersion)
               .Text(state).Number(s.Score).Number(s.Coverage)
               .Text(c.FunctionCode).Text(c.SubcategoryCode).Bool(c.Evaluated)
               .Text(c.Evaluated ? c.Status?.ToString() ?? "" : "NotEvaluated")
               .Number(c.AchievedPoints).Number(c.MaxPoints)
               .Text(c.VerdictSource?.ToString()).TimestampValue(c.EvaluatedAt)
               .Text(FormatEvidence(c.EvidenceRefs)).EndRow();
        }
    }

    // ---- AEGIS KNIGHT — uma linha por indicador ------------------------------------------------------

    private static void WriteKnight(CsvBuilder csv, PostureSnapshot s)
    {
        // [AEGIS-KNIGHT-MULTICLOUD-01] Fotografia v2: uma linha por objeto de cada controle, a partir do MESMO
        // modelo do HTML. As v1 mantêm exatamente o formato anterior (uma linha por indicador).
        if (string.Equals(s.SchemaVersion, PostureSnapshotSchema.KnightReportVersion, StringComparison.Ordinal))
        {
            WriteKnightV2(csv, s);
            return;
        }

        csv.Text("SnapshotId").Text("ContentHash").Text("Type").Timestamp("CapturedAt")
           .Text("SchemaVersion").Text("FormulaVersion").Text("CatalogVersion")
           .Text("EvaluationState").Text("Score").Text("Coverage")
           .Text("SourceType").Text("SourceLabel")
           .Text("IndicatorId").Text("Title").Text("Category").Text("Severity").Text("Status")
           .Text("AffectedObjectCount").Timestamp("CollectedAt")
           .Text("NistCodes").Text("MitreTechniques").Text("Evidence").EndRow();

        var state = EvaluationState(s.Score);
        foreach (var i in s.Indicators.OrderBy(i => i.IndicatorId, StringComparer.Ordinal))
        {
            csv.Text(s.Id.ToString("D")).Text(s.ContentHash).Text(s.Type.ToString()).TimestampValue(s.CapturedAt)
               .Text(s.SchemaVersion).Text(s.FormulaVersion).Text(s.CatalogVersion)
               .Text(state).Number(s.Score).Number(s.Coverage)
               .Text(s.SourceType?.ToString()).Text(s.SourceLabel)
               .Text(i.IndicatorId).Text(i.Title).Text(i.Category.ToString()).Text(i.Severity.ToString()).Text(i.Status.ToString())
               .Number(i.AffectedObjectCount).TimestampValue(i.CollectedAt)
               .Text(string.Join(" ", i.NistCodes)).Text(string.Join(" ", i.MitreTechniques)).Text(i.Evidence)
               .EndRow();
        }
    }

    /// <summary>
    /// [AEGIS-KNIGHT-MULTICLOUD-01] CSV v2. As colunas da v1 vêm primeiro, com os mesmos nomes; as novas são
    /// acrescentadas ao final. Cada controle aparece ao menos uma vez: com objetos, uma linha por objeto
    /// (<c>RowKind=Objeto</c>); sem objetos, uma linha do controle (<c>RowKind=Controle</c>). Reconciliação com o
    /// HTML: controles = IndicatorId distintos; ocorrências = linhas com ObjectRelation "Afetado" em controles
    /// reprovados/mitigados; objetos únicos = pares (ObjectType, ObjectExternalId) distintos dessas linhas.
    /// </summary>
    private static void WriteKnightV2(CsvBuilder csv, PostureSnapshot s)
    {
        var model = KnightReportModelBuilder.Build(s, integrityVerified: true);
        var indicators = s.Indicators.ToDictionary(i => i.IndicatorId, StringComparer.Ordinal);

        csv.Text("SnapshotId").Text("ContentHash").Text("Type").Timestamp("CapturedAt")
           .Text("SchemaVersion").Text("FormulaVersion").Text("CatalogVersion")
           .Text("EvaluationState").Text("Score").Text("Coverage")
           .Text("SourceType").Text("SourceLabel")
           .Text("IndicatorId").Text("Title").Text("Category").Text("Severity").Text("Status")
           .Text("AffectedObjectCount").Timestamp("CollectedAt")
           .Text("NistCodes").Text("MitreTechniques").Text("Evidence")
           .Text("ClientName").Text("RunId").Text("ResultLabel").Text("SeverityLabel").Text("Domain").Text("Service").Text("Provider")
           .Text("Description").Text("Rationale").Text("ExpectedConfiguration").Text("DoesNotProve").Text("Recommendation")
           .Text("NotEvaluatedReason").Text("Weight").Text("ScoreFactor").Text("ScorePointsAchieved").Text("References")
           .Text("RowKind").Text("ObjectRelation").Text("ObjectType").Text("ObjectExternalId").Text("ObjectName")
           .Text("ObjectPrincipalName").Text("ObjectRoles").Text("ObjectDetail").Text("ObjectConfiguration")
           // [AEGIS-KNIGHT-COVERAGE-01] Acrescentadas AO FINAL — a ordem das colunas anteriores não muda.
           .Text("Platform").Text("Impact").Text("ProvenReach").Text("AffectedComposition")
           // [AEGIS-KNIGHT-CLOSURE-01] Acrescentadas AO FINAL: versão preview da API usada pelo controle (vazia = só estável) e,
           // nas linhas RowKind=ResultadoManual, o resultado de verificação manual congelado (à parte da avaliação).
           .Text("PreviewApi").Text("Origin")
           .Text("ManualReferenceKey").Text("ManualReferenceTitle").Text("ManualResult").Text("ManualJustification")
           .Text("ManualResponsible").Text("ManualEvidence").Text("ManualRecordedBy").Timestamp("ManualRecordedAt").Text("ManualValidUntil")
           .EndRow();

        var state = EvaluationState(s.Score);
        foreach (var c in model.Controls.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            var i = indicators[c.Id];
            var rows = c.Objects.Count == 0 ? new ReportObject?[] { null } : c.Objects.Cast<ReportObject?>().ToArray();
            foreach (var o in rows)
            {
                csv.Text(s.Id.ToString("D")).Text(s.ContentHash).Text(s.Type.ToString()).TimestampValue(s.CapturedAt)
                   .Text(s.SchemaVersion).Text(s.FormulaVersion).Text(s.CatalogVersion)
                   .Text(state).Number(s.Score).Number(s.Coverage)
                   // [AEGIS-KNIGHT-CONSOLIDATED-01] SourceType é do INDICADOR, não da fotografia: numa fotografia
                   // consolidada s.SourceType é "Consolidated" para toda a fotografia, mas cada linha precisa
                   // dizer de qual fonte real (Entra ID/Teams/Exchange) ela veio. Numa fotografia de fonte única
                   // os dois valores sempre coincidiam, então esta coluna não muda para nenhum relatório existente.
                   .Text(i.SourceType.ToString()).Text(s.SourceLabel)
                   .Text(i.IndicatorId).Text(i.Title).Text(i.Category.ToString()).Text(i.Severity.ToString()).Text(i.Status.ToString())
                   .Number(i.AffectedObjectCount).TimestampValue(i.CollectedAt)
                   .Text(string.Join(" ", i.NistCodes)).Text(string.Join(" ", i.MitreTechniques)).Text(i.Evidence)
                   .Text(s.ClientName).Text(s.SourceRunId?.ToString("D")).Text(c.StatusLabel).Text(c.SeverityLabel)
                   .Text(c.DomainLabel).Text(c.Service).Text(c.Provider)
                   .Text(c.Description).Text(c.Rationale).Text(c.ExpectedConfiguration).Text(c.DoesNotProve).Text(c.Recommendation)
                   .Text(c.NotEvaluatedReason).Number(c.Weight).Number(c.Factor).Number(c.Achieved)
                   .Text(string.Join(" | ", c.References.Select(r => (r.Version is null ? r.Framework : r.Framework + " " + r.Version) + ": " + r.Code + (r.Url is null ? "" : " <" + r.Url + ">"))))
                   .Text(o is null ? "Controle" : "Objeto")
                   .Text(o?.RelationLabel).Text(o?.KindLabel).Text(o?.ExternalId).Text(o?.DisplayName)
                   .Text(o?.UserPrincipalName).Text(o is null ? null : string.Join(", ", o.Roles)).Text(o?.Detail).Text(o?.ObservedConfiguration)
                   .Text(c.Platform).Text(c.Impact).Text(c.ProvenReach).Text(c.AffectedComposition)
                   .Text(c.PreviewApis is { Count: > 0 } pv ? string.Join(" | ", pv) : null).Text("Automatizado")
                   .Text(null).Text(null).Text(null).Text(null).Text(null).Text(null).Text(null).Text(null).Text(null)
                   .EndRow();
            }
        }

        // [AEGIS-KNIGHT-CLOSURE-01] Resultados de verificação MANUAL: linhas próprias, sem IndicatorId — a reconciliação com
        // o HTML (controles = IndicatorId distintos) continua valendo só para a avaliação automatizada.
        foreach (var m in model.ReferenceCoverage?.ManualResults ?? Array.Empty<AegisScore.Application.Knight.Reference.KnightManualResultEntry>())
        {
            csv.Text(s.Id.ToString("D")).Text(s.ContentHash).Text(s.Type.ToString()).TimestampValue(s.CapturedAt)
               .Text(s.SchemaVersion).Text(s.FormulaVersion).Text(s.CatalogVersion)
               .Text(state).Number(s.Score).Number(s.Coverage)
               .Text(null).Text(s.SourceLabel)
               .Text(null).Text(null).Text(null).Text(null).Text(null)
               .Number(0).Text(null)
               .Text(null).Text(null).Text(null)
               .Text(s.ClientName).Text(s.SourceRunId?.ToString("D")).Text(m.ResultLabel).Text(null)
               .Text(null).Text(null).Text(null)
               .Text(null).Text(null).Text(null).Text(null).Text(null)
               .Text(null).Text(null).Text(null).Text(null).Text(null)
               .Text("ResultadoManual")
               .Text(null).Text(null).Text(null).Text(null)
               .Text(null).Text(null).Text(null).Text(null)
               .Text(null).Text(null).Text(null).Text(null)
               .Text(null).Text("Manual")
               .Text(m.ReferenceKey).Text(m.Title).Text(m.ResultLabel + (m.Expired ? " (vencido)" : "")).Text(m.Justification)
               .Text(m.ResponsibleName)
               .Text(string.Join(" · ", new[] { m.EvidenceReference, m.EvidenceDocumentTitle is null ? null : "Documento: " + m.EvidenceDocumentTitle + (m.EvidenceDocumentSha256 is null ? "" : " (SHA-256 " + m.EvidenceDocumentSha256 + ")") }.Where(x => !string.IsNullOrWhiteSpace(x))))
               .Text(m.RecordedByName).Text(m.RecordedAt).Text(m.ValidUntil)
               .EndRow();
        }
    }

    private static string EvaluationState(double? score) => score is null ? "NotEvaluated" : "Evaluated";

    /// <summary>Referências de evidência sanitizadas numa única célula (uma por linha), legível e sem conteúdo bruto.</summary>
    private static string FormatEvidence(IEnumerable<PostureEvidenceRef> refs)
    {
        var lines = refs
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Source, StringComparer.Ordinal)
            .ThenBy(e => e.Reference, StringComparer.Ordinal)
            .Select(e =>
            {
                var at = e.CollectedAt is { } dt ? " @ " + Iso(dt) : "";
                return $"[{e.Kind}] {e.Source} / {e.Reference}{at}";
            })
            .ToList();
        return string.Join("\n", lines);
    }

    internal static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
