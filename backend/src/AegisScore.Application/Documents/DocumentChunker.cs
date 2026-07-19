using System.Globalization;
using System.Text;

namespace AegisScore.Application.Documents;

/// <summary>
/// Seleção de TRECHO dirigida ao controle — o "context-aware chunking" do RAG documental.
///
/// Uma política corporativa tem dezenas de páginas e o parágrafo que prova PR.AA-01 são cinco linhas.
/// Mandar o documento inteiro a cada controle custa tokens em proporção ao lixo e, pior, DILUI a atenção
/// do modelo: quanto mais texto irrelevante, mais fácil o LLM ancorar num trecho que não é evidência do
/// controle avaliado. Aqui o documento é quebrado em parágrafos, cada um pontuado pela sobreposição com
/// os termos do controle, e só os melhores viajam.
///
/// Determinístico e sem embeddings de propósito: o ranking precisa ser auditável (um auditor tem de
/// poder explicar POR QUE aquele trecho foi escolhido) e o projeto não carrega dependência de modelo de
/// vetor. Sobreposição léxica resolve bem o caso — documentos de governança repetem o vocabulário do
/// framework ("MFA", "acesso privilegiado", "revisão periódica").
/// </summary>
public static class DocumentChunker
{
    /// <summary>Comprimento mínimo para um parágrafo valer ranqueamento (abaixo disso é título/ruído).</summary>
    private const int MinParagraphLength = 40;

    /// <summary>
    /// Fração do melhor score que um parágrafo precisa atingir para entrar no trecho. Corta a cauda de
    /// parágrafos que casam um termo solto e só estariam ali porque sobrou orçamento.
    /// </summary>
    private const double RelevanceFloor = 0.34;

