using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Queries;

// ============================================================================
//  [AEGIS-RISK-PRIORITIZATION-01] Prioridade de tratamento de vulnerabilidades em DISPOSITIVOS
// ============================================================================
// Responde: "o que merece atenção primeiro, quais evidências justificam essa ordem e o que ainda falta saber?" — no
// escopo de risco TÉCNICO em dispositivos: vulnerabilidades em aberto observadas pelo Microsoft Defender, contexto do
// ativo e situações entre fontes Defender × Intune. Não é avaliação completa dos riscos do tenant.
//
// Três camadas separadas, como na correlação:
//   • aquisição dos fatos — DevicePriorityQuery (infraestrutura), na MESMA leitura coerente da correlação;
//   • avaliação — DevicePriorityEvaluator (aqui, puro e determinístico). A atribuição das vulnerabilidades, a política
//     temporal, a associação entre fontes e as situações vêm da autoridade ÚNICA CrossSourceCorrelationEvaluator — nada
//     disso é reimplementado aqui;
//   • linguagem e contratos — DevicePriorityNarrative e os DTOs.
//
// Unidade: o CASO = ativo × CVE em aberto e atribuível. O resumo por dispositivo aponta o caso que determinou a posição;
// a faixa de um caso nunca combina a severidade de uma CVE com o exploit de outra.
//
// Relação com os motores existentes (nenhum é substituído, nenhum é escrito): AEGIS Score e KNIGHT (postura/identidade),
// Microsoft Secure Score (recomendações de postura), CVSS/EPSS (fatos da fonte), ICR e o risco registrado no ativo
// (Asset.RiskScore) seguem com as próprias fórmulas. O IcrScoringService exige sete fatores numéricos e aqui só há fatos
// para parte deles — preencher o resto com zero, média ou constante inventaria dado; por isso ele NÃO é usado. A IA não
// decide faixa nem ordem.

/// <summary>Faixas de prioridade de tratamento (códigos estáveis). Ordem: <see cref="P1"/> antes de <see cref="P4"/>.</summary>
public static class DevicePriorityBands
{
    public const string P1 = "p1";
    public const string P2 = "p2";
    public const string P3 = "p3";
    public const string P4 = "p4";

    /// <summary>Resultado NÃO priorizável: falta fundamento mínimo (motivo explícito). Não é uma faixa.</summary>
    public const string Insufficient = "insufficient";

    public static IReadOnlyList<string> Prioritized { get; } = new[] { P1, P2, P3, P4 };

    public static string Of(int rank) => rank switch { 1 => P1, 2 => P2, 3 => P3, _ => P4 };

    public static int? RankOf(string? band) => band switch { P1 => 1, P2 => 2, P3 => 3, P4 => 4, _ => null };

    /// <summary>Filtro aceito pela Central: nulo (todas as faixas), uma faixa ou <see cref="Insufficient"/>.</summary>
    public static bool IsKnownFilter(string? band) => band is null || RankOf(band) is not null || band == Insufficient;
}

/// <summary>Ordem da severidade técnica na política (1 = crítica … 4 = baixa/nenhuma; 5 = não informada).</summary>
public static class DevicePrioritySeverity
{
    public const int Critical = 1;
    public const int High = 2;
    public const int Medium = 3;
    public const int Low = 4;
    public const int Unknown = 5;
}

/// <summary>Ordem do exploit informado pela fonte (0 = verificado, 1 = público, 2 = não informado/sem exploit).</summary>
public static class DevicePriorityExploit
{
    public const int Verified = 0;
    public const int Public = 1;
    public const int NotInformed = 2;
}

/// <summary>
/// Classificação de UMA CVE segundo a política — projeção traduzível para SQL (ordenação e paginação no banco) e a MESMA
/// usada em memória (<see cref="DevicePriorityPolicy.RankOf"/>): uma única definição, nunca duas versões divergentes.
/// </summary>
public sealed class DevicePriorityThreatRank
{
    public Guid ThreatId { get; set; }
    public string Code { get; set; } = "";
    public int SeverityOrder { get; set; }
    public int ExploitOrder { get; set; }
    public double? Cvss { get; set; }
}

/// <summary>
/// Política OPERACIONAL e versionada do AEGIS para a prioridade de tratamento — não é exigência normativa, não calcula
/// probabilidade nem percentual de risco. Mudança de critério = nova versão.
/// </summary>
public static class DevicePriorityPolicy
{
    public const string Code = "AEGIS-PRIO-DEV";
    public const int Version = 1;

    /// <summary>Quantas faixas o contexto comprovado pode antecipar, qualquer que seja o número de agravantes.</summary>
    public const int MaxAggravation = 1;

    /// <summary>Criticidade declarada a partir da qual o ativo agrava (1–4).</summary>
    public const int HighCriticality = 3;

    /// <summary>
    /// Severidade técnica: faixa QUALITATIVA oficial do CVSS v3.1 sobre o CVSS informado pela fonte (Crítica 9.0–10.0,
    /// Alta 7.0–8.9, Média 4.0–6.9, Baixa 0.1–3.9, Nenhuma 0.0 — agrupada com Baixa); a severidade textual da fonte só
    /// quando o CVSS não foi informado. Precedência FIXA (nunca "a mais conveniente"); divergência vira ressalva. Exploit:
    /// verificado &gt; público &gt; não informado — disponibilidade de exploit informada pela fonte, não exploração ativa.
    /// </summary>
    public static readonly Expression<Func<Threat, DevicePriorityThreatRank>> Rank = t => new DevicePriorityThreatRank
    {
        ThreatId = t.Id,
        Code = t.Code,
        SeverityOrder = t.CvssScore != null
            ? (t.CvssScore >= 9.0 ? 1 : t.CvssScore >= 7.0 ? 2 : t.CvssScore >= 4.0 ? 3 : 4)
            : t.Severity == null ? 5
            : t.Severity.Trim().ToLower() == "critical" ? 1
            : t.Severity.Trim().ToLower() == "high" ? 2
            : t.Severity.Trim().ToLower() == "medium" ? 3
            : t.Severity.Trim().ToLower() == "low" ? 4
            : 5,
        ExploitOrder = t.ExploitVerified == true ? 0 : t.PublicExploit == true ? 1 : 2,
        Cvss = t.CvssScore,
    };

    private static readonly Func<Threat, DevicePriorityThreatRank> RankCompiled = Rank.Compile();

    /// <summary>A MESMA classificação de <see cref="Rank"/>, em memória.</summary>
    public static DevicePriorityThreatRank RankOf(double? cvss, string? severity, bool? publicExploit, bool? exploitVerified) =>
        RankCompiled(new Threat
        {
            CvssScore = cvss, Severity = severity, PublicExploit = publicExploit, ExploitVerified = exploitVerified,
        });

    /// <summary>
    /// Tabela de decisão da faixa BASE — linhas: severidade técnica (crítica, alta, média, baixa); colunas: exploit
    /// verificado, público, não informado. Consultas no banco usam a forma equivalente limitar(severidade + exploit − 1,
    /// 1, 4), conferida contra esta tabela pelos testes.
    /// </summary>
    private static readonly int[,] BaseTable =
    {
        { 1, 1, 2 },   // Crítica
        { 1, 2, 3 },   // Alta
        { 2, 3, 4 },   // Média
        { 3, 4, 4 },   // Baixa / nenhuma
    };

    /// <summary>Faixa base do caso; nula quando a severidade técnica não foi informada (não priorizável).</summary>
    public static int? BaseBand(int severityOrder, int exploitOrder) =>
        severityOrder is >= DevicePrioritySeverity.Critical and <= DevicePrioritySeverity.Low
        && exploitOrder is >= DevicePriorityExploit.Verified and <= DevicePriorityExploit.NotInformed
            ? BaseTable[severityOrder - 1, exploitOrder]
            : null;

    /// <summary>Faixa final: agravante comprovado antecipa UMA faixa (nunca rebaixa; nunca acima de P1).</summary>
    public static int FinalBand(int baseBand, bool aggravated) =>
        Math.Max(1, baseBand - (aggravated ? MaxAggravation : 0));

    /// <summary>
    /// Desempate DETERMINÍSTICO entre casos: (1) faixa; (2) exploit informado; (3) severidade técnica; (4) agravante
    /// comprovado; (5) CVSS informado, maior primeiro (ausente por último). Empate nesses fatores é empate real — o chamador
    /// usa nome e identificador só para estabilidade.
    /// </summary>
    public static int Compare(DevicePriorityKey a, DevicePriorityKey b)
    {
        var c = a.Band.CompareTo(b.Band);
        if (c != 0) return c;
        c = a.ExploitOrder.CompareTo(b.ExploitOrder);
        if (c != 0) return c;
        c = a.SeverityOrder.CompareTo(b.SeverityOrder);
        if (c != 0) return c;
        c = b.Aggravated.CompareTo(a.Aggravated);
        if (c != 0) return c;
        return (b.Cvss ?? -1).CompareTo(a.Cvss ?? -1);
    }

