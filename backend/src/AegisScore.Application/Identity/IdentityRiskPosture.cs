using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AegisScore.Application.Knight;

namespace AegisScore.Application.Identity;

// ============================================================================
//  [AEGIS-MVP-MICROSOFT-COVERAGE-03] Postura de RISCO DE IDENTIDADE (agregados)
// ============================================================================
// Vocabulário PROVIDER-NEUTRAL para a postura operacional de risco de identidade. Nenhum tipo do Microsoft
// Graph atravessa para Domain/Application: o conector traduz os valores da fonte nestes buckets e só os
// AGREGADOS chegam aqui.
//
// PRIVACIDADE (invariante do pacote): NADA que identifique uma pessoa entra nestes contratos — nem id de
// usuário, nome, userPrincipalName, IP, localização, requestId, correlationId, additionalInfo, user agent,
// token, segredo ou payload bruto. Os campos pessoais existem apenas TRANSITORIAMENTE no JSON devolvido pelo
// Graph e morrem na normalização. Só contagens, distribuições e carimbos de tempo atravessam.
//
// AUTORIDADE: estes fatos são CONSULTIVOS. Não são EvidenceSignal, não alteram TenantControlState/ledger, não
// concedem nem retiram pontos do AEGIS Score, e NÃO entram na fórmula do KNIGHT Score nesta entrega. Risco é
// uma fotografia dinâmica: a AUSÊNCIA de detecções NÃO prova que um controle esteja eficaz.

/// <summary>
/// Nível de risco NORMALIZADO. <see cref="Hidden"/> é semanticamente distinto de <see cref="None"/>: a fonte
/// OCULTOU o nível (tipicamente por limitação de licença), o que não é "sem risco". <see cref="Unknown"/>
/// cobre <c>unknownFutureValue</c>, valores futuros e ausência do campo — nunca colapsa em "seguro".
/// </summary>
public enum IdentityRiskLevel
{
    High = 0,
    Medium = 1,
    Low = 2,
    None = 3,
    Hidden = 4,
    Unknown = 5,
}

/// <summary>
/// Estado NORMALIZADO de um risco. Separa explicitamente o que continua EM ABERTO (<see cref="AtRisk"/>,
/// <see cref="ConfirmedCompromised"/>) do que foi RESOLVIDO (<see cref="Remediated"/>,
/// <see cref="Dismissed"/>, <see cref="ConfirmedSafe"/>). <see cref="Unknown"/> recebe
/// <c>unknownFutureValue</c>/valores novos e NUNCA é lido como resolvido nem descartado em silêncio.
/// </summary>
public enum IdentityRiskState
{
    AtRisk = 0,
    ConfirmedCompromised = 1,
    Remediated = 2,
    Dismissed = 3,
    ConfirmedSafe = 4,
    None = 5,
    Unknown = 6,
}

/// <summary>Momento da detecção (tempo real × processamento posterior), quando a fonte informa.</summary>
public enum IdentityRiskDetectionTiming
{
    Realtime = 0,
    NearRealtime = 1,
    Offline = 2,
    NotDefined = 3,
    Unknown = 4,
}

/// <summary>
/// Janelas temporais DETERMINÍSTICAS da coleta de risco. São constantes do produto — a referência de "agora"
/// vem sempre de um <see cref="TimeProvider"/> injetado, nunca de <c>DateTimeOffset.UtcNow</c> espalhado por
/// regras e testes.
/// </summary>
public static class IdentityRiskWindows
{
    /// <summary>Janela principal de detecções consideradas "recentes" (30 dias).</summary>
    public const int DetectionWindowDays = 30;

    /// <summary>Sub-janela de destaque para a UI (7 dias) — subconjunto da janela principal.</summary>
    public const int RecentDetectionWindowDays = 7;

    /// <summary>Quantidade máxima de tipos de detecção reportados (o restante é somado em "outros").</summary>
    public const int TopDetectionTypes = 8;
}

