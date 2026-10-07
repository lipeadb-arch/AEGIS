using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AegisScore.Application.Nist;

namespace AegisScore.Application.Posture.Export;

// ============================================================================
//  [AEGIS-ASSESSMENT-VISUALS-01] Dados ÚNICOS dos gráficos dos relatórios
// ============================================================================
// HTML (SVG), PDF (desenho vetorial) e CSV saem DESTA estrutura, montada só a partir dos modelos autoritativos dos
// relatórios (KnightReportModel / NistMaturityReport) e do histórico CONGELADO na fotografia. Nenhuma fórmula nova: as
// contagens e médias já estão nos modelos; aqui elas só são organizadas em categorias e séries. Valor ausente é nulo
// (nunca zero) e cada gráfico declara a escala, a base (denominador) e o critério de ordenação quando há ranking.

public enum ReportChartKind
{
    /// <summary>Colunas agrupadas (poucas categorias, nomes curtos).</summary>
    Columns,
    /// <summary>Barras horizontais (ranking com critério explícito ou nomes longos).</summary>
    HorizontalBars,
    /// <summary>Barras horizontais empilhadas com contagens absolutas e o total da linha.</summary>
    StackedBars,
    /// <summary>Rosca: partes de UM total com denominador explícito (nunca score ou média).</summary>
    Donut,
    /// <summary>Linha mensal com pontos; segmentos só entre meses vizinhos comparáveis.</summary>
    Line,
    /// <summary>Radar complementar (no máximo duas séries; escala fixa).</summary>
    Radar,
}

/// <param name="Tone">Classe de cor (ok, bad, warn, ne, err, na, acc, cur, tgt, sev-Critical…). Nunca o único portador da informação.</param>
/// <param name="Connect">Só em linha: <c>Connect[i]</c> liga o ponto i ao i−1 (mês vizinho e comparável).</param>
public sealed record ReportChartSeries(
    string Key, string Label, string Tone, IReadOnlyList<double?> Values, IReadOnlyList<bool>? Connect = null, bool Dashed = false);

/// <param name="Unit">Unidade dos valores, por extenso ("controles", "nível 1–5", "nota 0–100").</param>
/// <param name="Basis">Base/denominador declarado (ex.: "Base: 120 controles aplicáveis").</param>
/// <param name="CategoryLinks">Destino do detalhe de cada categoria (âncora no HTML, ou "filtro:chave=valor" no KNIGHT).</param>
/// <param name="CategoryTones">Cor por categoria quando há uma série só (severidades).</param>
/// <param name="Decimals">Casas decimais dos valores exibidos.</param>
public sealed record ReportChart(
    string Id,
    ReportChartKind Kind,
    string Title,
    string Description,
    string Unit,
    double Min,
    double Max,
    IReadOnlyList<string> Categories,
    IReadOnlyList<ReportChartSeries> Series,
    string? Basis = null,
    IReadOnlyList<string?>? CategoryLinks = null,
    IReadOnlyList<string>? CategoryTones = null,
    IReadOnlyList<string>? Notes = null,
    int Decimals = 0,
    /// <summary>Texto curto para categorias sem nenhum valor (ex.: "sem avaliação").</summary>
    string EmptyLabel = "sem dado",
    /// <summary>Gráfico sem nenhum valor a desenhar: o relatório mostra só a mensagem.</summary>
    string? EmptyMessage = null)
{
    /// <summary>Total por categoria (empilhadas) ou total geral (rosca).</summary>
    public double CategoryTotal(int i) => Series.Sum(s => s.Values[i] ?? 0);

    public static string Format(double? v, int decimals) =>
        v is not { } x ? "—" : x.ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), Pt);

    public static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
}

/// <summary>Os gráficos de um relatório, na ordem de apresentação: os da visão executiva primeiro.</summary>
public sealed record ReportCharts(IReadOnlyList<ReportChart> Executive, IReadOnlyList<ReportChart> Detail, ReportChart? History)
{
    public IEnumerable<ReportChart> All => Executive.Concat(Detail).Concat(History is null ? Array.Empty<ReportChart>() : new[] { History });
}

public static class ReportChartBuilder
{
    // ---- KNIGHT ---------------------------------------------------------------------------------------------------