    /// <summary>
    /// A faixa FINAL de um caso como expressão traduzível para SQL, GERADA da tabela de decisão e de <see cref="FinalBand"/> —
    /// a ordenação no banco usa a mesma definição do avaliador, inclusive a saturação em P1 (casos de faixas base diferentes
    /// que o agravante leva à mesma faixa final e que só o exploit e a severidade desempatam). Severidade não informada →
    /// <paramref name="unknownBand"/> (depois de todas as faixas).
    /// </summary>
    public static Expression<Func<T, int>> BandExpression<T>(
        Expression<Func<T, int>> severityOrder, Expression<Func<T, int>> exploitOrder, bool aggravated, int unknownBand)
    {
        var p = severityOrder.Parameters[0];
        var s = severityOrder.Body;
        var e = new ParameterSwap(exploitOrder.Parameters[0], p).Visit(exploitOrder.Body)!;
        Expression body = Expression.Constant(unknownBand);
        for (var sev = DevicePrioritySeverity.Low; sev >= DevicePrioritySeverity.Critical; sev--)
            for (var exp = DevicePriorityExploit.NotInformed; exp >= DevicePriorityExploit.Verified; exp--)
                body = Expression.Condition(
                    Expression.AndAlso(Expression.Equal(s, Expression.Constant(sev)), Expression.Equal(e, Expression.Constant(exp))),
                    Expression.Constant(FinalBand(BaseBand(sev, exp)!.Value, aggravated)),
                    body);
        return Expression.Lambda<Func<T, int>>(body, p);
    }

    private sealed class ParameterSwap(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}

/// <summary>Chave de ordenação de um caso (ou do caso determinante de um dispositivo).</summary>
public readonly record struct DevicePriorityKey(int Band, int ExploitOrder, int SeverityOrder, bool Aggravated, double? Cvss);

// ---- Fatos de entrada (obtidos pela infraestrutura) -----------------------------------------------------------------

/// <summary>Casos em aberto (disposição ativa) de UM conector numa marca de aquisição, agregados pela classificação da CVE.</summary>
public sealed record DevicePriorityCaseGroup(
    Guid ConnectorId, DateTimeOffset Marker, int SeverityOrder, int ExploitOrder, double? Cvss, int Count);

/// <summary>Casos em aberto com DISPOSIÇÃO humana registrada (mitigação informada, risco aceito, falso positivo).</summary>
public sealed record DevicePriorityDispositionGroup(Guid ConnectorId, DateTimeOffset Marker, ExposureStatus Status, int Count);

/// <summary>Criticidade do ativo e a proveniência da declaração (nula = sem proveniência).</summary>
public sealed record DevicePriorityCriticalityFacts(
    int StoredValue, int? DeclaredValue, DateTimeOffset? DeclaredAt, string? DeclaredByName, string? Note);

public sealed record DevicePriorityFacts(
    CrossSourceAssetFacts Correlation,
    DevicePriorityCriticalityFacts Criticality,
    IReadOnlyList<DevicePriorityCaseGroup> Cases,
    IReadOnlyList<DevicePriorityDispositionGroup> Dispositions);

/// <summary>Fatos de UMA CVE do ativo (para o caso determinante e a lista de casos) — só campos permitidos.</summary>
public sealed record DevicePriorityCaseFacts(
    Guid ThreatId,
    string CveId,
    string? Title,
    string? Severity,
    double? Cvss,
    string? CvssVector,
    bool? PublicExploit,
    bool? ExploitVerified,
    double? Epss,
    bool KnownExploited,
    DateTimeOffset? UpdatedOn,
    Guid ConnectorId,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset AcquiredAt);

// ---- Resultado da avaliação -----------------------------------------------------------------------------------------

/// <summary>Estado da prioridade de UM dispositivo (códigos estáveis).</summary>
public static class DevicePriorityStatuses
{
    /// <summary>Há ao menos um caso priorizável: o dispositivo tem faixa.</summary>
    public const string Prioritized = "prioritized";

    /// <summary>Há casos em aberto, mas nenhum com fundamento mínimo (motivo explícito).</summary>
    public const string Insufficient = "insufficient";

    /// <summary>Todos os casos atribuíveis em aberto têm disposição humana registrada — fora da fila, evidência preservada.</summary>
    public const string AllDisposed = "allDisposed";

    /// <summary>
    /// Nenhuma vulnerabilidade em aberto atribuível, e a ausência é CONCLUSIVA: a aquisição completa mais recente (ou a
    /// última completa publicada, com ressalva da tentativa que falhou) observou o dispositivo.
    /// </summary>
    public const string NoOpenCases = "noOpenCases";

    /// <summary>
    /// Nenhum caso publicado nesta leitura, mas a publicação não permite concluir ausência (parcial, não concluída, desfecho
    /// não registrado ou registro não reconfirmado). Nunca "nada a tratar".
    /// </summary>
    public const string AbsenceNotVerified = "absenceNotVerified";

    /// <summary>O ativo não tem registro da fonte de vulnerabilidades — fora do escopo desta avaliação.</summary>
    public const string NoSourceRecord = "noSourceRecord";
}

/// <summary>O que a correlação Defender × Intune diz sobre o dispositivo (códigos estáveis).</summary>
public static class DevicePriorityDeviceContexts
{
    /// <summary>Ao menos uma situação entre fontes IDENTIFICADA (associação comprovada) — um único agravante.</summary>
    public const string Gap = "gap";

    /// <summary>
    /// Com associação comprovada, o Intune informa fatos em que NENHUMA condição das regras está presente (conformidade que
    /// não é "não conforme" — conforme ou em período de carência — e criptografia). O estado informado é preservado em
    /// <see cref="DevicePriorityAssessment.DeviceReport"/>; "não corresponde à regra" não é "conforme".
    /// </summary>
    public const string NoGapInformed = "noGapInformed";

    /// <summary>Uma condição informada ausente; a outra sem informação determinada.</summary>
    public const string Partial = "partial";

    public const string Contradictory = "contradictory";
    public const string Conflict = "conflict";

    /// <summary>O ativo não tem registro do Intune.</summary>
    public const string NoManagementSource = "noManagementSource";

    /// <summary>Associação não comprovada ou evidência insuficiente para verificar as regras.</summary>
    public const string Unknown = "unknown";
}

public static class DevicePriorityCriticalityStates
{
    public const string Declared = "declared";
    public const string NotConfirmed = "notConfirmed";
    /// <summary>Há declaração, mas o valor cadastrado atual difere do declarado — a proveniência não o cobre.</summary>
    public const string Diverged = "diverged";
}

/// <summary>
/// O que a fonte NÃO reporta pode ser dado como ausente? Distinto da contagem de casos publicados e do estado da
/// atribuição (códigos estáveis) — a mesma completude da autoridade de correlação.
/// </summary>
public static class DevicePriorityAbsenceStates
{
    /// <summary>Aquisição mais recente completa, com o dispositivo reconfirmado nela.</summary>
    public const string Conclusive = "conclusive";

    /// <summary>Conclusiva na última aquisição completa publicada; a tentativa mais recente de coleta falhou.</summary>
    public const string ConclusiveAttemptFailed = "conclusiveAttemptFailed";

    /// <summary>A publicação não permite concluir ausência (parcial, não concluída, desfecho não registrado, não reconfirmado).</summary>
    public const string NotVerifiable = "notVerifiable";

