using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace AegisScore.Application.Posture;

// ============================================================================
//  [AEGIS-ASSESSMENT-VISUALS-01] Histórico mensal CONGELADO na publicação
// ============================================================================
// O relatório exportado mostra a evolução anual do mesmo instrumento. Para que reexportar nunca leia o presente, a série
// é calculada NA PUBLICAÇÃO — pela mesma regra mensal da tela (PostureMonthlyHistory) — e gravada na fotografia, sob o
// hash. A própria publicação entra como o ponto do seu mês (quando o mês pertence ao período escolhido) com os valores
// dela mesma: nada depende do histórico para existir, então não há dependência circular.
//
// A prévia mostra exatamente esta série e devolve uma impressão digital (BasisFingerprint) do que foi apresentado:
// período, identidade da série, as fotografias anteriores usadas e os valores do novo ponto. A publicação recalcula e
// RECUSA (409) se a impressão mudou — uma outra publicação entrou na série, o mês virou ou a avaliação mudou —, em vez
// de gravar em silêncio uma série diferente da que a pessoa viu. A impressão digital do conteúdo avaliativo NIST é
// outra, independente desta.

/// <summary>Identidade e recorte da série congelada: o que ela mede, de onde vem e sobre qual base.</summary>
public sealed record FrozenHistoryIdentity(
    string Type,
    string SemanticFamily,
    string Label,
    string Instrument,
    double ScaleMin,
    double ScaleMax,
    string CoverageBasis,
    string FormulaVersion,
    string CatalogVersion,
    string? SourceType,
    string? SourceLabel,
    IReadOnlyList<string>? Composition,
    Guid? NistAssessmentId,
    Guid? NistScopeId);

/// <summary>A série anual congelada numa fotografia (ou mostrada numa prévia).</summary>
public sealed record FrozenPostureHistory(
    string Schema,
    string Criterion,
    DateOnly From,
    DateOnly Until,
    IReadOnlyList<DateOnly> Months,
    FrozenHistoryIdentity Series,
    IReadOnlyList<PostureMonthlyPoint> Points,
    /// <summary>Mês da publicação; <see cref="IncludesThisPublication"/> diz se ele pertence ao período escolhido.</summary>
    DateOnly PublicationMonth,
    bool IncludesThisPublication,
    /// <summary>Outras séries do mesmo instrumento no período (outra composição, outro escopo) — ditas, nunca ligadas.</summary>
    IReadOnlyList<string> RelatedSeries,
    /// <summary>Impressão digital do que a prévia apresentou (sem o id e o instante da nova fotografia).</summary>
    string BasisFingerprint)
{
    public const string SchemaV1 = "posture-history-v1";
}

/// <summary>Período pedido para o histórico: fim (mês) e quantidade de meses. Nulos = 12 meses até o mês da publicação.</summary>
public sealed record HistoryWindow(DateOnly? Until, int? Months)
{
    public static readonly HistoryWindow Default = new(null, null);

    /// <summary>Período pedido por um cliente: fim "aaaa-mm" e 1–36 meses (ambos opcionais). Mensagem pt-BR quando inválido.</summary>
    public static bool TryCreate(string? until, int? months, out HistoryWindow window, out string error)
    {
        window = Default;
        error = "";
        if (!TryParseMonth(until, out var month))
        {
            error = $"Mês final do histórico inválido: '{until}'. Use o formato aaaa-mm.";
            return false;
        }
        if (months is { } m && (m < 1 || m > PostureMonthlyHistory.MaxMonths))
        {
            error = $"Quantidade de meses do histórico fora do intervalo (1 a {PostureMonthlyHistory.MaxMonths}).";
            return false;
        }
        window = new HistoryWindow(month, months);
        return true;
    }

    /// <summary>"2026-09" ou "2026-09-01" → primeiro dia do mês; vazio → nulo; inválido → <c>false</c>.</summary>
    public static bool TryParseMonth(string? value, out DateOnly? month)
    {
        month = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var v = value.Trim();
        if (DateOnly.TryParseExact(v, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m)
            || DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out m))
        {
            month = new DateOnly(m.Year, m.Month, 1);
            return true;
        }
        return false;
    }
}

/// <summary>Erro de publicação: o histórico mudou desde a prévia (409). Nada foi publicado.</summary>
public sealed class HistoryChangedException : Exception
{
    public HistoryChangedException(string message) : base(message) { }
}

