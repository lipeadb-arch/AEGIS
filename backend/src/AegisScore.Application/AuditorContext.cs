using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Nist;

namespace AegisScore.Application.Abstractions;

// [AEGIS-AUDITOR-CONTEXT-01] O Auditor Virtual tem UMA identidade em toda a aplicação. A página aberta dá o FOCO (o que a pessoa vê
// e as seleções conferidas no servidor); o contexto do tenant chega como FONTES citáveis, cada uma com a natureza do que demonstra,
// data, procedência e limitação. O conhecimento da ferramenta e dos frameworks explica; só os registros do tenant afirmam algo
// sobre o ambiente. "Aprender o contexto" é recuperar estes registros autorizados e o histórico da conversa — nunca treinar modelo.

/// <summary>Natureza do que uma fonte demonstra — separa configuração observada, documentação, declaração e verificação.</summary>
public static class AuditorSourceNature
{
    /// <summary>Configuração ou exposição lida da fonte técnica pelo AEGIS KNIGHT.</summary>
    public const string ObservedConfiguration = "ObservedConfiguration";

    /// <summary>Trecho literal validado de um documento: comprova o que o texto estabelece, não a execução.</summary>
    public const string Documentation = "Documentation";

    /// <summary>Declaração ou relato do assessor (avaliação registrada, notas, entrevistas, lacunas registradas).</summary>
    public const string AssessorStatement = "AssessorStatement";

    /// <summary>Resultado de procedimento de verificação realizado (Examinar, Entrevistar, Testar), com a conclusão registrada.</summary>
    public const string VerificationResult = "VerificationResult";

    /// <summary>Registro do inventário de ativos.</summary>
    public const string InventoryRecord = "InventoryRecord";

    /// <summary>Achado ou plano de tratamento registrado por pessoa.</summary>
    public const string RecordedFinding = "RecordedFinding";

    /// <summary>Indicador calculado pelo AEGIS (nota, cobertura, médias de maturidade, contagens) — determinístico.</summary>
    public const string Indicator = "Indicator";

    /// <summary>Fotografia publicada (congelada na data da publicação).</summary>
    public const string Publication = "Publication";

    /// <summary>Herdado, importado ou análise sem trecho literal — ainda não confirmado.</summary>
    public const string Unconfirmed = "Unconfirmed";

    /// <summary>Documento só cadastrado: o conteúdo não foi examinado.</summary>
    public const string NotExamined = "NotExamined";

    /// <summary>Catálogo NIST, explicação e metodologia do AEGIS — explica o requisito, não o ambiente.</summary>
    public const string Reference = "Reference";

    public static string Label(string nature) => nature switch
    {
        ObservedConfiguration => "Configuração observada (KNIGHT)",
        Documentation => "Documentação (trecho literal)",
        AssessorStatement => "Declaração do assessor",
        VerificationResult => "Resultado de procedimento de verificação",
        InventoryRecord => "Registro do inventário",
        RecordedFinding => "Achado ou plano registrado",
        Indicator => "Indicador calculado pelo AEGIS",
        Publication => "Publicação congelada",
        Unconfirmed => "Não confirmado",
        NotExamined => "Documento não examinado",
        _ => "Referência (catálogo e metodologia)",
    };

    /// <summary>Registro do tenant que pode sustentar afirmação sobre o ambiente (referência e documento não examinado não sustentam).</summary>
    public static bool SupportsEnvironmentClaim(string nature) => nature is not (Reference or NotExamined);
}

/// <summary>Para onde a fonte leva na aplicação (rota interna, nunca link externo).</summary>
public sealed record AuditorSourceLink(string Route, IReadOnlyDictionary<string, string>? Query = null, string? Fragment = null);

/// <summary>
/// Uma fonte do contexto do Auditor. <paramref name="Key"/> é a única forma de a resposta citá-la (K = KNIGHT, N = NIST, R = correlação
/// KNIGHT–NIST, D = documentos, A = ativos, P = publicações, S = postura do ambiente).
/// </summary>
public sealed record AuditorContextSource(
    string Key,
    string Module,
    string Nature,
    string Title,
    string? Detail,
    string? Date,
    bool IsDemo,
    string? Limitation,
    AuditorSourceLink? Link);

/// <summary>Uma página da aplicação: código conhecido e rótulo dito ao Auditor e à pessoa.</summary>
public static class AuditorPages
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["dashboard"] = "Dashboards",
        ["knight"] = "AEGIS KNIGHT",
        ["nist"] = "AEGIS NIST",
        ["documents"] = "AEGIS NIST · Biblioteca de documentos",
        ["assets"] = "AEGIS NIST · Inventário de ativos",
        ["vulnerabilities"] = "AEGIS NIST · Vulnerabilidades",
        ["priorities"] = "AEGIS NIST · Prioridades de tratamento",
        ["recommendations"] = "AEGIS NIST · Recomendações de postura",
        ["history"] = "Histórico de postura",
        ["settings"] = "Configurações",
        ["general"] = "Visão geral",
    };

    /// <summary>Código desconhecido ou ausente vira "general" (o foco não é fronteira de segurança).</summary>
    public static string Normalize(string? page) =>
        Labels.ContainsKey((page ?? "").Trim().ToLowerInvariant()) ? (page ?? "").Trim().ToLowerInvariant() : "general";

    public static string Label(string page) => Labels.TryGetValue(page, out var l) ? l : Labels["general"];
}