    /// <summary>Sem atribuição utilizável (sem registro da fonte, registro não atual, atribuição ambígua) ou sem leitura.</summary>
    public const string NotApplicable = "notApplicable";
}

/// <summary>
/// Estado de conformidade e de criptografia EFETIVAMENTE informado pelo Intune nos registros elegíveis que sustentam a
/// conclusão das regras — para a narrativa não deduzir "conforme" de "não corresponde à condição da regra".
/// </summary>
public sealed record DevicePriorityDeviceReport(
    IReadOnlyList<DeviceComplianceBucket> Compliance, IReadOnlyList<DeviceEncryptionBucket> Encryption)
{
    public static DevicePriorityDeviceReport None { get; } =
        new(Array.Empty<DeviceComplianceBucket>(), Array.Empty<DeviceEncryptionBucket>());

    public bool GracePeriod => Compliance.Contains(DeviceComplianceBucket.InGracePeriod);
}

public sealed record DevicePriorityAssessment(
    DevicePriorityFacts Facts,
    CrossSourceAssetAssessment Correlation,
    CrossSourceVulnerabilitySource Source,
    string Status,
    /// <summary>Faixa final (1–4) quando priorizado.</summary>
    int? Band,
    /// <summary>Chave do caso determinante (a melhor do dispositivo).</summary>
    DevicePriorityKey? Best,
    /// <summary>Casos do dispositivo com exatamente os mesmos fatores do determinante (inclui o próprio).</summary>
    int BestCount,
    string DeviceContext,
    IReadOnlyList<CrossSourceRuleAssessment> IdentifiedSituations,
    string CriticalityState,
    bool CriticalityAggravates,
    bool Aggravated,
    /// <summary>Casos priorizáveis por faixa FINAL (unidade: casos ativo × CVE).</summary>
    IReadOnlyDictionary<int, int> CasesByBand,
    /// <summary>Casos em aberto sem severidade técnica informada (não priorizáveis).</summary>
    int InsufficientCases,
    IReadOnlyDictionary<ExposureStatus, int> Dispositions,
    /// <summary>Marcas de aquisição elegíveis por conector usável (as mesmas que a correlação usa).</summary>
    IReadOnlyDictionary<Guid, IReadOnlyList<DateTimeOffset>> EligibleMarkers,
    IReadOnlyList<string> Reasons,
    /// <summary>Completude para concluir ausência (<see cref="DevicePriorityAbsenceStates"/>) — independe da contagem.</summary>
    string AbsenceState,
    /// <summary>Marca da aquisição completa que sustenta a ausência (quando conclusiva).</summary>
    DateTimeOffset? AbsenceAt,
    IReadOnlyList<string> AbsenceReasons,
    DevicePriorityDeviceReport DeviceReport)
{
    public int PrioritizableCases => CasesByBand.Values.Sum();
    public int DispositionCases => Dispositions.Values.Sum();
    /// <summary>Casos em aberto armazenados nas aquisições elegíveis (priorizáveis, sem severidade e com disposição) — o fato.</summary>
    public int StoredOpenCases => PrioritizableCases + InsufficientCases + DispositionCases;
    public int OpenOutOfPolicy => Source.Evidence.Sum(e => e.OpenOutOfPolicy);
    public int NoLongerReported => Source.Evidence.Sum(e => e.NoLongerReported);
}

/// <summary>
/// Autoridade ÚNICA e DETERMINÍSTICA da prioridade de tratamento em dispositivos: mesma entrada ⇒ mesma saída. Pura — não
/// lê banco nem relógio. Atribuição, política temporal, associação e situações são da <see cref="CrossSourceCorrelationEvaluator"/>.
/// </summary>
public static class DevicePriorityEvaluator
{
    public static DevicePriorityAssessment Evaluate(DevicePriorityFacts f, CrossSourcePolicy policy, DateTimeOffset now)
    {
        var correlation = CrossSourceCorrelationEvaluator.Evaluate(f.Correlation, policy, now);
        var source = CrossSourceCorrelationEvaluator.AssessVulnerabilitySource(f.Correlation, policy, now);
        var (context, situations, report) = DeviceContext(correlation);
        var (absence, absenceAt) = Absence(source);
        var (critState, critAggravates) = Criticality(f.Criticality);
        var gap = context == DevicePriorityDeviceContexts.Gap;
        var aggravated = gap || critAggravates;

        // Marcas elegíveis da autoridade de correlação — é por elas (e só por elas) que os casos contam.
        var markers = source.Evidence.ToDictionary(
            e => e.Connector.ConnectorId, e => (IReadOnlyList<DateTimeOffset>)e.EligibleMarkers.ToList());
        bool Eligible(Guid connector, DateTimeOffset marker) =>
            markers.TryGetValue(connector, out var m) && m.Contains(marker);

        var eligible = f.Cases.Where(c => Eligible(c.ConnectorId, c.Marker)).ToList();
        var dispositions = f.Dispositions.Where(d => Eligible(d.ConnectorId, d.Marker))
            .GroupBy(d => d.Status)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.Count));

        var byBand = new Dictionary<int, int>();
        var insufficientCases = 0;
        DevicePriorityKey? best = null;
        foreach (var c in eligible)
        {
            if (DevicePriorityPolicy.BaseBand(c.SeverityOrder, c.ExploitOrder) is not { } baseBand)
            {
                insufficientCases += c.Count;
                continue;
            }
            var key = new DevicePriorityKey(
                DevicePriorityPolicy.FinalBand(baseBand, aggravated), c.ExploitOrder, c.SeverityOrder, aggravated, c.Cvss);
            byBand[key.Band] = byBand.GetValueOrDefault(key.Band) + c.Count;
            if (best is null || DevicePriorityPolicy.Compare(key, best.Value) < 0) best = key;
        }
        var bestCount = best is { } b
            ? eligible.Where(c => c.SeverityOrder == b.SeverityOrder && c.ExploitOrder == b.ExploitOrder && Nullable.Equals(c.Cvss, b.Cvss))
                .Sum(c => c.Count)
            : 0;

        var reasons = new List<string>();
        string status;
        if (source.State == CrossSourceVulnerabilitySourceStates.NoSourceRecord)
        {
            status = DevicePriorityStatuses.NoSourceRecord;
            reasons.AddRange(source.Reasons);
        }
        else if (best is not null)
            status = DevicePriorityStatuses.Prioritized;
        else if (source.State != CrossSourceVulnerabilitySourceStates.Attributable)
        {
            status = DevicePriorityStatuses.Insufficient;
            reasons.Add(source.State == CrossSourceVulnerabilitySourceStates.AmbiguousAttribution
                ? "As vulnerabilidades não podem ser atribuídas sem ambiguidade a este dispositivo: há outro registro ativo do " +
                  "mesmo conector do Defender que não está atualmente observado. Nenhum registro é escolhido."
                : "O registro do Microsoft Defender deste ativo não está atualmente observado (não mais observado, obtido fora " +
                  "da política temporal ou sem atividade recente informada pela fonte).");
            reasons.AddRange(source.Reasons);
        }
        else if (insufficientCases > 0)
        {
            status = DevicePriorityStatuses.Insufficient;
            reasons.Add($"{insufficientCases} CVE(s) em aberto sem CVSS nem severidade informados pela fonte: sem severidade " +
                "técnica não há fundamento mínimo para uma faixa.");
        }
        else if (dispositions.Count > 0)
            status = DevicePriorityStatuses.AllDisposed;
        else if (source.Evidence.Sum(e => e.OpenOutOfPolicy) > 0)
        {
            status = DevicePriorityStatuses.Insufficient;
            reasons.Add($"As vulnerabilidades em aberto deste dispositivo foram observadas antes da política temporal " +
                $"({policy.MaxEvidenceAgeDays} dia(s)) e não sustentam prioridade atual.");
        }
        else if (absence == DevicePriorityAbsenceStates.NotVerifiable)
        {
            // Nenhum caso publicado NÃO é ausência: a publicação não permite concluir (mesma regra da correlação).
            status = DevicePriorityStatuses.AbsenceNotVerified;
            reasons.AddRange(source.AbsenceReasons);
        }
        else
            status = DevicePriorityStatuses.NoOpenCases;

        return new DevicePriorityAssessment(
            f, correlation, source, status,
            Band: best?.Band, Best: best, BestCount: bestCount,
            DeviceContext: context, IdentifiedSituations: situations,
            CriticalityState: critState, CriticalityAggravates: critAggravates, Aggravated: aggravated && best is not null,
            CasesByBand: byBand, InsufficientCases: insufficientCases, Dispositions: dispositions,
            EligibleMarkers: markers, Reasons: reasons,
            AbsenceState: absence, AbsenceAt: absenceAt, AbsenceReasons: source.AbsenceReasons, DeviceReport: report);
    }

    /// <summary>Completude da fonte de vulnerabilidades NESTE dispositivo, pela autoridade de correlação.</summary>
    private static (string State, DateTimeOffset? At) Absence(CrossSourceVulnerabilitySource s)
    {
        if (s.State != CrossSourceVulnerabilitySourceStates.Attributable) return (DevicePriorityAbsenceStates.NotApplicable, null);
        if (!s.AbsenceConclusive) return (DevicePriorityAbsenceStates.NotVerifiable, null);
        return (s.Evidence.Any(e => e.Connector.LatestAttemptFailed)
                ? DevicePriorityAbsenceStates.ConclusiveAttemptFailed
                : DevicePriorityAbsenceStates.Conclusive,
            s.Evidence.Select(e => e.Connector.Watermark).Max());
    }

    /// <summary>
    /// Completude da leitura INTEIRA (Central): a mesma regra por fonte — só aquisição publicada completa sustenta que os
    /// dispositivos fora da fila não têm vulnerabilidade em aberto. Distinta da contagem de candidatos.
    /// </summary>
    public static (string State, string? Note) SourceAbsence(IEnumerable<CrossSourceConnectorFacts> connectors)
    {
        var vuln = connectors.Where(c => c.Role == CrossSourceRole.Vulnerabilities)
            .OrderBy(c => c.ConnectorName, StringComparer.Ordinal).ThenBy(c => c.ConnectorId).ToList();
        if (vuln.Count == 0 || vuln.All(c => c.Watermark is null)) return (DevicePriorityAbsenceStates.NotApplicable, null);
        var gaps = vuln
            .Select(c => c.Watermark is not { } w
                ? $"{c.Label} ({c.ConnectorName}): ainda não publicou uma leitura por dispositivo."
                : CrossSourceCorrelationEvaluator.AcquisitionIncompleteness(c) is { } why
                    ? $"{why} Aquisição de {CrossSourceCorrelationEvaluator.Utc(w)}."
                    : null)
            .OfType<string>()
            .ToList();
        if (gaps.Count > 0)
            return (DevicePriorityAbsenceStates.NotVerifiable,
                "A leitura atual não permite concluir ausência de vulnerabilidades em aberto nos dispositivos fora desta fila: " +
                string.Join(" ", gaps) + " Os casos publicados continuam valendo como fato, com ressalvas.");
        var failed = vuln.Where(c => c.LatestAttemptFailed).ToList();
        if (failed.Count > 0)
            return (DevicePriorityAbsenceStates.ConclusiveAttemptFailed,
                "A tentativa mais recente de coleta do Microsoft Defender falhou; a leitura usa a última aquisição completa " +
                $"publicada ({string.Join(", ", failed.Select(c => CrossSourceCorrelationEvaluator.Utc(c.Watermark!.Value)))}).");
        return (DevicePriorityAbsenceStates.Conclusive, null);
    }

    /// <summary>
    /// Contexto de gestão do dispositivo pelas SITUAÇÕES já avaliadas: qualquer situação identificada é UM agravante (as
    /// duas regras são coexistências no mesmo dispositivo — duas situações não viram dois bônus). Contradição, conflito,
    /// associação não comprovada ou fonte ausente não agravam e não atenuam. Quando as regras NÃO se formam pelo lado do
    /// dispositivo, guarda o estado que o Intune efetivamente informou (ex.: período de carência não é "conforme").
    /// </summary>
    private static (string Context, IReadOnlyList<CrossSourceRuleAssessment> Situations, DevicePriorityDeviceReport Report)
        DeviceContext(CrossSourceAssetAssessment a)
    {
        var identified = a.Rules.Where(r => r.State == CrossSourceStates.Identified).ToList();
        var none = DevicePriorityDeviceReport.None;
        if (identified.Count > 0) return (DevicePriorityDeviceContexts.Gap, identified, none);
        if (a.Rules.Any(r => r.State == CrossSourceStates.ContradictoryEvidence))
            return (DevicePriorityDeviceContexts.Contradictory, identified, none);
        if (a.Rules.Any(r => r.State == CrossSourceStates.LinkConflict)) return (DevicePriorityDeviceContexts.Conflict, identified, none);
        if (a.Association.State == CrossSourceAssociationStates.SourceMissing
            && a.Records.All(r => r.Connector.Role != CrossSourceRole.DeviceManagement))
            return (DevicePriorityDeviceContexts.NoManagementSource, identified, none);
        bool DeviceAbsent(CrossSourceRuleAssessment r) =>
            r.State == CrossSourceStates.NotIdentified && r.SupportingDeviceRecords.Count > 0;
        IEnumerable<CrossSourceBindingFacts> Supporting(CrossSourceDeviceCondition c) => a.Rules
            .Where(r => r.Rule.Condition == c && DeviceAbsent(r))
            .SelectMany(r => r.SupportingDeviceRecords)
            .Select(r => r.Binding);
        var report = new DevicePriorityDeviceReport(
            Supporting(CrossSourceDeviceCondition.Noncompliant).Select(b => b.Compliance).OfType<DeviceComplianceBucket>()
                .Distinct().OrderBy(x => x).ToList(),
            Supporting(CrossSourceDeviceCondition.NotEncrypted).Select(b => b.Encryption).OfType<DeviceEncryptionBucket>()
                .Distinct().OrderBy(x => x).ToList());
        if (a.Rules.All(DeviceAbsent)) return (DevicePriorityDeviceContexts.NoGapInformed, identified, report);
        if (a.Rules.Any(DeviceAbsent)) return (DevicePriorityDeviceContexts.Partial, identified, report);
        return (DevicePriorityDeviceContexts.Unknown, identified, none);
    }

    /// <summary>Criticidade só é informação declarada com proveniência E igual ao valor cadastrado atual.</summary>
    private static (string State, bool Aggravates) Criticality(DevicePriorityCriticalityFacts c)
    {
        if (c.DeclaredAt is null || c.DeclaredValue is null) return (DevicePriorityCriticalityStates.NotConfirmed, false);
        if (c.DeclaredValue != c.StoredValue) return (DevicePriorityCriticalityStates.Diverged, false);
        return (DevicePriorityCriticalityStates.Declared, c.DeclaredValue >= DevicePriorityPolicy.HighCriticality);
    }
}