    public static ReportCharts ForKnight(KnightReportModel m, FrozenPostureHistory? history)
    {
        var k = m.Kpis;
        var evaluated = k.Passed + k.Failed + k.Mitigated;
        var applicable = evaluated + k.NotEvaluated + k.Errors;

        var coverage = new ReportChart(
            "knight-cobertura", ReportChartKind.Donut, "Cobertura da avaliação",
            $"Controles avaliados dentro da base aplicável. Cobertura {ReportChart.Format(k.Coverage, 1)}%.",
            "controles", 0, applicable,
            new[] { "Avaliados", "Não avaliados", "Erro na avaliação" },
            new[] { new ReportChartSeries("controles", "Controles", "acc", new double?[] { evaluated, k.NotEvaluated, k.Errors }) },
            Basis: $"Base: {applicable} controle(s) aplicável(is); {k.NotApplicable} não aplicável(is) ficam fora da base.",
            CategoryLinks: new string?[] { null, "filtro:status=NotEvaluated", "filtro:status=Error" },
            CategoryTones: new[] { "acc", "ne", "err" },
            EmptyMessage: applicable == 0 ? "Nenhum controle aplicável nesta avaliação." : null);

        var severities = k.FindingsBySeverity;
        var severity = new ReportChart(
            "knight-severidade", ReportChartKind.Columns, "Controles com achados por severidade",
            "Controles reprovados ou mitigados em cada severidade (contagem de controles, não de itens afetados).",
            "controles", 0, NiceMax(severities.Select(x => (double)x.Count)),
            severities.Select(x => x.Label).ToList(),
            new[] { new ReportChartSeries("achados", "Controles com achados", "bad", severities.Select(x => (double?)x.Count).ToList()) },
            Basis: $"Total: {k.Findings} controle(s) com achados ({k.Failed} reprovado(s), {k.Mitigated} mitigado(s)).",
            CategoryLinks: severities.Select(x => (string?)$"filtro:status=findings&severity={x.Key}").ToList(),
            CategoryTones: severities.Select(x => "sev-" + x.Key).ToList(),
            EmptyMessage: k.Findings == 0 ? "Nenhum controle com achado nesta avaliação." : null);

        var services = m.ByService;
        var byService = new ReportChart(
            "knight-servicos", ReportChartKind.StackedBars, "Resultado dos controles por serviço",
            "Contagem absoluta de controles por resultado em cada serviço; o número à direita é o total do serviço.",
            "controles", 0, services.Count == 0 ? 1 : services.Max(r => r.Total),
            services.Select(r => r.Label).ToList(),
            StatusSeries(services),
            Basis: $"Ordenado pela quantidade de reprovados e, no empate, pelo total. {k.TotalControls} controle(s) no total.",
            CategoryLinks: services.Select(r => (string?)$"filtro:service={r.Key}").ToList(),
            EmptyMessage: services.Count == 0 ? "Nenhum controle nesta avaliação." : null);

        var domains = m.ByDomain.Where(r => r.Failed > 0).OrderByDescending(r => r.Failed).ThenByDescending(r => r.Total)
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        var byDomain = new ReportChart(
            "knight-dominios", ReportChartKind.HorizontalBars, "Domínios com mais controles reprovados",
            "Ranking por quantidade de controles reprovados (até dez domínios; empate pelo total de controles).",
            "controles reprovados", 0, NiceMax(domains.Select(r => (double)r.Failed)),
            domains.Select(r => r.Label).ToList(),
            new[] { new ReportChartSeries("reprovados", "Controles reprovados", "bad", domains.Select(r => (double?)r.Failed).ToList()) },
            Basis: $"{m.ByDomain.Count(r => r.Failed > 0)} de {m.ByDomain.Count} domínio(s) têm controle reprovado.",
            CategoryLinks: domains.Select(r => (string?)$"filtro:status=Exposed&domain={r.Key}").ToList(),
            EmptyMessage: domains.Count == 0 ? "Nenhum controle reprovado nesta avaliação." : null);

        return new ReportCharts(new[] { coverage, severity, byService }, new[] { byDomain }, HistoryChart(history, "knight-historico"));
    }