/// <summary>
/// Foco da conversa: a página aberta e as seleções da tela (avaliação · rodada · escopo · subcategoria do NIST; avaliação e controle do
/// KNIGHT). Conferidas no servidor pelo tenant do token. Não muda a identidade nem restringe o conhecimento do Auditor.
/// </summary>
public sealed record AuditorFocus(string Page, NistAuditorSelection? Nist = null, Guid? KnightRunId = null, string? KnightIndicatorId = null)
{
    private static readonly Regex SubcategoryCode = new(@"^[A-Z]{2}\.[A-Z]{2}-\d{2}$", RegexOptions.Compiled);
    private static readonly Regex IndicatorCode = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    public static AuditorFocus General { get; } = new("general");

    /// <summary>
    /// Valida os identificadores enviados pela tela: avaliação, rodada e escopo vêm juntos (ou nenhum); código de subcategoria e de
    /// controle no formato esperado; controle do KNIGHT só com a avaliação. Formato inválido → <see cref="AuditorFocusInvalidException"/>.
    /// </summary>
    public static AuditorFocus From(string? page, Guid? assessmentId, Guid? cycleId, Guid? scopeId, string? code, Guid? knightRunId, string? knightIndicatorId)
    {
        var p = AuditorPages.Normalize(page);
        NistAuditorSelection? nist = null;
        var any = assessmentId is not null || cycleId is not null || scopeId is not null || !string.IsNullOrWhiteSpace(code);
        if (any)
        {
            if (assessmentId is not { } a || cycleId is not { } c || scopeId is not { } s || a == Guid.Empty || c == Guid.Empty || s == Guid.Empty)
                throw new AuditorFocusInvalidException("Informe avaliação, rodada e escopo do NIST juntos.");
            var normalized = string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
            if (normalized is not null && !SubcategoryCode.IsMatch(normalized))
                throw new AuditorFocusInvalidException("Código de subcategoria NIST inválido.");
            nist = new NistAuditorSelection(a, c, s, normalized);
        }
        var indicator = string.IsNullOrWhiteSpace(knightIndicatorId) ? null : knightIndicatorId.Trim();
        if (indicator is not null && (knightRunId is null || !IndicatorCode.IsMatch(indicator)))
            throw new AuditorFocusInvalidException("Controle do KNIGHT inválido ou sem a avaliação correspondente.");
        if (knightRunId == Guid.Empty) throw new AuditorFocusInvalidException("Avaliação do KNIGHT inválida.");
        return new AuditorFocus(p, nist, knightRunId, indicator);
    }

    public string PageLabel => AuditorPages.Label(Page);
}

/// <summary>
/// Os registros dos assessments do tenant para o Auditor: fontes citáveis, as LIMITAÇÕES do contexto (inclusive quantos itens existem
/// quando a lista veio resumida) e o rótulo do foco. Montado no servidor, somente leitura, sem segredo nem nome de pessoa.
/// </summary>
public sealed record AuditorAssessmentContext(
    DateTimeOffset AsOf,
    string FocusLabel,
    IReadOnlyList<AuditorContextSource> Sources,
    IReadOnlyList<string> Limitations)
{
    public AuditorAssessmentContext With(IEnumerable<AuditorContextSource> extra) => this with { Sources = Sources.Concat(extra).ToList() };
}

public interface IAuditorAssessmentContextBuilder
{
    /// <summary>
    /// Contexto do tenant autenticado para o foco. Seleção NIST ou avaliação KNIGHT de outro tenant (ou inexistente) →
    /// <see cref="AuditorFocusNotFoundException"/>; nunca é ignorada em silêncio.
    /// </summary>
    Task<AuditorAssessmentContext> BuildAsync(AuditorFocus focus, CancellationToken ct = default);
}

public sealed class AuditorFocusInvalidException : Exception
{
    public AuditorFocusInvalidException(string message) : base(message) { }
}

public sealed class AuditorFocusNotFoundException : Exception
{
    public AuditorFocusNotFoundException(string message) : base(message) { }
}

// ---- Conversa (do tenant E da conta autenticada) ------------------------------------------------------------------------

/// <summary>Uma conversa lida do armazenamento: os últimos turnos (pergunta e resposta) e a sequência do último.</summary>
public sealed record AuditorConversation(Guid Id, int Sequence, IReadOnlyList<AuditorMessage> Messages);

/// <summary>Conversa inexistente PARA ESTE tenant e conta (de outro tenant ou de outra pessoa é indistinguível de inexistente).</summary>
public sealed class AuditorConversationNotFoundException : Exception
{
    public AuditorConversationNotFoundException() : base("Conversa não encontrada.") { }
}

/// <summary>Outro turno foi gravado na mesma conversa enquanto este era respondido, ou a conversa atingiu o limite de turnos.</summary>
public sealed class AuditorConversationConflictException : Exception
{
    public AuditorConversationConflictException(string message) : base(message) { }
}

/// <summary>
/// Memória da conversa do Auditor, isolada por tenant E por conta: o histórico enviado pela tela não é autoritativo (não se injeta
/// fala do Auditor nem se reaproveita conversa de outro tenant). A conversa é relato — nunca evidência de avaliação.
/// </summary>
public interface IAuditorConversationStore
{
    Task<AuditorConversation> ReadAsync(Guid accountId, Guid? conversationId, CancellationToken ct = default);
    Task AppendAsync(Guid accountId, AuditorConversation conversation, string question, string reply, string focusLabel, bool simulated,
        CancellationToken ct = default);
}