// ---- Linguagem ------------------------------------------------------------------------------------------------------

/// <summary>Autoridade ÚNICA da linguagem da prioridade — a Central e o detalhe do ativo dizem a mesma coisa. Sem IA.</summary>
public static class DevicePriorityNarrative
{
    public const string Heading = "Prioridade de tratamento";

    public const string Scope =
        "Prioridade de tratamento de vulnerabilidades em dispositivos, calculada a cada leitura pela política " +
        "determinística e versionada do AEGIS sobre fatos das fontes integradas: vulnerabilidades em aberto observadas pelo " +
        "Microsoft Defender, criticidade declarada do ativo e situações entre fontes Defender × Intune. Não é avaliação " +
        "completa dos riscos do ambiente nem probabilidade de incidente; não substitui nem altera o AEGIS Score, o AEGIS " +
        "KNIGHT, o Microsoft Secure Score, CVSS/EPSS ou o risco registrado do ativo. A IA não decide faixa nem ordem.";

    public const string VulnerabilitiesSource = "Microsoft Defender Vulnerability Management";

    public static string BandLabel(string band) => band switch
    {
        DevicePriorityBands.P1 => "Prioridade 1 · tratar primeiro",
        DevicePriorityBands.P2 => "Prioridade 2 · tratar em seguida",
        DevicePriorityBands.P3 => "Prioridade 3 · planejar tratamento",
        DevicePriorityBands.P4 => "Prioridade 4 · acompanhar",
        _ => "Não priorizável · informação insuficiente",
    };

    public static string StatusBandLabel(DevicePriorityAssessment a) => a.Status switch
    {
        DevicePriorityStatuses.Prioritized => BandLabel(DevicePriorityBands.Of(a.Band!.Value)),
        DevicePriorityStatuses.AllDisposed => "Fora da fila · disposição registrada",
        DevicePriorityStatuses.NoOpenCases => a.AbsenceState == DevicePriorityAbsenceStates.ConclusiveAttemptFailed
            ? "Sem vulnerabilidade em aberto na última aquisição completa publicada"
            : "Sem vulnerabilidade em aberto na aquisição completa mais recente",
        DevicePriorityStatuses.AbsenceNotVerified => "Sem caso publicado · ausência não verificável",
        DevicePriorityStatuses.NoSourceRecord => "Fora do escopo · sem registro do Microsoft Defender",
        _ => BandLabel(DevicePriorityBands.Insufficient),
    };

    public static string SeverityName(int order, double? cvss) => order switch
    {
        DevicePrioritySeverity.Critical => "Crítica",
        DevicePrioritySeverity.High => "Alta",
        DevicePrioritySeverity.Medium => "Média",
        DevicePrioritySeverity.Low => cvss is 0 ? "Nenhuma" : "Baixa",
        _ => "Não informada",
    };

    public static string Cvss(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>"Crítica (CVSS 9.8)", "Alta (severidade da fonte; CVSS não informado)" ou "Não informada pela fonte".</summary>
    public static string SeverityLabel(int order, double? cvss) =>
        order == DevicePrioritySeverity.Unknown ? "Não informada pela fonte"
        : cvss is { } s ? $"{SeverityName(order, cvss)} (CVSS {Cvss(s)})"
        : $"{SeverityName(order, null)} (severidade informada pela fonte; CVSS não informado)";

    /// <summary>O rótulo de severidade dentro de uma frase: só a primeira palavra em minúscula ("crítica (CVSS 9.8)").</summary>
    private static string SeverityPhrase(int order, double? cvss)
    {
        var label = SeverityLabel(order, cvss);
        var space = label.IndexOf(' ');
        return space < 0 ? label.ToLowerInvariant() : label[..space].ToLowerInvariant() + label[space..];
    }

    /// <summary>Disponibilidade de exploit informada pela fonte — nunca "explorada", "exploração ativa" ou "ameaça".</summary>
    public static string ExploitLabel(bool? publicExploit, bool? exploitVerified) =>
        exploitVerified == true ? "Exploit verificado informado pela fonte"
        : publicExploit == true ? "Exploit público informado pela fonte"
        : publicExploit is null && exploitVerified is null ? "Informação de exploit não fornecida pela fonte"
        : "Sem exploit informado pela fonte";

    private static string ExploitLabel(int order) => order switch
    {
        DevicePriorityExploit.Verified => "exploit verificado informado pela fonte",
        DevicePriorityExploit.Public => "exploit público informado pela fonte",
        _ => "sem exploit público ou verificado informado pela fonte",
    };

    /// <summary>Vetor de ataque do CVSS — característica da vulnerabilidade, nunca exposição do dispositivo.</summary>
    public static string? AttackVectorLabel(string? vector)
    {
        if (string.IsNullOrWhiteSpace(vector)) return null;
        var av = vector.Split('/').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("AV:", StringComparison.OrdinalIgnoreCase));
        var name = av?.ToUpperInvariant() switch
        {
            "AV:N" => "rede (AV:N)",
            "AV:A" => "rede adjacente (AV:A)",
            "AV:L" => "local (AV:L)",
            "AV:P" => "físico (AV:P)",
            _ => null,
        };
        return name is null ? null
            : $"Vetor de ataque CVSS: {name} — característica da vulnerabilidade; não comprova que o dispositivo seja " +
              "acessível pela rede ou pela internet.";
    }

    public const string EpssMeaning =
        "EPSS: probabilidade GLOBAL de atividade de exploração da CVE nos próximos 30 dias (modelo EPSS), informada pela " +
        "fonte. Não é probabilidade de comprometimento deste ambiente; exibido, sem participar da faixa nem da ordem nesta versão.";

    public const string KnownExploitedNotUsed =
        "O catálogo marca esta CVE como explorada ativamente, mas sem origem verificável registrada (nenhuma fonte de " +
        "inteligência de ameaças integrada): a marca não é usada na decisão.";

    public static string CriticalityLabel(DevicePriorityCriticalityFacts c, string state) => state switch
    {
        DevicePriorityCriticalityStates.Declared =>
            $"Criticidade {c.DeclaredValue} declarada em {CrossSourceCorrelationEvaluator.Utc(c.DeclaredAt!.Value)}" +
            (string.IsNullOrWhiteSpace(c.DeclaredByName) ? "" : $" por {c.DeclaredByName}"),
        DevicePriorityCriticalityStates.Diverged =>
            $"Criticidade não confirmada — o valor cadastrado ({c.StoredValue}) difere do declarado ({c.DeclaredValue})",
        _ => $"Criticidade não confirmada — valor cadastrado {c.StoredValue} sem proveniência" +
             (c.StoredValue == 1 ? " (1 é o valor padrão do modelo para ativos criados pela coleta)" : ""),
    };