public static class FrozenPostureHistoryBuilder
{
    public const string ChangedMessage =
        "O histórico mudou desde a prévia (outra publicação entrou na série, o mês virou ou o período é outro). " +
        "Nada foi publicado: revise a prévia novamente antes de publicar.";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Monta a série da <paramref name="publication"/> (o resumo da fotografia que está sendo publicada — na prévia, com
    /// id vazio) a partir das fotografias JÁ publicadas do tenant (<paramref name="published"/>, qualquer tipo).
    /// Só entram as da mesma família; a publicação é o ponto do seu mês quando o mês pertence ao período. Função PURA.
    /// </summary>
    public static FrozenPostureHistory Build(
        PostureSnapshotSummaryDto publication, IReadOnlyList<PostureSnapshotSummaryDto> published, HistoryWindow window)
    {
        var publicationMonth = PostureMonthlyHistory.MonthOf(publication.CapturedAt);
        var until = window.Until is { } u ? new DateOnly(u.Year, u.Month, 1) : publicationMonth;
        if (until > publicationMonth) until = publicationMonth;   // não há fotografia depois desta
        var axis = PostureMonthlyHistory.Axis(publication.CapturedAt, window.Months ?? PostureMonthlyHistory.DefaultMonths, until);
        var includes = publicationMonth >= axis[0] && publicationMonth <= axis[^1];

        var family = published
            .Where(s => s.Id != publication.Id && s.Type == publication.Type && s.SemanticFamily == publication.SemanticFamily)
            .Where(s => s.CapturedAt <= publication.CapturedAt)
            .ToList();
        var series = PostureMonthlyHistory.SeriesOf(family.Append(publication).ToList(), axis);
        var points = series.Points
            .Where(p => includes || p.SnapshotId != publication.Id)
            .Select(p => p.SnapshotId == publication.Id ? p with { IsThisPublication = true } : p)
            .ToList();

        var related = Related(publication, published, axis);
        var identity = new FrozenHistoryIdentity(
            publication.Type, publication.SemanticFamily, PostureMonthlyHistory.LabelOf(publication),
            series.Instrument ?? PostureMonthlyHistory.InstrumentOf(publication.Type), series.ScaleMin ?? 0, series.ScaleMax ?? 100,
            series.CoverageBasis ?? PostureMonthlyHistory.CoverageBasisOf(publication.Type),
            publication.FormulaVersion, publication.CatalogVersion, publication.SourceType, publication.SourceLabel,
            publication.CompositionLabels, publication.NistAssessmentId, publication.NistScopeId);

        var basis = Fingerprint(identity, axis, publicationMonth, includes, points, publication, related);
        return new FrozenPostureHistory(FrozenPostureHistory.SchemaV1, PostureMonthlyHistory.Criterion, axis[0], axis[^1], axis,
            identity, points, publicationMonth, includes, related.Select(r => r.Text).ToList(), basis);
    }

    /// <summary>
    /// Recusa a publicação quando a prévia apresentou outra série (impressão digital informada e diferente). Sem impressão
    /// informada — chamada de API que não passou pela prévia, onde nenhuma série foi apresentada —, congela-se a série do
    /// período pedido (padrão: 12 meses até o mês da publicação), que o relatório identifica por inteiro. A tela sempre envia.
    /// </summary>
    public static void EnsureMatches(FrozenPostureHistory history, string? expectedFingerprint)
    {
        if (string.IsNullOrWhiteSpace(expectedFingerprint)) return;
        if (!string.Equals(history.BasisFingerprint, expectedFingerprint.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            throw new HistoryChangedException(ChangedMessage);
    }

    public static string Serialize(FrozenPostureHistory history) => JsonSerializer.Serialize(history, Json);

    /// <summary>Lê o histórico congelado; nulo quando a fotografia não tem (anterior a este recurso) ou ele está ilegível.</summary>
    public static FrozenPostureHistory? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<FrozenPostureHistory>(json, Json); }
        catch (JsonException) { return null; }
    }

    private sealed record RelatedSeries(string Text, IReadOnlyList<Guid> SnapshotIds);

    /// <summary>
    /// Séries vizinhas que o leitor precisa saber que existem: no KNIGHT consolidado, outra COMPOSIÇÃO publicada no período
    /// (outra família, nunca ligada a esta); no NIST, outro escopo da mesma avaliação.
    /// </summary>
    private static IReadOnlyList<RelatedSeries> Related(
        PostureSnapshotSummaryDto publication, IReadOnlyList<PostureSnapshotSummaryDto> published, IReadOnlyList<DateOnly> axis)
    {
        bool InWindow(PostureSnapshotSummaryDto s)
        {
            var m = PostureMonthlyHistory.MonthOf(s.CapturedAt);
            return m >= axis[0] && m <= axis[^1] && s.CapturedAt <= publication.CapturedAt && s.Id != publication.Id;
        }

        IEnumerable<IGrouping<string, PostureSnapshotSummaryDto>> groups;
        Func<IGrouping<string, PostureSnapshotSummaryDto>, string> text;
        if (publication.Type == "Knight" && publication.SemanticFamily.StartsWith("knight:consolidated:", StringComparison.Ordinal))
        {
            groups = published.Where(s => s.Type == "Knight" && s.SemanticFamily.StartsWith("knight:consolidated:", StringComparison.Ordinal)
                    && s.SemanticFamily != publication.SemanticFamily && InWindow(s))
                .GroupBy(s => s.SemanticFamily);
            text = g =>
            {
                var latest = g.OrderByDescending(s => s.CapturedAt.UtcTicks).First();
                var sources = latest.CompositionLabels is { Count: > 0 } c ? string.Join(", ", c) : latest.SourceLabel ?? "outra composição";
                return $"Outra composição de fontes publicada no período ({sources}; {MonthsText(g)}): série separada, não comparada com esta.";
            };
        }
        else if (publication.Type == "NistMaturity" && publication.NistAssessmentId is { } assessment)
        {
            groups = published.Where(s => s.Type == "NistMaturity" && s.NistAssessmentId == assessment
                    && s.SemanticFamily != publication.SemanticFamily && InWindow(s))
                .GroupBy(s => s.SemanticFamily);
            text = g => $"Outro escopo ou metodologia desta avaliação publicado no período ({g.OrderByDescending(s => s.CapturedAt.UtcTicks).First().SourceLabel}; {MonthsText(g)}): série separada.";
        }
        else return Array.Empty<RelatedSeries>();

        return groups
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new RelatedSeries(text(g), g.Select(s => s.Id).OrderBy(id => id).ToList()))
            .ToList();
    }