/// <summary>
/// Traduz os valores CRUS da fonte nos buckets provider-neutral. Puro, determinístico e defensivo: qualquer
/// valor desconhecido, futuro ou ausente cai em <c>Unknown</c> — nunca em "seguro" e nunca descartado.
/// </summary>
public static class IdentityRiskVocabulary
{
    /// <summary>Comprimento máximo de um rótulo de tipo de detecção normalizado.</summary>
    private const int MaxTypeLength = 48;

    /// <summary>Rótulo usado quando o tipo é ausente, vazio ou não sanitizável.</summary>
    public const string UnknownType = "unknown";

    /// <summary>Rótulo agregador dos tipos além do teto de <see cref="IdentityRiskWindows.TopDetectionTypes"/>.</summary>
    public const string OtherTypes = "other";

    /// <summary>
    /// Tipo devolvido pelo Microsoft Entra ID Protection quando a detecção é PREMIUM e o tenant não tem
    /// licença para ver o detalhe — o evento existe, mas a categoria é suprimida. Representado honestamente.
    /// </summary>
    public const string GenericPremiumType = "generic";

    public static IdentityRiskLevel LevelOf(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "high" => IdentityRiskLevel.High,
        "medium" => IdentityRiskLevel.Medium,
        "low" => IdentityRiskLevel.Low,
        "none" => IdentityRiskLevel.None,
        "hidden" => IdentityRiskLevel.Hidden,
        _ => IdentityRiskLevel.Unknown,
    };

    public static IdentityRiskState StateOf(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "atrisk" => IdentityRiskState.AtRisk,
        "confirmedcompromised" => IdentityRiskState.ConfirmedCompromised,
        "remediated" => IdentityRiskState.Remediated,
        "dismissed" => IdentityRiskState.Dismissed,
        "confirmedsafe" => IdentityRiskState.ConfirmedSafe,
        "none" => IdentityRiskState.None,
        _ => IdentityRiskState.Unknown,
    };

    public static IdentityRiskDetectionTiming TimingOf(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "realtime" => IdentityRiskDetectionTiming.Realtime,
        "nearrealtime" => IdentityRiskDetectionTiming.NearRealtime,
        "offline" => IdentityRiskDetectionTiming.Offline,
        "notdefined" => IdentityRiskDetectionTiming.NotDefined,
        _ => IdentityRiskDetectionTiming.Unknown,
    };

    /// <summary>
    /// Normaliza um rótulo categórico da fonte (tipo de detecção, método de autenticação) num slug seguro:
    /// minúsculo, só letras/dígitos/<c>_</c>/<c>-</c>, truncado. Um valor vazio ou totalmente não sanitizável
    /// vira <see cref="UnknownType"/>. Isto é uma CATEGORIA, jamais um identificador de pessoa.
    /// </summary>
    public static string CategoryOf(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return UnknownType;
        var sb = new StringBuilder(Math.Min(raw.Length, MaxTypeLength));
        foreach (var ch in raw.Trim())
        {
            if (sb.Length >= MaxTypeLength) break;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch is '_' or '-') sb.Append(ch);
        }
        return sb.Length == 0 ? UnknownType : sb.ToString();
    }
}

/// <summary>Distribuição por NÍVEL de risco. <c>Hidden</c> e <c>Unknown</c> ficam à parte de <c>None</c>.</summary>
public sealed record IdentityRiskLevelDistribution(
    long High = 0, long Medium = 0, long Low = 0, long None = 0, long Hidden = 0, long Unknown = 0)
{
    public static IdentityRiskLevelDistribution Empty { get; } = new();

    public long Total => High + Medium + Low + None + Hidden + Unknown;

    public IdentityRiskLevelDistribution With(IdentityRiskLevel level) => level switch
    {
        IdentityRiskLevel.High => this with { High = High + 1 },
        IdentityRiskLevel.Medium => this with { Medium = Medium + 1 },
        IdentityRiskLevel.Low => this with { Low = Low + 1 },
        IdentityRiskLevel.None => this with { None = None + 1 },
        IdentityRiskLevel.Hidden => this with { Hidden = Hidden + 1 },
        _ => this with { Unknown = Unknown + 1 },
    };
}