    public static string DeviceContextLabel(DevicePriorityAssessment a) => a.DeviceContext switch
    {
        DevicePriorityDeviceContexts.Gap =>
            "Situação entre fontes identificada: " + string.Join(" e ", a.IdentifiedSituations.Select(r => ConditionText(r.Rule.Condition))),
        DevicePriorityDeviceContexts.NoGapInformed => NoGapLabel(a.DeviceReport),
        DevicePriorityDeviceContexts.Partial => "Uma condição informada ausente pelo Intune; a outra sem informação determinada",
        DevicePriorityDeviceContexts.Contradictory => "Registros do Intune contraditórios — contexto não utilizado",
        DevicePriorityDeviceContexts.Conflict => "Vínculo em conflito — contexto do Intune não utilizado",
        DevicePriorityDeviceContexts.NoManagementSource => "Sem registro do Microsoft Intune — contexto de gestão desconhecido",
        _ => "Contexto de gestão desconhecido — " + CrossSourceNarrative.AssociationLabel(a.Correlation.Association.State).ToLowerInvariant(),
    };

    private static string ConditionText(CrossSourceDeviceCondition c) =>
        c == CrossSourceDeviceCondition.Noncompliant ? "dispositivo não conforme segundo o Intune" : "dispositivo sem criptografia segundo o Intune";

    /// <summary>
    /// Nenhuma condição das regras presente, com o estado INFORMADO pela fonte — "não corresponde à condição não conforme"
    /// não vira "conforme": período de carência é dito como tal.
    /// </summary>
    private static string NoGapLabel(DevicePriorityDeviceReport r)
    {
        var compliance = r.Compliance switch
        {
            [DeviceComplianceBucket.Compliant] => "como conforme",
            [DeviceComplianceBucket.InGracePeriod] => "em período de carência de conformidade",
            _ => "com estados de conformidade diferentes entre registros (" +
                 string.Join("; ", r.Compliance.Select(c => AssetCrossSourceNarrative.ComplianceLabel(c))) + ")",
        };
        return $"O Intune informa o dispositivo {compliance} e com criptografia" +
            (r.GracePeriod ? " — carência não é conformidade" : "");
    }

    public const string GracePeriodNote =
        "Período de carência, segundo a fonte: o dispositivo não atende a política e está dentro do prazo concedido por ela. " +
        "Não é conformidade, e a regra XS-DEF-INT-NONCOMPLIANT v1 só considera o estado \"não conforme\" informado pelo " +
        "Intune — por isso não há situação entre fontes nem agravante.";

    /// <summary>Completude da fonte de vulnerabilidades neste dispositivo, dita com a data da aquisição que a sustenta.</summary>
    public static string? AbsenceLabel(DevicePriorityAssessment a)
    {
        var at = a.AbsenceAt is { } w ? $" ({CrossSourceCorrelationEvaluator.Utc(w)})" : "";
        return a.AbsenceState switch
        {
            DevicePriorityAbsenceStates.Conclusive =>
                $"A aquisição mais recente do Microsoft Defender{at} foi completa e observou este dispositivo: o que ela não " +
                "reporta não está em aberto na fonte.",
            DevicePriorityAbsenceStates.ConclusiveAttemptFailed =>
                $"A última aquisição completa publicada do Microsoft Defender{at} observou este dispositivo; a tentativa mais " +
                "recente de coleta falhou, e a conclusão vale para essa aquisição.",
            DevicePriorityAbsenceStates.NotVerifiable =>
                "A ausência de outras vulnerabilidades em aberto não pode ser concluída: " + string.Join(" ", a.AbsenceReasons),
            _ => null,
        };
    }

    public static IReadOnlyList<string> Aggravators(DevicePriorityAssessment a)
    {
        var list = new List<string>();
        if (a.DeviceContext == DevicePriorityDeviceContexts.Gap)
            list.Add("situação entre fontes identificada (" +
                string.Join(" e ", a.IdentifiedSituations.Select(r => $"{r.Rule.Code} v{r.Rule.Version}")) + ")");
        if (a.CriticalityAggravates)
            list.Add($"criticidade {a.Facts.Criticality.DeclaredValue} declarada com proveniência");
        return list;
    }

    /// <summary>O que determinou a posição — sempre a partir de UM caso (nunca fatos de CVEs diferentes combinados).</summary>
    public static string PositionReason(DevicePriorityAssessment a, DevicePriorityCaseFacts? determining)
    {
        if (a.Status != DevicePriorityStatuses.Prioritized || a.Best is not { } b)
        {
            var at = a.AbsenceAt is { } w ? $" ({CrossSourceCorrelationEvaluator.Utc(w)})" : "";
            return a.Status switch
            {
                DevicePriorityStatuses.AllDisposed =>
                    $"Fora da fila: os {a.DispositionCases} caso(s) em aberto atribuível(is) têm disposição registrada; a " +
                    "evidência é preservada." + (a.AbsenceState == DevicePriorityAbsenceStates.NotVerifiable
                        ? " A aquisição mais recente não permite concluir que não haja outros: " + string.Join(" ", a.AbsenceReasons)
                        : ""),
                DevicePriorityStatuses.NoOpenCases => a.AbsenceState == DevicePriorityAbsenceStates.ConclusiveAttemptFailed
                    ? $"A última aquisição completa publicada do Microsoft Defender{at} observou este dispositivo e não reporta " +
                      "vulnerabilidade em aberto atribuível. A tentativa mais recente de coleta falhou; a ausência vale para " +
                      "essa aquisição."
                    : $"A aquisição completa mais recente do Microsoft Defender{at} observou este dispositivo e não reporta " +
                      "vulnerabilidade em aberto atribuível.",
                DevicePriorityStatuses.AbsenceNotVerified =>
                    "Nenhuma vulnerabilidade em aberto está publicada para este dispositivo nesta leitura, e a ausência não " +
                    "pode ser concluída: " + string.Join(" ", a.Reasons),
                _ => a.Reasons.Count > 0 ? string.Join(" ", a.Reasons) : StatusBandLabel(a) + ".",
            };
        }
        var cve = determining?.CveId ?? "a CVE determinante";
        var text = $"{BandLabel(DevicePriorityBands.Of(b.Band))}: determinada por {cve} — severidade técnica " +
            $"{SeverityPhrase(b.SeverityOrder, b.Cvss)} e {ExploitLabel(b.ExploitOrder)}";
        var baseBand = DevicePriorityPolicy.BaseBand(b.SeverityOrder, b.ExploitOrder)!.Value;
        var aggr = Aggravators(a);
        if (a.Aggravated && baseBand > b.Band)
            text += $"; antecipada de Prioridade {baseBand} para Prioridade {b.Band} por {string.Join(" e ", aggr)}" +
                (aggr.Count > 1 ? " (a política antecipa no máximo uma faixa)" : "");
        else if (a.Aggravated)
            text += $"; {string.Join(" e ", aggr)} — já na faixa mais alta, sem antecipação adicional";
        text += ".";
        if (a.BestCount > 1)
            text += $" Outras {a.BestCount - 1} CVE(s) deste dispositivo têm exatamente os mesmos fatores (empate real; a " +
                "determinante é a primeira pelo identificador).";
        return text;
    }

    public static string CaseReason(int severityOrder, int exploitOrder, double? cvss, bool aggravated)
    {
        if (DevicePriorityPolicy.BaseBand(severityOrder, exploitOrder) is not { } baseBand)
            return "Sem CVSS nem severidade informados pela fonte — não priorizável.";
        var final = DevicePriorityPolicy.FinalBand(baseBand, aggravated);
        return $"{SeverityName(severityOrder, cvss)} × {ExploitLabel(exploitOrder)} → Prioridade {baseBand}" +
            (final < baseBand ? $"; contexto comprovado do dispositivo antecipa para Prioridade {final}" : "");
    }