    private static string MonthsText(IEnumerable<PostureSnapshotSummaryDto> g) =>
        string.Join(", ", g.Select(s => PostureMonthlyHistory.MonthOf(s.CapturedAt)).Distinct().OrderBy(m => m).Select(MonthLabel));

    public static string MonthLabel(DateOnly m)
    {
        string[] names = { "jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez" };
        return $"{names[m.Month - 1]}/{m.Year % 100:00}";
    }

    /// <summary>
    /// SHA-256 do que a prévia apresentou: identidade, período, mês da publicação, cada ponto (fotografia e hash das
    /// anteriores; valores do novo) e as séries vizinhas citadas. O id e o instante exatos da nova fotografia ficam de
    /// fora — eles só existem na publicação.
    /// </summary>
    private static string Fingerprint(
        FrozenHistoryIdentity id, IReadOnlyList<DateOnly> axis, DateOnly publicationMonth, bool includes,
        IReadOnlyList<PostureMonthlyPoint> points, PostureSnapshotSummaryDto publication, IReadOnlyList<RelatedSeries> related)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void S(string? v) => sb.Append(v is null ? "N;" : $"S{v.Length}:{v};");
        void D(double? v) => sb.Append(v is null ? "Dn;" : "D" + v.Value.ToString("R", inv) + ";");
        S(FrozenPostureHistory.SchemaV1); S(id.Type); S(id.SemanticFamily); S(id.FormulaVersion); S(id.CatalogVersion);
        S(publication.CompositionKey); S(id.NistAssessmentId?.ToString("D")); S(id.NistScopeId?.ToString("D"));
        S(axis[0].ToString("yyyy-MM", inv)); S(axis[^1].ToString("yyyy-MM", inv)); S(publicationMonth.ToString("yyyy-MM", inv));
        S(includes ? "1" : "0");
        foreach (var p in points)
        {
            S(p.Month.ToString("yyyy-MM", inv));
            if (p.IsThisPublication)
            {
                S("this"); D(p.Score); D(p.MaturityCurrent); D(p.MaturityTarget); D(p.Coverage);
                S(p.EvaluatedItems.ToString(inv)); S(p.EligibleItems.ToString(inv)); S(p.ApplicableItems?.ToString(inv));
                S(p.SchemaVersion); S(p.SourceRunId?.ToString("D")); S(p.CycleName);
                S(p.PeriodStart?.ToString("yyyy-MM-dd", inv)); S(p.PeriodEnd?.ToString("yyyy-MM-dd", inv));
                S(p.PublishedInMonth.ToString(inv));
            }
            else
            {
                // As anteriores são imutáveis (append-only): o id identifica o conteúdo exibido.
                S(p.SnapshotId.ToString("D"));
            }
        }
        foreach (var r in related)
        {
            S(r.Text);
            foreach (var x in r.SnapshotIds) S(x.ToString("D"));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