    private static IReadOnlyList<ReportChartSeries> StatusSeries(IReadOnlyList<ReportDistributionRow> rows) => new[]
    {
        new ReportChartSeries("Exposed", "Reprovado", "bad", rows.Select(r => (double?)r.Failed).ToList()),
        new ReportChartSeries("Mitigated", "Mitigado (atenção)", "warn", rows.Select(r => (double?)r.Mitigated).ToList()),
        new ReportChartSeries("Error", "Erro na avaliação", "err", rows.Select(r => (double?)r.Errors).ToList()),
        new ReportChartSeries("NotEvaluated", "Não avaliado", "ne", rows.Select(r => (double?)r.NotEvaluated).ToList()),
        new ReportChartSeries("Passed", "Aprovado", "ok", rows.Select(r => (double?)r.Passed).ToList()),
        new ReportChartSeries("NotApplicable", "Não aplicável", "na", rows.Select(r => (double?)r.NotApplicable).ToList()),
    };

    // ---- NIST -----------------------------------------------------------------------------------------------------

    public static ReportCharts ForNist(NistMaturityReport r, FrozenPostureHistory? history)
    {
        var fns = NistLabels.FunctionOrder
            .Select(code => r.Functions.FirstOrDefault(f => f.Code == code) ?? EmptyProfile(code))
            .ToList();
        var names = fns.Select(f => f.Code).ToList();
        var links = fns.Select(f => (string?)("#fn-" + f.Code)).ToList();

        var functions = new ReportChart(
            "nist-funcoes", ReportChartKind.Columns, "Atual × alvo nas seis funções",
            "Médias de maturidade confirmadas por revisão humana, na escala 1 a 5 da metodologia do AEGIS. Função sem avaliação fica sem coluna.",
            "nível 1–5", 0, 5, names,
            new[]
            {
                new ReportChartSeries("atual", "Atual", "cur", fns.Select(f => f.Current).ToList()),
                new ReportChartSeries("alvo", "Alvo", "tgt", fns.Select(f => f.Target).ToList()),
            },
            Basis: string.Join(" · ", fns.Select(f => $"{f.Code}: {f.Evaluated}/{f.Subcategories} avaliadas")),
            CategoryLinks: links, Decimals: 1, EmptyLabel: "sem avaliação",
            EmptyMessage: fns.All(f => f.Current is null && f.Target is null) ? "Nenhuma função tem avaliação confirmada." : null);

        var radar = functions with
        {
            Id = "nist-radar",
            Kind = ReportChartKind.Radar,
            Title = "Perfil atual × alvo (radar)",
            Description = "As mesmas médias das colunas, em perfil. Função sem avaliação não é desenhada como zero: o polígono fica aberto nela.",
        };

        var s = r.Summary;
        var stateCats = new[] { "Avaliadas", "Não se aplicam", "Aguardando confirmação", "Em andamento", "Sem avaliação" };
        var stateTones = new[] { "acc", "na", "warn", "cur-light", "ne" };
        var states = new ReportChart(
            "nist-situacao", ReportChartKind.Donut, "Situação das subcategorias do escopo",
            $"Estado de cada subcategoria do catálogo nesta rodada. Cobertura {ReportChart.Format(s.Coverage, 1)}% (avaliadas + não se aplicam).",
            "subcategorias", 0, s.Subcategories, stateCats,
            new[] { new ReportChartSeries("subcategorias", "Subcategorias", "acc",
                new double?[] { s.Evaluated, s.NotApplicable, s.PendingConfirmation, s.InProgress, s.NotEvaluated }) },
            Basis: $"Base: {s.Subcategories} subcategoria(s) do catálogo no escopo.",
            CategoryLinks: new string?[] { "#tecnica", "#tecnica", "#tecnica", "#tecnica", "#tecnica" },
            CategoryTones: stateTones,
            EmptyMessage: s.Subcategories == 0 ? "O escopo não tem subcategorias." : null);

        var progress = new ReportChart(
            "nist-andamento", ReportChartKind.StackedBars, "Andamento da avaliação por função",
            "Contagem absoluta de subcategorias por situação em cada função; o número à direita é o total da função.",
            "subcategorias", 0, fns.Count == 0 ? 1 : fns.Max(f => f.Subcategories),
            fns.Select(f => $"{f.Code} — {f.Name}").ToList(),
            new[]
            {
                new ReportChartSeries("Evaluated", "Avaliadas", "acc", fns.Select(f => (double?)f.Evaluated).ToList()),
                new ReportChartSeries("NotApplicable", "Não se aplicam", "na", fns.Select(f => (double?)f.NotApplicable).ToList()),
                new ReportChartSeries("PendingConfirmation", "Aguardando confirmação", "warn", fns.Select(f => (double?)f.PendingConfirmation).ToList()),
                new ReportChartSeries("InProgress", "Em andamento", "cur-light", fns.Select(f => (double?)f.InProgress).ToList()),
                new ReportChartSeries("NotEvaluated", "Sem avaliação", "ne", fns.Select(f => (double?)f.NotEvaluated).ToList()),
            },
            Basis: "Ordem fixa das funções do NIST CSF 2.0 (GV, ID, PR, DE, RS, RC).",
            CategoryLinks: links);

        var gaps = r.PriorityGaps.OrderByDescending(g => g.Gap).ThenBy(g => g.Code, StringComparer.Ordinal).Take(8).ToList();
        var gapChart = new ReportChart(
            "nist-lacunas", ReportChartKind.HorizontalBars, "Maiores lacunas confirmadas",
            "Distância entre alvo e atual (em níveis) nas subcategorias com os dois valores confirmados. Lacuna é meta de melhoria, não falha por si.",
            "níveis de lacuna", 0, 4,
            gaps.Select(g => $"{g.Code} — {g.Title}").ToList(),
            new[] { new ReportChartSeries("lacuna", "Lacuna (alvo − atual)", "tgt", gaps.Select(g => (double?)g.Gap).ToList()) },
            Basis: $"Ordenado pela lacuna e, no empate, pelo código (até oito). {s.WithGap} subcategoria(s) têm atual e alvo confirmados.",
            CategoryLinks: gaps.Select(g => (string?)("#sub-" + g.Code)).ToList(),
            EmptyMessage: gaps.Count == 0 ? "Nenhuma lacuna confirmada (atual e alvo registrados por revisão humana)." : null);

        var sevs = new[] { "Critical", "High", "Medium", "Low" };
        int Count(string sev, string status) => r.Findings.Count(f => f.Severity == sev && f.Status == status);
        var findings = new ReportChart(
            "nist-achados", ReportChartKind.Columns, "Achados por severidade e situação",
            "Quantidade de achados registrados nesta rodada e escopo, por severidade e situação.",
            "achados", 0, NiceMax(sevs.SelectMany(sv => new double[] { Count(sv, "Open"), Count(sv, "RiskAccepted"), Count(sv, "Closed") })),
            sevs.Select(NistLabels.Severity).ToList(),
            new[]
            {
                new ReportChartSeries("Open", "Abertos", "bad", sevs.Select(sv => (double?)Count(sv, "Open")).ToList()),
                new ReportChartSeries("RiskAccepted", "Risco aceito", "warn", sevs.Select(sv => (double?)Count(sv, "RiskAccepted")).ToList()),
                new ReportChartSeries("Closed", "Encerrados", "ok", sevs.Select(sv => (double?)Count(sv, "Closed")).ToList()),
            },
            Basis: $"Total: {r.Findings.Count} achado(s) — {s.FindingsOpen} aberto(s), {s.PlansOverdue} plano(s) atrasado(s).",
            CategoryLinks: sevs.Select(_ => (string?)"#achados").ToList(),
            EmptyMessage: r.Findings.Count == 0 ? "Nenhum achado registrado nesta rodada e escopo." : null);

        return new ReportCharts(new[] { functions, states, findings }, new[] { radar, progress, gapChart }, HistoryChart(history, "nist-historico"));
    }