    /// <summary>Próxima ação sugerida, derivada dos fatos — o AEGIS não aplica correção.</summary>
    public static string NextAction(DevicePriorityAssessment a, DevicePriorityCaseFacts? determining)
    {
        switch (a.Status)
        {
            case DevicePriorityStatuses.Prioritized:
                var cve = determining?.CveId ?? "a CVE determinante";
                var text = $"Tratar {cve} neste dispositivo pelo processo de gestão de vulnerabilidades: aplicar a atualização " +
                    "ou mitigação indicada pela fonte e confirmar numa nova coleta do Defender.";
                if (a.DeviceContext == DevicePriorityDeviceContexts.Gap)
                    text += " Revisar também, no mesmo dispositivo, " +
                        string.Join(" e ", a.IdentifiedSituations.Select(r =>
                            r.Rule.Condition == CrossSourceDeviceCondition.Noncompliant
                                ? "as políticas de conformidade não atendidas no Intune"
                                : "a criptografia do armazenamento informada pelo Intune")) + ".";
                if (a.CriticalityState != DevicePriorityCriticalityStates.Declared)
                    text += " Confirmar a criticidade do ativo, se ela for conhecida.";
                return text;
            case DevicePriorityStatuses.AllDisposed:
                return "Nada na fila: os casos em aberto têm disposição registrada. Revise as disposições quando houver nova coleta." +
                    (a.AbsenceState == DevicePriorityAbsenceStates.NotVerifiable
                        ? " A aquisição mais recente não permite concluir que não haja outras vulnerabilidades em aberto neste " +
                          "dispositivo; confirme numa aquisição completa do Microsoft Defender."
                        : "");
            case DevicePriorityStatuses.NoOpenCases:
                return a.AbsenceState == DevicePriorityAbsenceStates.ConclusiveAttemptFailed
                    ? "Nada a tratar por esta política na última aquisição completa publicada. A tentativa mais recente de " +
                      "coleta falhou: verifique a coleta do Microsoft Defender em Integrações. Ausência de vulnerabilidade " +
                      "reportada não comprova que o dispositivo esteja seguro."
                    : "Nada a tratar por esta política na aquisição completa mais recente. Ausência de vulnerabilidade " +
                      "reportada não comprova que o dispositivo esteja seguro.";
            case DevicePriorityStatuses.AbsenceNotVerified:
                return "Não é possível concluir ausência agora: aguarde uma aquisição completa do Microsoft Defender ou verifique " +
                    "a coleta em Integrações. Sem ela, nenhum caso publicado não significa nada a tratar.";
            case DevicePriorityStatuses.NoSourceRecord:
                return "Integre ou verifique a fonte de vulnerabilidades para este dispositivo em Integrações.";
        }
        if (a.Source.State == CrossSourceVulnerabilitySourceStates.AmbiguousAttribution)
            return "Verifique no inventário os registros duplicados do Microsoft Defender deste ativo; a prioridade volta a ser " +
                "calculada na próxima leitura.";
        if (a.Source.State == CrossSourceVulnerabilitySourceStates.NotCurrentlyObserved)
            return "Verifique se o dispositivo ainda está ativo no Microsoft Defender e a coleta em Integrações.";
        if (a.InsufficientCases > 0)
            return "Consulte na fonte o CVSS ou a severidade das CVEs em aberto deste dispositivo.";
        return "Verifique a coleta do Microsoft Defender em Integrações: as observações em aberto são anteriores à política temporal.";
    }

    /// <summary>Informações que PODERIAM alterar a faixa — só as que a política usa e que ainda faltam.</summary>
    public static IReadOnlyList<string> CouldChange(DevicePriorityAssessment a)
    {
        var list = new List<string>();
        if (a.Status == DevicePriorityStatuses.AbsenceNotVerified)
            list.Add("Uma aquisição completa do Microsoft Defender que observe este dispositivo permitiria concluir a ausência " +
                "ou publicaria as vulnerabilidades dele.");
        if (a.Status == DevicePriorityStatuses.Insufficient && a.InsufficientCases > 0)
            list.Add("CVSS ou severidade informados pela fonte para as CVEs sem severidade tornariam esses casos priorizáveis.");
        if (a.Status != DevicePriorityStatuses.Prioritized || a.Best is not { } b) return list;
        if (b.Band > 1 && b.ExploitOrder > DevicePriorityExploit.Verified)
            list.Add("Exploit público ou verificado informado pela fonte para uma CVE deste dispositivo pode antecipar a faixa.");
        if (b.Band > 1 && !a.Aggravated)
        {
            if (a.CriticalityState != DevicePriorityCriticalityStates.Declared)
                list.Add("Criticidade 3 ou 4 declarada com proveniência anteciparia uma faixa.");
            if (a.DeviceContext is not DevicePriorityDeviceContexts.NoGapInformed)
                list.Add("Não conformidade ou falta de criptografia informada pelo Intune, com associação comprovada " +
                    "(situação entre fontes identificada), anteciparia uma faixa.");
            else if (a.DeviceReport.GracePeriod)
                list.Add("Se o período de carência terminar sem conformidade e o Intune passar a informar o dispositivo como " +
                    "não conforme, a situação entre fontes seria identificada e anteciparia uma faixa.");
        }
        if (a.Aggravated && b.Band > 1)
            list.Add("Outro agravante não mudaria a faixa: a política antecipa no máximo uma faixa.");
        return list;
    }

    public static IReadOnlyList<string> Limitations { get; } = new[]
    {
        "Exploração ativa conhecida (ex.: catálogo KEV), ameaça observada no dispositivo e exposição de rede ou internet não " +
        "têm fonte integrada com origem verificável; não participam desta versão da política e podem alterar a avaliação " +
        "quando existirem.",
        "Exploit público ou verificado informado pela fonte indica disponibilidade de exploit — não comprova exploração ativa.",
        "Não conformidade não significa ausência de EDR, e falta de criptografia não comprova que uma CVE seja explorável.",
        "A política não reduz a faixa técnica por criticidade declarada baixa; reduções pertencem a disposições humanas " +
        "registradas (risco aceito, mitigação informada, falso positivo).",
    };

    public static string InformationLabel(DevicePriorityAssessment a, DevicePriorityCaseFacts? determining)
    {
        var unknown = new List<string>();
        if (a.CriticalityState != DevicePriorityCriticalityStates.Declared) unknown.Add("criticidade");
        if (a.DeviceContext is not (DevicePriorityDeviceContexts.Gap or DevicePriorityDeviceContexts.NoGapInformed))
            unknown.Add("contexto de gestão do dispositivo");
        if (determining is { PublicExploit: null, ExploitVerified: null }) unknown.Add("exploit");
        unknown.Add("exploração ativa conhecida");
        unknown.Add("ameaça observada no dispositivo");
        unknown.Add("exposição de rede");
        return $"Informação parcial — {unknown.Count} fator(es) desconhecido(s): {string.Join(", ", unknown)}.";
    }

    // ---- Fatores ----------------------------------------------------------------------------------------------------