    /// <summary>
    /// Palavras curtas e conectivos que aparecem em todo parágrafo e não discriminam nada. Sem esta
    /// lista, "de/da/para" dominariam a pontuação e o ranking viraria sorteio.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "para", "com", "que", "dos", "das", "por", "uma", "não", "nos", "nas", "ser", "seu", "sua",
        "the", "and", "for", "with", "this", "that", "from", "are", "shall", "must",
    };

    /// <summary>
    /// Extrai do documento os trechos mais aderentes aos termos do controle, respeitando um orçamento
    /// de caracteres. Devolve o documento inteiro (truncado no orçamento) quando nada casa — é o
    /// comportamento honesto: sem sinal léxico, deixar o modelo ler o começo é melhor que entregar vazio.
    /// </summary>
    /// <param name="documentText">Texto cru extraído do PDF/TXT.</param>
    /// <param name="controlTerms">
    /// Vocabulário do controle: código NIST, descrição do outcome e os <c>evidence_requirements</c>.
    /// </param>
    /// <param name="charBudget">Teto de caracteres do trecho devolvido (orçamento de tokens).</param>
    public static string SelectRelevantExcerpt(
        string documentText, IReadOnlyList<string> controlTerms, int charBudget) =>
        SelectRelevantExcerpt(documentText, controlTerms, Array.Empty<string>(), charBudget);

    /// <summary>
    /// Variante com vocabulário em DUAS FAIXAS de peso, que é como o RAG dirigido deve chamar.
    ///
    /// A distinção não é cosmética. As <c>evaluation_metrics</c> e <c>evidence_requirements</c> do
    /// 800-53 são prosa de GRC recheada de termos genéricos — "registro", "execução", "cobertura",
    /// "responsável" — que aparecem em QUALQUER seção de QUALQUER política. Num teste ao vivo, esse
    /// vocabulário fez o trecho de RC.RP-01 (continuidade) vir com a seção de contas privilegiadas
    /// junto, e o motor creditou cobertura ao controle errado: falso positivo de conformidade, o defeito
    /// mais caro deste produto. O que discrimina o assunto é o vocabulário PRÓPRIO do controle — o
    /// código e o outcome do catálogo.
    /// </summary>
    /// <param name="primaryTerms">Código NIST + outcome da subcategoria. Peso alto — definem o assunto.</param>
    /// <param name="supportingTerms">Métricas e critérios da regra. Peso baixo — confirmam, não definem.</param>
    public static string SelectRelevantExcerpt(
        string documentText, IReadOnlyList<string> primaryTerms,
        IReadOnlyList<string> supportingTerms, int charBudget)
    {
        if (string.IsNullOrWhiteSpace(documentText))
            return "";
        if (charBudget <= 0 || documentText.Length <= charBudget)
            return documentText.Trim();

        var primary = BuildVocabulary(primaryTerms);
        // Um termo que está nas duas faixas conta como primário — a faixa alta manda.
        var supporting = BuildVocabulary(supportingTerms);
        supporting.ExceptWith(primary);

        if (primary.Count == 0 && supporting.Count == 0)
            return Truncate(documentText, charBudget);

        var paragraphs = SplitParagraphs(documentText).ToList();

        // Peso por RARIDADE (IDF). Sem isso, termos genéricos das regras do 800-53 — "registro",
        // "responsável", "revisão", que aparecem em quase todo parágrafo de uma política — dominam a
        // pontuação e arrastam seções alheias para dentro do trecho. Foi exatamente o que aconteceu num
        // teste ao vivo: o trecho de RC.RP-01 (continuidade) veio com a seção de contas privilegiadas
        // junto, e o motor creditou cobertura ao controle errado. Termo que está em todo lugar não
        // discrimina nada; o que aparece em poucos parágrafos é o que aponta o assunto.
        var documentFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paragraphs)
        foreach (var token in Tokenize(p).Distinct(StringComparer.OrdinalIgnoreCase))
            documentFrequency[token] = documentFrequency.GetValueOrDefault(token) + 1;

        // Pontua cada parágrafo; empate resolve pela ORDEM ORIGINAL (documentos de governança são
        // escritos do geral para o específico, então o trecho anterior costuma ser o mais definidor).
        var ranked = paragraphs
            .Select((text, index) => (
                text, index,
                score: ScoreOf(text, primary, supporting, documentFrequency, paragraphs.Count)))
            .Where(p => p.score > 0)
            .OrderByDescending(p => p.score)
            .ThenBy(p => p.index)
            .ToList();

        if (ranked.Count == 0)
            return Truncate(documentText, charBudget);

        // PISO DE RELEVÂNCIA relativo ao melhor parágrafo. Sem ele, o orçamento generoso vira convite:
        // quando os trechos realmente pertinentes somam 400 caracteres e o teto é 6.000, TODO parágrafo
        // com um único termo em comum entra na carona. Foi assim que a seção de contas privilegiadas se
        // infiltrou no trecho de RC.RP-01 num teste ao vivo — não por estar mal ranqueada, mas por haver
        // espaço sobrando. Orçamento é TETO, nunca cota a preencher.
        var cutoff = ranked[0].score * RelevanceFloor;
        ranked = ranked.Where(p => p.score >= cutoff).ToList();

        // Reordena os selecionados pela posição no documento: o modelo lê a política na sequência em que
        // ela foi escrita, não em ordem de placar — a narrativa importa para julgar intenção.
        var picked = new List<(string text, int index)>();
        var used = 0;
        foreach (var p in ranked)
        {
            if (used + p.text.Length > charBudget) continue;
            picked.Add((p.text, p.index));
            used += p.text.Length;
            if (used >= charBudget) break;
        }

        if (picked.Count == 0)
            return Truncate(ranked[0].text, charBudget);

        return string.Join(
            "\n[…]\n",
            picked.OrderBy(p => p.index).Select(p => p.text));
    }

    /// <summary>Termos significativos do controle, normalizados e sem stop words.</summary>
    private static HashSet<string> BuildVocabulary(IReadOnlyList<string> controlTerms)
    {
        var vocabulary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in controlTerms ?? Array.Empty<string>())
        foreach (var token in Tokenize(term))
            vocabulary.Add(token);
        return vocabulary;
    }

    /// <summary>
    /// Sobreposição de termos DISTINTOS (a contagem bruta premiaria a repetição), cada um pesado pela
    /// sua raridade no documento — IDF clássico. Um termo presente em todos os parágrafos vale ~0; um
    /// que aparece em um só vale o máximo.
    /// </summary>
    private static double ScoreOf(
        string paragraph, HashSet<string> primary, HashSet<string> supporting,
        IReadOnlyDictionary<string, int> documentFrequency, int paragraphCount)
    {
        const double PrimaryWeight = 4.0;      // vocabulário próprio do controle: define o assunto
        const double SupportingWeight = 1.0;   // vocabulário genérico de GRC: confirma, não define

        var score = 0.0;
        foreach (var token in Tokenize(paragraph).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var weight = primary.Contains(token) ? PrimaryWeight
                       : supporting.Contains(token) ? SupportingWeight
                       : 0.0;
            if (weight == 0.0) continue;

            var df = documentFrequency.GetValueOrDefault(token, 1);
            score += weight * Math.Log(1.0 + (double)paragraphCount / df);
        }
        return score;
    }

    private static IEnumerable<string> Tokenize(string text) =>
        RemoveDiacritics(text)
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '(', ')', '[', ']', '/', '-', '"', '\'' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2 && !StopWords.Contains(t));

    /// <summary>
    /// Remove acentos para casar "revisão" com "revisao". Documento e catálogo vêm de fontes distintas
    /// (PDF de cliente × JSON do 800-53) e a acentuação diverge o tempo todo.
    /// </summary>
    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static IEnumerable<string> SplitParagraphs(string text) =>
        text.Split(new[] { "\r\n\r\n", "\n\n", "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length >= MinParagraphLength);

    private static string Truncate(string text, int budget) =>
        text.Length <= budget ? text.Trim() : text[..budget].Trim();
}