/// <summary>
/// Distribuição por ESTADO de risco, com os agregados operacionais derivados. <see cref="Active"/> é o que
/// EXIGE INVESTIGAÇÃO (atRisk + confirmedCompromised); <see cref="Resolved"/> é o que já foi tratado; o
/// bucket <c>Unknown</c> NUNCA entra em nenhum dos dois — ele é reportado como desconhecido.
/// </summary>
public sealed record IdentityRiskStateDistribution(
    long AtRisk = 0, long ConfirmedCompromised = 0, long Remediated = 0,
    long Dismissed = 0, long ConfirmedSafe = 0, long None = 0, long Unknown = 0)
{
    public static IdentityRiskStateDistribution Empty { get; } = new();

    public long Total => AtRisk + ConfirmedCompromised + Remediated + Dismissed + ConfirmedSafe + None + Unknown;

    /// <summary>Em aberto: exige investigação. Estado desconhecido NÃO é somado aqui (nem em Resolved).</summary>
    public long Active => AtRisk + ConfirmedCompromised;

    /// <summary>Tratado: remediado, descartado ou confirmado seguro.</summary>
    public long Resolved => Remediated + Dismissed + ConfirmedSafe;

    public IdentityRiskStateDistribution With(IdentityRiskState state) => state switch
    {
        IdentityRiskState.AtRisk => this with { AtRisk = AtRisk + 1 },
        IdentityRiskState.ConfirmedCompromised => this with { ConfirmedCompromised = ConfirmedCompromised + 1 },
        IdentityRiskState.Remediated => this with { Remediated = Remediated + 1 },
        IdentityRiskState.Dismissed => this with { Dismissed = Dismissed + 1 },
        IdentityRiskState.ConfirmedSafe => this with { ConfirmedSafe = ConfirmedSafe + 1 },
        IdentityRiskState.None => this with { None = None + 1 },
        _ => this with { Unknown = Unknown + 1 },
    };
}

/// <summary>Contagem de uma categoria (tipo de detecção / método de autenticação) — rótulo sanitizado.</summary>
public sealed record IdentityRiskCategoryCount(string Category, long Count);

/// <summary>
/// Inventário AGREGADO de usuários marcados em risco pelo provedor de identidade. Nenhuma linha por usuário:
/// só contagens e distribuições. <see cref="IsComplete"/> falso significa que a paginação NÃO terminou
/// (falha intermediária ou teto operacional) — os números são um piso, jamais a verdade total.
/// </summary>
public sealed record IdentityRiskyUserFacts(
    /// <summary>Entradas efetivamente normalizadas (inclui as de usuários já excluídos).</summary>
    long Total,
    /// <summary>Entradas cujo usuário já foi EXCLUÍDO do diretório — fora das distribuições e dos KPIs ativos.</summary>
    long Deleted,
    /// <summary>Entradas com reavaliação de risco em andamento no provedor (o estado pode mudar).</summary>
    long Processing,
    IdentityRiskLevelDistribution Levels,
    IdentityRiskStateDistribution States,
    /// <summary>Em aberto E de nível alto — o subconjunto mais urgente. Nunca inferido de ausência.</summary>
    long HighRiskActive,
    /// <summary>Instante mais recente de atualização de risco — usado só para frescor/agregação.</summary>
    DateTimeOffset? MostRecentRiskUpdateAt,
    bool IsComplete)
{
    /// <summary>Usuários vivos que exigem investigação (atRisk + confirmedCompromised).</summary>
    public long Active => States.Active;

    /// <summary>Usuários vivos considerados no inventário (exclui os excluídos do diretório).</summary>
    public long Live => States.Total;
}