    public static IReadOnlyList<DevicePriorityFactorDto> Factors(DevicePriorityAssessment a, DevicePriorityCaseFacts? d)
    {
        var list = new List<DevicePriorityFactorDto>();
        var vulnerabilities = a.Source.Evidence.FirstOrDefault(e => e.OpenEligible > 0);
        var prioritized = a.Status == DevicePriorityStatuses.Prioritized;

        // Evidência: as aquisições elegíveis, com o estado de cada uma.
        var markers = a.EligibleMarkers.Values.SelectMany(m => m).Distinct().OrderBy(m => m).ToList();
        if (markers.Count > 0)
            list.Add(new DevicePriorityFactorDto(
                "evidence", "Vulnerabilidades em aberto observadas",
                $"{a.PrioritizableCases + a.InsufficientCases} caso(s) atribuível(is), " + CrossSourceCorrelationEvaluator.DateSpan(markers),
                DevicePriorityFactorKinds.SourceFact, VulnerabilitiesSource, markers[^1], "Aquisição do AEGIS",
                prioritized ? DevicePriorityEffects.Basis : DevicePriorityEffects.None,
                vulnerabilities is null ? null : CrossSourceNarrative.AcquisitionLabel(
                    CrossSourceCorrelationEvaluator.AcquisitionState(markers[^1], vulnerabilities.Connector))));
        else
        {
            // Sem observação em aberto elegível: o que isso significa depende da COMPLETUDE, não da contagem zero.
            var conclusive = a.AbsenceState is DevicePriorityAbsenceStates.Conclusive or DevicePriorityAbsenceStates.ConclusiveAttemptFailed;
            list.Add(new DevicePriorityFactorDto(
                "evidence", "Vulnerabilidades em aberto observadas",
                conclusive && a.AbsenceAt is { } at
                    ? $"Nenhuma em aberto na aquisição completa de {CrossSourceCorrelationEvaluator.Utc(at)}"
                    : a.AbsenceState == DevicePriorityAbsenceStates.NotVerifiable
                        ? "Nenhum caso publicado nesta leitura — ausência não verificável"
                        : "Nenhuma aquisição elegível",
                conclusive ? DevicePriorityFactorKinds.SourceFact : DevicePriorityFactorKinds.Unknown,
                VulnerabilitiesSource, conclusive ? a.AbsenceAt : null, conclusive ? "Aquisição do AEGIS" : null,
                DevicePriorityEffects.None, AbsenceLabel(a)));
        }

        if (prioritized && a.Best is { } b)
        {
            var sevNote = d is null ? null : SeverityDivergence(d);
            list.Add(new DevicePriorityFactorDto(
                "technicalSeverity", "Severidade técnica (caso determinante)", SeverityLabel(b.SeverityOrder, b.Cvss),
                DevicePriorityFactorKinds.SourceFact, VulnerabilitiesSource, d?.AcquiredAt, "Aquisição do AEGIS",
                DevicePriorityEffects.Determinant,
                (d?.UpdatedOn is { } u ? $"CVE atualizada pela fonte em {CrossSourceCorrelationEvaluator.Utc(u)}. " : "") +
                "Faixa qualitativa oficial do CVSS v3.1; a severidade textual só é usada sem CVSS." +
                (sevNote is null ? "" : " " + sevNote)));
            list.Add(new DevicePriorityFactorDto(
                "exploit", "Exploit (caso determinante)",
                d is null ? ExploitLabel(b.ExploitOrder) : ExploitLabel(d.PublicExploit, d.ExploitVerified),
                d is { PublicExploit: null, ExploitVerified: null } ? DevicePriorityFactorKinds.Unknown : DevicePriorityFactorKinds.SourceFact,
                VulnerabilitiesSource, d?.AcquiredAt, "Aquisição do AEGIS", DevicePriorityEffects.Determinant,
                "Disponibilidade de exploit informada pela fonte; não comprova exploração ativa nem ameaça observada no dispositivo."));
            list.Add(new DevicePriorityFactorDto(
                "epss", "EPSS (caso determinante)",
                d?.Epss is { } e ? $"{Math.Round(e * 100, 1).ToString("0.#", CultureInfo.InvariantCulture)}% (informado pela fonte)" : "Não informado pela fonte",
                d?.Epss is null ? DevicePriorityFactorKinds.Unknown : DevicePriorityFactorKinds.SourceFact,
                VulnerabilitiesSource, d?.AcquiredAt, "Aquisição do AEGIS", DevicePriorityEffects.NotUsed, EpssMeaning));
        }

        list.Add(new DevicePriorityFactorDto(
            "knownExploitation", "Exploração ativa conhecida", "Desconhecida",
            DevicePriorityFactorKinds.Unknown, null, null, null, DevicePriorityEffects.NotUsed,
            (d?.KnownExploited == true ? KnownExploitedNotUsed + " " : "") +
            "Nenhuma fonte de inteligência de ameaças (ex.: catálogo KEV) com origem verificável está integrada."));
        list.Add(new DevicePriorityFactorDto(
            "threatObserved", "Ameaça observada no dispositivo", "Desconhecida",
            DevicePriorityFactorKinds.Unknown, null, null, null, DevicePriorityEffects.NotUsed,
            "Nenhuma fonte integrada atribui alertas ou detecções a este dispositivo; alertas agregados por software ou " +
            "contagens do SIEM não servem para essa atribuição."));
        list.Add(new DevicePriorityFactorDto(
            "networkExposure", "Exposição de rede ou internet", "Desconhecida",
            DevicePriorityFactorKinds.Unknown, null, null, null, DevicePriorityEffects.NotUsed,
            "Vulnerabilidade presente no dispositivo não é exposição de rede: nenhuma fonte integrada informa se o dispositivo " +
            "é acessível pela internet." + (AttackVectorLabel(d?.CvssVector) is { } av ? " " + av : "")));

        var c = a.Facts.Criticality;
        list.Add(new DevicePriorityFactorDto(
            "assetCriticality", "Criticidade do ativo", CriticalityLabel(c, a.CriticalityState),
            a.CriticalityState == DevicePriorityCriticalityStates.Declared
                ? DevicePriorityFactorKinds.Declared : DevicePriorityFactorKinds.Unknown,
            a.CriticalityState == DevicePriorityCriticalityStates.Declared ? "Declaração no AEGIS" : null,
            a.CriticalityState == DevicePriorityCriticalityStates.Declared ? c.DeclaredAt : null,
            a.CriticalityState == DevicePriorityCriticalityStates.Declared ? "Declarada em" : null,
            a.CriticalityAggravates && prioritized ? DevicePriorityEffects.Aggravating
                : a.CriticalityState == DevicePriorityCriticalityStates.Declared ? DevicePriorityEffects.None
                : DevicePriorityEffects.NotUsed,
            a.CriticalityState == DevicePriorityCriticalityStates.Declared
                ? (string.IsNullOrWhiteSpace(c.Note) ? null : $"Justificativa: {c.Note}")
                : "Sem declaração com autor e data, o valor cadastrado não é criticidade confirmada — nem baixa, nem alta. " +
                  "Nome, plataforma ou tipo de servidor não são usados para inferir papel crítico."));

        var gap = a.DeviceContext == DevicePriorityDeviceContexts.Gap;
        var deviceDates = a.IdentifiedSituations.SelectMany(r => r.DeviceAcquisitions).OrderBy(x => x).ToList();
        list.Add(new DevicePriorityFactorDto(
            "deviceManagement", "Situação entre fontes (Defender × Intune)", DeviceContextLabel(a),
            gap ? DevicePriorityFactorKinds.Inferred
                : a.DeviceContext == DevicePriorityDeviceContexts.NoGapInformed ? DevicePriorityFactorKinds.SourceFact
                : DevicePriorityFactorKinds.Unknown,
            a.DeviceContext is DevicePriorityDeviceContexts.NoManagementSource or DevicePriorityDeviceContexts.Unknown
                ? null : "Microsoft Intune",
            deviceDates.Count > 0 ? deviceDates[^1] : null, deviceDates.Count > 0 ? "Aquisição do Intune" : null,
            gap && prioritized ? DevicePriorityEffects.Aggravating
                : a.DeviceContext == DevicePriorityDeviceContexts.NoGapInformed ? DevicePriorityEffects.None
                : DevicePriorityEffects.NotUsed,
            gap
                ? "Coexistência identificada pela regra versionada no mesmo dispositivo; não prova ausência de EDR nem que a " +
                  "CVE seja explorável. Duas situações no mesmo dispositivo contam como UM agravante."
                : a.DeviceReport.GracePeriod
                    ? GracePeriodNote + " " + a.Correlation.Association.Text
                    : a.Correlation.Association.Text));
        return list;
    }

    private static string? SeverityDivergence(DevicePriorityCaseFacts d)
    {
        if (d.Cvss is null || string.IsNullOrWhiteSpace(d.Severity)) return null;
        var fromText = DevicePriorityPolicy.RankOf(null, d.Severity, null, null).SeverityOrder;
        var fromCvss = DevicePriorityPolicy.RankOf(d.Cvss, null, null, null).SeverityOrder;
        return fromText == DevicePrioritySeverity.Unknown || fromText == fromCvss ? null
            : $"A severidade textual da fonte ({SeverityName(fromText, null)}) difere da faixa do CVSS {Cvss(d.Cvss.Value)} " +
              $"({SeverityName(fromCvss, d.Cvss)}); a política usa a faixa do CVSS e mantém a divergência visível.";
    }

    // ---- Política como contrato de leitura --------------------------------------------------------------------------

    public static DevicePriorityPolicyDto Policy(CrossSourcePolicy temporal)
    {
        string B(int s, int e) => $"P{DevicePriorityPolicy.BaseBand(s, e)}";
        var rows = new[]
        {
            (DevicePrioritySeverity.Critical, "Crítica (CVSS 9.0–10.0)"),
            (DevicePrioritySeverity.High, "Alta (CVSS 7.0–8.9)"),
            (DevicePrioritySeverity.Medium, "Média (CVSS 4.0–6.9)"),
            (DevicePrioritySeverity.Low, "Baixa ou nenhuma (CVSS 0.0–3.9)"),
        }.Select(r => new DevicePriorityDecisionRowDto(r.Item2,
            B(r.Item1, DevicePriorityExploit.Verified), B(r.Item1, DevicePriorityExploit.Public),
            B(r.Item1, DevicePriorityExploit.NotInformed))).ToList();

        return new DevicePriorityPolicyDto(
            Code: DevicePriorityPolicy.Code,
            Version: DevicePriorityPolicy.Version,
            Name: "Prioridade de tratamento de vulnerabilidades em dispositivos",
            Rationale:
                "Política operacional do AEGIS, sem validade normativa: parte da severidade técnica (faixa qualitativa oficial " +
                "do CVSS v3.1) e da disponibilidade de exploit informada pela fonte — os dois fatos que as fontes atuais " +
                "sustentam para todo caso — e antecipa uma faixa só com contexto comprovado do dispositivo. Não calcula " +
                "probabilidade nem percentual de risco; falta de dado nunca vira evidência favorável, e sim lacuna declarada.",
            Table: rows,
            Aggravation: new[]
            {
                "Criticidade 3 ou 4 declarada no AEGIS com autor e data (valor padrão ou legado sem proveniência não conta).",
                "Situação entre fontes identificada no mesmo dispositivo (XS-DEF-INT-NONCOMPLIANT v1 ou XS-DEF-INT-UNENCRYPTED v1), " +
                "com associação comprovada pela autoridade de correlação.",
                "Antecipa no máximo UMA faixa, qualquer que seja o número de agravantes; duas situações no mesmo dispositivo " +
                "contam como um agravante; nenhum agravante rebaixa a faixa.",
            },
            TieBreaks: new[]
            {
                "1. Faixa (Prioridade 1 antes de Prioridade 4).",
                "2. Exploit informado pela fonte: verificado, público, não informado.",
                "3. Severidade técnica: crítica, alta, média, baixa.",
                "4. Agravante comprovado antes de nenhum.",
                "5. CVSS informado pela fonte, maior primeiro (ausente por último).",
                "6. Empate real nesses fatores: nome do ativo e identificador — só estabilidade, sem diferença de risco.",
            },
            NotUsed: new[]
            {
                "EPSS: exibido com a semântica oficial (probabilidade global de exploração em 30 dias), fora da faixa e da ordem.",
                "Exploração ativa conhecida, ameaça observada no dispositivo e exposição de rede/internet: sem fonte com origem " +
                "verificável; o vetor CVSS AV:N não comprova acesso pela internet.",
                "Criticidade sem proveniência (inclui o padrão 1 do resolvedor), perfil de impacto e processo de negócio sem " +
                "proveniência, nome do dispositivo e plataforma.",
                "ICR (exige sete fatores numéricos que não existem aqui), risco registrado do ativo, AEGIS Score, KNIGHT e " +
                "Microsoft Secure Score: não entram e não são alterados.",
            },
            Temporal: CrossSourcePolicyDto.From(temporal));
    }

