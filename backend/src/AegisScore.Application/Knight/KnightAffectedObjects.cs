using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

// ============================================================================
//  [AEGIS-MVP-PRODUCT-02] Objetos AFETADOS de um achado KNIGHT
// ============================================================================
// O contrato AGREGADO do KNIGHT (fatos tipados, score, cobertura) permanece SEM PII e é a autoridade do
// veredito. Este arquivo acrescenta uma superfície SEPARADA: os objetos que sustentam um achado, para que o
// analista consiga sair de "78 objetos privilegiados" e chegar em "quais são, e por que cada um entrou".
//
// Três regras que este contrato existe para não deixar quebrar:
//   1. LISTA ≠ ACUSAÇÃO. O conjunto é material de REVISÃO. Estar na lista de AK-ENTRA-002 significa
//      "tem papel privilegiado", não "deve perder o acesso".
//   2. LISTA ≠ CENSO DE PESSOAS. Um membro de papel privilegiado pode ser aplicação/service principal ou
//      grupo. Por isso o tipo do objeto é EXPLÍCITO e nunca se presume "usuário".
//   3. NOME AUSENTE NÃO SE INVENTA. Quando a fonte não devolve nome/UPN (permissão, tipo de objeto ou
//      campo simplesmente ausente), o objeto aparece pelo identificador externo, com a limitação declarada.

/// <summary>
/// UM objeto que sustenta um achado, como o COLETOR o observou. Somente o mínimo necessário para o analista
/// reconhecer o objeto e agir sobre ele na própria fonte: identificador externo, tipo, nome/UPN quando a
/// fonte efetivamente os devolveu, papéis associados quando aplicável e o fato que o colocou na lista.
/// </summary>
/// <param name="ExternalId">Identificador do objeto NA FONTE (ex.: object id do diretório). Nunca vazio.</param>
/// <param name="Kind">Natureza do objeto — jamais presumida como pessoa.</param>
/// <param name="DisplayName">Nome de exibição, SOMENTE quando a fonte o devolveu. <c>null</c> nunca vira "—" aqui.</param>
/// <param name="UserPrincipalName">UPN/e-mail principal, SOMENTE quando aplicável ao tipo e devolvido pela fonte.</param>
/// <param name="Roles">Papéis associados ao objeto, quando a coleta os conhece (ex.: papéis privilegiados).</param>
/// <param name="Detail">
/// O fato que sustenta a inclusão (ex.: "sem método de MFA registrado", "sem sinal de atividade desde …").
/// É uma constatação da coleta — não uma conclusão sobre a pessoa.
/// </param>
public sealed record KnightAffectedObjectFact(
    string ExternalId,
    KnightAffectedObjectKind Kind,
    string? DisplayName = null,
    string? UserPrincipalName = null,
    IReadOnlyList<string>? Roles = null,
    string? Detail = null);

/// <summary>
/// O conjunto de objetos que sustenta UM sinal coletado, com sua COMPLETUDE. A completude é parte da prova:
/// uma lista truncada por falha de página não pode parecer o conjunto inteiro, e o consumidor precisa saber
/// disso antes de tratar a lista como "todos os afetados".
/// </summary>
/// <param name="Signal">Sinal ao qual o conjunto pertence — é o sinal, não o indicador, que o coletor conhece.</param>
/// <param name="Objects">Objetos observados, já deduplicados pela MESMA regra que produziu a contagem.</param>
/// <param name="IsComplete">
/// <c>false</c> quando a coleta não conseguiu enumerar tudo (falha de página, truncamento, campo ausente).
/// Uma lista incompleta continua útil — desde que declarada como incompleta.
/// </param>
/// <param name="Limitation">O que exatamente ficou de fora ou não pôde ser lido. Sanitizado, sem segredo.</param>
public sealed record KnightAffectedObjectEvidence(
    KnightSignalKey Signal,
    IReadOnlyList<KnightAffectedObjectFact> Objects,
    bool IsComplete = true,
    string? Limitation = null);

/// <summary>
/// Mapa EXPLÍCITO entre o sinal coletado e o indicador cujo detalhe ele sustenta. Existe porque o coletor
/// conhece SINAIS (contrato provider-neutral) e a tela conhece INDICADORES: sem este mapa, o coletor teria de
/// importar o catálogo e passaria a decidir veredito — exatamente o acoplamento que o KNIGHT evita.
///
/// O escopo é fechado de propósito ([AEGIS-MVP-PRODUCT-02] cobre três achados). Um sinal fora do mapa
/// simplesmente não tem detalhe preservado, e a tela diz isso — nunca mostra uma lista de outro achado.
/// </summary>
public static class KnightAffectedObjectScope
{
    private static readonly IReadOnlyDictionary<KnightSignalKey, string> BySignal =
        new Dictionary<KnightSignalKey, string>
        {
            // O conjunto de objetos com papel privilegiado é o MESMO que produz a contagem total.
            [KnightSignalKey.PrivilegedAccountsTotal] = "AK-ENTRA-002",
            // Subconjunto: privilegiados que o cruzamento com o relatório de registro de MFA sinalizou.
            [KnightSignalKey.PrivilegedAccountsWithoutMfa] = "AK-ENTRA-001",
            // Convidados que a regra de atividade sinalizou.
            [KnightSignalKey.InactiveGuestAccounts] = "AK-ENTRA-004",
        };