/// <summary>
/// Detecções de risco AGREGADAS dentro de uma janela temporal determinística. Nenhum evento individual é
/// persistido: nem IP, nem localização, nem identificador de requisição/correlação/usuário.
/// </summary>
public sealed record IdentityRiskDetectionFacts(
    int WindowDays,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    /// <summary>Detecções DENTRO da janela — o denominador de todas as distribuições abaixo.</summary>
    long TotalInWindow,
    /// <summary>Detecções lidas porém FORA da janela — reportadas, nunca contadas como recentes.</summary>
    long OutsideWindow,
    /// <summary>Detecções sem instante utilizável — não podem ser situadas na janela; contadas à parte.</summary>
    long Undated,
    /// <summary>Subconjunto da janela detectado nos últimos <see cref="IdentityRiskWindows.RecentDetectionWindowDays"/> dias.</summary>
    long InRecentWindow,
    IdentityRiskLevelDistribution Levels,
    IdentityRiskStateDistribution States,
    long Realtime,
    long NearRealtime,
    long Offline,
    long TimingNotDefined,
    long TimingUnknown,
    /// <summary>
    /// Detecções cuja CATEGORIA foi suprimida pela fonte (tipo <c>generic</c>) — indício direto de detalhe
    /// limitado por licença. O evento é real; o que falta é a classificação.
    /// </summary>
    long PremiumDetailWithheld,
    IReadOnlyList<IdentityRiskCategoryCount> TopTypes,
    DateTimeOffset? MostRecentDetectionAt,
    bool IsComplete)
{
    /// <summary>Detecções em aberto na janela (atRisk + confirmedCompromised).</summary>
    public long Active => States.Active;

    /// <summary>Detecções resolvidas na janela (remediated + dismissed + confirmedSafe).</summary>
    public long Resolved => States.Resolved;
}

/// <summary>
/// Postura de risco de identidade de UMA coleta: DUAS capacidades INDEPENDENTES, cada uma com seu desfecho
/// tipado e seus fatos. Uma capacidade em 403/licença/429/timeout/5xx NÃO invalida a outra — os campos são
/// separados de propósito. O instante de avaliação vem do relógio INJETADO da coleta.
/// </summary>
public sealed record IdentityRiskPosture(
    KnightCapabilityOutcome RiskyUsersOutcome,
    string? RiskyUsersDetail,
    IdentityRiskyUserFacts? RiskyUsers,
    KnightCapabilityOutcome RiskDetectionsOutcome,
    string? RiskDetectionsDetail,
    IdentityRiskDetectionFacts? RiskDetections,
    DateTimeOffset EvaluatedAt)
{
    /// <summary>True quando ao menos uma das duas capacidades produziu fatos (mesmo parciais).</summary>
    public bool HasAnyFacts => RiskyUsers is not null || RiskDetections is not null;
}

/// <summary>
/// Postura AGREGADA de registro de métodos de autenticação, derivada do relatório
/// <c>reports/authenticationMethods/userRegistrationDetails</c> que o AEGIS já consome com
/// <c>AuditLog.Read.All</c>.
///
/// [DECISÃO DE ARQUITETURA — AEGIS-MVP-MICROSOFT-COVERAGE-03] <c>UserAuthenticationMethod.Read.All</c> NÃO faz
/// parte deste pacote e NÃO é permissão obrigatória. Iterar <c>GET /users/{id}/authentication/methods</c> por
/// toda a população para auditoria seria um N+1 (uma chamada por usuário), aumentaria a exposição de dados
/// pessoais e o custo operacional sem ganho para uma visão agregada — e a Microsoft não recomenda esse uso.
/// A ampliação aqui é feita no relatório JÁ AUTORIZADO, apenas acrescentando campos ao <c>$select</c> da
/// MESMA consulta paginada: zero chamadas adicionais, zero permissões adicionais.
/// </summary>
public sealed record IdentityAuthenticationPosture(
    long TotalUsers,
    long MfaCapable,
    long MfaRegistered,
    long PasswordlessCapable,
    /// <summary>Usuários cujo <c>isMfaCapable</c> veio ausente/inválido — não contam como capazes nem incapazes.</summary>
    long CapabilityUnknown,
    /// <summary>Métodos registrados na população, por categoria sanitizada (um usuário pode ter vários).</summary>
    IReadOnlyList<IdentityRiskCategoryCount> MethodsRegistered,
    bool IsComplete)
{
    /// <summary>Cobertura (%) de capacidade de MFA — <c>null</c> quando não há denominador (nunca 0 por ausência).</summary>
    public double? MfaCapableCoveragePercent =>
        TotalUsers > 0 ? Math.Round(100.0 * MfaCapable / TotalUsers, 1, MidpointRounding.AwayFromZero) : null;

    /// <summary>Cobertura (%) de capacidade sem senha — <c>null</c> quando não há denominador.</summary>
    public double? PasswordlessCoveragePercent =>
        TotalUsers > 0 ? Math.Round(100.0 * PasswordlessCapable / TotalUsers, 1, MidpointRounding.AwayFromZero) : null;
}