    public static DevicePriorityCriticalityDto CriticalityDto(DevicePriorityCriticalityFacts c)
    {
        var state = c.DeclaredAt is null || c.DeclaredValue is null ? DevicePriorityCriticalityStates.NotConfirmed
            : c.DeclaredValue != c.StoredValue ? DevicePriorityCriticalityStates.Diverged
            : DevicePriorityCriticalityStates.Declared;
        return new DevicePriorityCriticalityDto(c.StoredValue, state, CriticalityLabel(c, state), c.DeclaredValue, c.DeclaredAt,
            string.IsNullOrWhiteSpace(c.DeclaredByName) ? null : c.DeclaredByName, c.Note);
    }
}

// ---- Contratos de leitura -------------------------------------------------------------------------------------------

public static class DevicePriorityFactorKinds
{
    public const string SourceFact = "sourceFact";
    public const string Declared = "declared";
    public const string Inferred = "inferred";
    public const string Unknown = "unknown";
}

public static class DevicePriorityEffects
{
    /// <summary>Base da avaliação (evidência que sustenta os casos).</summary>
    public const string Basis = "basis";
    /// <summary>Determinou a faixa base.</summary>
    public const string Determinant = "determinant";
    /// <summary>Antecipou uma faixa.</summary>
    public const string Aggravating = "aggravating";
    /// <summary>Considerado e não altera a faixa.</summary>
    public const string None = "none";
    /// <summary>Não participa desta versão da política.</summary>
    public const string NotUsed = "notUsed";
}

/// <summary>UM fator com valor/estado, origem, data disponível e natureza (fato da fonte, declarado, inferido, desconhecido).</summary>
public sealed record DevicePriorityFactorDto(
    string Code,
    string Label,
    string Value,
    string Kind,
    string? Source,
    DateTimeOffset? AvailableAt,
    string? AvailableAtLabel,
    string Effect,
    string? Note);

public sealed record DevicePriorityDecisionRowDto(string Severity, string ExploitVerified, string ExploitPublic, string ExploitNotInformed);

public sealed record DevicePriorityPolicyDto(
    string Code,
    int Version,
    string Name,
    string Rationale,
    IReadOnlyList<DevicePriorityDecisionRowDto> Table,
    IReadOnlyList<string> Aggravation,
    IReadOnlyList<string> TieBreaks,
    IReadOnlyList<string> NotUsed,
    CrossSourcePolicyDto Temporal);

public sealed record DevicePriorityCriticalityDto(
    int StoredValue, string State, string Label, int? DeclaredValue, DateTimeOffset? DeclaredAt, string? DeclaredByName, string? Note);

/// <summary>UM caso (ativo × CVE) com a faixa e o motivo — proveniência da aquisição que o sustenta.</summary>
public sealed record DevicePriorityCaseDto(
    string CveId,
    string? Title,
    string Band,
    string BandLabel,
    string Reason,
    string SeverityLabel,
    string? Severity,
    double? CvssScore,
    string ExploitLabel,
    string? AttackVectorLabel,
    double? Epss,
    bool KnownExploitedMarkedWithoutOrigin,
    string Source,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset AcquiredAt,
    string AcquisitionState,
    string AcquisitionLabel);

public sealed record DevicePriorityCasePageDto(int Total, int Page, int PageSize, IReadOnlyList<DevicePriorityCaseDto> Items);

public sealed record DevicePriorityCountDto(string Band, string Label, int Count);

public sealed record DevicePriorityDispositionDto(string Status, string Label, int Count);

public sealed record DevicePrioritySituationDto(string RuleCode, int RuleVersion, string Title, string State, string StateLabel, bool HasCaveats);

/// <summary>Prioridade de UM dispositivo: posição, motivo, fatores, desconhecidos, ressalvas, casos e próxima ação.</summary>
public sealed record AssetDevicePriorityDto(
    Guid AssetId,
    string AssetName,
    bool NameIsPlaceholder,
    DateTimeOffset EvaluatedAt,
    string Heading,
    string Scope,
    DevicePriorityPolicyDto Policy,
    string Status,
    string Band,
    string BandLabel,
    string PositionReason,
    DevicePriorityCaseDto? DeterminingCase,
    string InformationLabel,
    IReadOnlyList<DevicePriorityFactorDto> Factors,
    IReadOnlyList<string> CouldChange,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<CrossSourceNoteDto> Caveats,
    string NextAction,
    DevicePriorityCriticalityDto Criticality,
    IReadOnlyList<DevicePrioritySituationDto> Situations,
    IReadOnlyList<DevicePriorityCountDto> CasesByBand,
    int InsufficientCases,
    IReadOnlyList<DevicePriorityDispositionDto> Dispositions,
    int NoLongerReported,
    int ExcludedOutOfPolicy,
    DevicePriorityCasePageDto? Cases,
    /// <summary>Completude para concluir ausência (<see cref="DevicePriorityAbsenceStates"/>) — distinta da contagem.</summary>
    string AbsenceState,
    string? AbsenceLabel,
    /// <summary>Casos em aberto armazenados nas aquisições elegíveis (unidade: casos ativo × CVE) — o fato, sem conclusão.</summary>
    int StoredOpenCases);

/// <summary>UM dispositivo na fila da Central — aponta o caso que determinou a posição.</summary>
public sealed record DevicePriorityItemDto(
    Guid AssetId,
    string AssetName,
    bool NameIsPlaceholder,
    /// <summary>Posição (1-based) na ordem filtrada — contínua entre páginas.</summary>
    int Position,
    string Status,
    string Band,
    string BandLabel,
    string PositionReason,
    DevicePriorityCaseDto? DeterminingCase,
    int PrioritizableCases,
    IReadOnlyList<DevicePriorityCountDto> CasesByBand,
    int InsufficientCases,
    int DispositionCases,
    IReadOnlyList<string> Aggravators,
    string DeviceContextLabel,
    string CriticalityLabel,
    string InformationLabel,
    IReadOnlyList<CrossSourceNoteDto> Caveats,
    string NextAction,
    /// <summary>Outros dispositivos AVALIADOS com exatamente os mesmos fatores de ordenação (empate real).</summary>
    int TiedAssets);

public static class DevicePriorityReadingStates
{
    /// <summary>Não há integração do Microsoft Defender (vulnerabilidades).</summary>
    public const string NoSource = "NoSource";

    /// <summary>A integração existe, mas ainda não publicou aquisição por dispositivo.</summary>
    public const string NeverCollected = "NeverCollected";

    public const string Available = "Available";
}

public sealed record DevicePrioritySummaryDto(
    string ReadingState,
    string? ReadingNote,
    /// <summary>Dispositivos com vulnerabilidade em aberto na fonte avaliada (unidade: dispositivos) — a população.</summary>
    int CandidateAssets,
    int AssetsEvaluated,
    bool EvaluationTruncated,
    /// <summary>Com recorte: faixas (P1…) completas — nenhum dispositivo não avaliado pode pertencer a elas. Nulo sem recorte.</summary>
    string? CompleteThroughBand,
    string? TruncationNote,
    /// <summary>Dispositivos por faixa do caso determinante + não priorizáveis (unidade: dispositivos).</summary>
    IReadOnlyList<DevicePriorityCountDto> AssetsByBand,
    /// <summary>Casos priorizáveis por faixa + não priorizáveis (unidade: casos ativo × CVE).</summary>
    IReadOnlyList<DevicePriorityCountDto> CasesByBand,
    IReadOnlyList<DevicePriorityDispositionDto> Dispositions,
    /// <summary>Integrações de vulnerabilidades de outros provedores, fora do escopo desta versão.</summary>
    int OutOfScopeSources,
    string? OutOfScopeNote,
    /// <summary>
    /// Suficiência da coleta para concluir AUSÊNCIA fora da fila (<see cref="DevicePriorityAbsenceStates"/>) — distinta de
    /// <see cref="CandidateAssets"/>: zero candidatos com publicação parcial ou não concluída não é "nada a tratar".
    /// </summary>
    string AbsenceState,
    string? AbsenceNote);

public sealed record DevicePriorityListDto(
    DateTimeOffset EvaluatedAt,
    string Heading,
    string Scope,
    DevicePriorityPolicyDto Policy,
    DevicePrioritySummaryDto Summary,
    string? BandFilter,
    IReadOnlyList<DevicePriorityItemDto> Items,
    /// <summary>Total FILTRADO (unidade: dispositivos).</summary>
    int Total,
    int Page,
    int PageSize);

public sealed record DevicePriorityFilter(string? Band = null, int Page = 1, int PageSize = 10);

/// <summary>
/// Leitura tenant-scoped (Global Query Filter fail-closed) da prioridade de tratamento em dispositivos. Somente leitura:
/// nunca coleta, nunca escreve, nunca aciona IA e nunca altera score ou fila existente.
/// </summary>
public interface IDevicePriorityQuery
{
    /// <summary>Fila de dispositivos em ordem de prioridade (candidatos selecionados ANTES da paginação).</summary>
    Task<DevicePriorityListDto> ListAsync(DevicePriorityFilter filter, CancellationToken ct = default);

    /// <summary>Prioridade de UM ativo com os casos paginados; <c>null</c> quando o ativo não existe no tenant.</summary>
    Task<AssetDevicePriorityDto?> GetForAssetAsync(Guid assetId, int casePage, int casePageSize, CancellationToken ct = default);
}