    private static NistReportProfile EmptyProfile(string code) =>
        new(code, NistLabels.FunctionName(code), null, null, null, null, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    // ---- Histórico (os dois instrumentos) -------------------------------------------------------------------------

    /// <summary>
    /// A série congelada vira uma linha: mês sem publicação fica sem ponto, e só meses VIZINHOS e comparáveis são ligados.
    /// Na maturidade NIST há uma segunda linha (alvo), tracejada — nunca no mesmo eixo de uma nota 0–100.
    /// </summary>
    public static ReportChart? HistoryChart(FrozenPostureHistory? h, string id)
    {
        if (h is null) return null;
        var maturity = h.Series.Type == "NistMaturity";
        var byMonth = h.Points.ToDictionary(p => p.Month);
        var months = h.Months;
        IReadOnlyList<bool> Connect(Func<PostureMonthlyPoint, double?> value) => months.Select((m, i) =>
            i > 0 && byMonth.TryGetValue(m, out var cur) && byMonth.TryGetValue(months[i - 1], out var prev)
            && cur.ComparableWithPrevious && value(cur) is not null && value(prev) is not null).ToList();

        var series = new List<ReportChartSeries>();
        if (maturity)
        {
            series.Add(new ReportChartSeries("atual", "Maturidade atual", "cur",
                months.Select(m => byMonth.TryGetValue(m, out var p) ? p.MaturityCurrent : null).ToList(), Connect(p => p.MaturityCurrent)));
            series.Add(new ReportChartSeries("alvo", "Alvo", "tgt",
                months.Select(m => byMonth.TryGetValue(m, out var p) ? p.MaturityTarget : null).ToList(), Connect(p => p.MaturityTarget), Dashed: true));
        }
        else
        {
            series.Add(new ReportChartSeries("nota", "Nota KNIGHT", "acc",
                months.Select(m => byMonth.TryGetValue(m, out var p) ? p.Score : null).ToList(), Connect(p => p.Score)));
        }

        var breaks = h.Points.Where(p => p.BreakReasons.Count > 0)
            .Select(p => $"{FrozenPostureHistoryBuilder.MonthLabel(p.Month)}: série recomeça ({string.Join(", ", p.BreakReasons.Select(BreakLabel))}).");
        var empty = h.Points.Count == 0;
        return new ReportChart(
            id, ReportChartKind.Line,
            maturity ? "Evolução mensal da maturidade (1–5)" : "Evolução mensal da nota KNIGHT (0–100)",
            $"{h.Series.Label}. {FrozenPostureHistoryBuilder.MonthLabel(h.From)} a {FrozenPostureHistoryBuilder.MonthLabel(h.Until)}. " +
            "Cada ponto é a última publicação do mês; mês sem publicação fica sem ponto e só meses vizinhos comparáveis são ligados.",
            maturity ? "nível 1–5" : "nota 0–100", maturity ? 1 : 0, maturity ? 5 : 100,
            months.Select(FrozenPostureHistoryBuilder.MonthLabel).ToList(), series,
            Basis: h.Series.CoverageBasis,
            Notes: breaks.Concat(h.RelatedSeries).ToList(),
            Decimals: maturity ? 1 : 0, EmptyLabel: "sem publicação",
            EmptyMessage: empty ? "Nenhuma publicação desta série no período escolhido." : null);
    }

    public static string BreakLabel(string reason) => reason switch
    {
        PostureSnapshotComparer.ReasonDifferentFormula => "fórmula diferente",
        PostureSnapshotComparer.ReasonDifferentCatalog => "catálogo diferente",
        PostureSnapshotComparer.ReasonDifferentSchema => "formato da fotografia diferente",
        PostureSnapshotComparer.ReasonDifferentFamily => "outra série",
        PostureSnapshotComparer.ReasonDifferentType => "outro instrumento",
        PostureSnapshotComparer.ReasonDifferentComposition => "catálogo de uma fonte da composição diferente",
        _ => reason,
    };

    /// <summary>Máximo "redondo" do eixo de contagens (≥ 1), para as colunas e barras não encostarem no topo.</summary>
    public static double NiceMax(IEnumerable<double> values)
    {
        var max = values.DefaultIfEmpty(0).Max();
        if (max <= 4) return Math.Max(1, Math.Ceiling(max));
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var step in new[] { 1, 2, 2.5, 5, 10 })
            if (step * magnitude >= max) return step * magnitude;
        return Math.Ceiling(max);
    }
}