    /// <summary>Indicador que recebe o detalhe deste sinal, ou <c>null</c> quando o sinal está fora do escopo.</summary>
    public static string? IndicatorFor(KnightSignalKey signal) =>
        BySignal.TryGetValue(signal, out var id) ? id : null;

    /// <summary>Indicadores que PODEM ter detalhe preservado nesta entrega (ordem estável).</summary>
    public static IReadOnlyList<string> Indicators { get; } =
        BySignal.Values.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

    public static bool IsInScope(string indicatorId) => Indicators.Contains(indicatorId);
}

// ---- Leitura (paginada e pesquisável NO SERVIDOR) ------------------------------------------------------

/// <summary>Um objeto afetado na visão de LEITURA — o que a API devolve, já vinculado à avaliação.</summary>
public sealed record KnightAffectedObjectView(
    string ExternalId,
    KnightAffectedObjectKind Kind,
    string? DisplayName,
    string? UserPrincipalName,
    IReadOnlyList<string> Roles,
    string? Detail);

/// <summary>
/// Estado do DETALHE de um achado numa avaliação. Distingue três coisas que a UI não pode confundir:
/// o achado não tem detalhe nesta entrega; a avaliação é ANTERIOR à preservação (histórico legítimo, sem
/// retropreenchimento); ou há detalhe — completo ou declaradamente parcial.
/// </summary>
public enum KnightAffectedDetailState
{
    /// <summary>Este indicador não preserva objetos afetados (fora do escopo desta entrega).</summary>
    OutOfScope = 0,

    /// <summary>
    /// A avaliação é anterior à preservação de detalhe — o resultado histórico permanece VÁLIDO e intocado.
    /// Jamais preenchido com a lista atual: seria apresentar o presente como prova do passado.
    /// </summary>
    NotPreserved = 1,

    /// <summary>Detalhe preservado e completo para o conjunto que produziu a contagem.</summary>
    Available = 2,

    /// <summary>Detalhe preservado, porém declaradamente incompleto (a coleta não enumerou tudo).</summary>
    Partial = 3,
}

/// <summary>Página de objetos afetados de UM achado de UMA avaliação (paginação e busca feitas no servidor).</summary>
/// <param name="RunId">Avaliação à qual a lista pertence — o vínculo que impede mostrar o presente como passado.</param>
/// <param name="IndicatorId">Achado ao qual a lista pertence.</param>
/// <param name="State">Estado do detalhe (fora de escopo, não preservado, disponível, parcial).</param>
/// <param name="AffectedObjectCount">Contagem VERBATIM do veredito — a mesma unidade e regra de dedupe da lista.</param>
/// <param name="TotalPreserved">Objetos efetivamente preservados (igual à contagem quando completo).</param>
/// <param name="MatchCount">Objetos que satisfazem a busca (igual a <paramref name="TotalPreserved"/> sem busca).</param>
/// <param name="Page">Página 1-based devolvida.</param>
/// <param name="PageSize">Tamanho de página efetivamente aplicado.</param>
/// <param name="Items">Os objetos da página.</param>
/// <param name="Limitation">O que a coleta não conseguiu enumerar/ler, quando aplicável.</param>
/// <param name="CollectedAt">Instante da coleta que originou esta lista.</param>
public sealed record KnightAffectedObjectsPage(
    Guid RunId,
    string IndicatorId,
    KnightAffectedDetailState State,
    int AffectedObjectCount,
    int TotalPreserved,
    int MatchCount,
    int Page,
    int PageSize,
    IReadOnlyList<KnightAffectedObjectView> Items,
    string? Limitation,
    DateTimeOffset? CollectedAt)
{
    /// <summary>Tamanho de página padrão da tabela de afetados.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>Teto de página: a lista NUNCA é carregada inteira no navegador para depois ser filtrada.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Página vazia para um achado sem detalhe — preserva a contagem do veredito.</summary>
    public static KnightAffectedObjectsPage Without(
        Guid runId, string indicatorId, KnightAffectedDetailState state, int affectedCount,
        int page, int pageSize, string? limitation, DateTimeOffset? collectedAt) =>
        new(runId, indicatorId, state, affectedCount, 0, 0, page, pageSize,
            Array.Empty<KnightAffectedObjectView>(), limitation, collectedAt);
}