/// <summary>
/// Acumulador PURO das distribuições de risco. Isola a contagem da E/S para que a semântica (bucket
/// desconhecido preservado, excluído fora dos KPIs ativos, alto+ativo cruzado) seja testável sem HTTP.
/// </summary>
public sealed class IdentityRiskAccumulator
{
    private IdentityRiskLevelDistribution _levels = IdentityRiskLevelDistribution.Empty;
    private IdentityRiskStateDistribution _states = IdentityRiskStateDistribution.Empty;
    private readonly Dictionary<string, long> _categories = new(StringComparer.Ordinal);

    public long Total { get; private set; }
    public long HighRiskActive { get; private set; }
    public DateTimeOffset? MostRecent { get; private set; }

    public IdentityRiskLevelDistribution Levels => _levels;
    public IdentityRiskStateDistribution States => _states;

    /// <summary>Contabiliza uma entrada normalizada. Nível e estado JÁ vêm traduzidos para os buckets neutros.</summary>
    public void Add(IdentityRiskLevel level, IdentityRiskState state, DateTimeOffset? at, string? category = null)
    {
        Total++;
        _levels = _levels.With(level);
        _states = _states.With(state);

        // "Alto + em aberto" é o cruzamento que a operação persegue — nunca inferido de ausência de dado.
        if (level == IdentityRiskLevel.High && state is IdentityRiskState.AtRisk or IdentityRiskState.ConfirmedCompromised)
            HighRiskActive++;

        if (at is { } stamp && (MostRecent is null || stamp > MostRecent)) MostRecent = stamp;

        if (category is not null)
        {
            var key = IdentityRiskVocabulary.CategoryOf(category);
            _categories[key] = _categories.TryGetValue(key, out var n) ? n + 1 : 1;
        }
    }

    /// <summary>Contagem de uma categoria específica já acumulada (0 quando ausente).</summary>
    public long CategoryCount(string category) =>
        _categories.TryGetValue(category, out var n) ? n : 0;

    /// <summary>
    /// Top categorias por contagem (desempate estável pelo nome), com o excedente somado em
    /// <see cref="IdentityRiskVocabulary.OtherTypes"/> — a soma dos itens NUNCA perde eventos.
    /// </summary>
    public IReadOnlyList<IdentityRiskCategoryCount> TopCategories(int top)
    {
        if (_categories.Count == 0) return Array.Empty<IdentityRiskCategoryCount>();

        var ordered = _categories
            .Select(kv => new IdentityRiskCategoryCount(kv.Key, kv.Value))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Category, StringComparer.Ordinal)
            .ToList();

        if (top <= 0 || ordered.Count <= top) return ordered;

        var head = ordered.Take(top).ToList();
        var rest = ordered.Skip(top).Sum(c => c.Count);
        if (rest > 0) head.Add(new IdentityRiskCategoryCount(IdentityRiskVocabulary.OtherTypes, rest));
        return head;
    }

    /// <summary>Formata um instante em ISO-8601 UTC — usado só em diagnóstico determinístico.</summary>
    public static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
