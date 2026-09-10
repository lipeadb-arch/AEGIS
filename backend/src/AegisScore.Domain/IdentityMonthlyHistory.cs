using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-ADM-02] Histórico MENSAL do ADM de identidade
// ============================================================================
// O que muda em relação ao ADM-01: as aquisições respondem "o que foi observado NAQUELA coleta", e o detalhe
// operacional delas tem prazo. Faltava a pergunta do meio — "como esta origem evoluiu ao longo dos meses?" —
// que não pode depender do detalhe (ele expira) nem ser reconstruída do cadastro atual (isso seria apresentar
// o presente como prova do passado).
//
// A consolidação mensal é uma FOTOGRAFIA ESCOLHIDA, não um agregado calculado. Três decisões que este modelo
// existe para não deixar colapsar:
//
//   • FOTOGRAFIA × SOMA. O mês guarda os valores de UMA aquisição — a última daquele mês que produziu dados.
//     Somar as populações de coletas sucessivas contaria as MESMAS identidades repetidamente, e um mês com
//     duas coletas pareceria o dobro de um mês com uma.
//   • ÚLTIMO DADO VÁLIDO × ÚLTIMA TENTATIVA. São perguntas diferentes, com respostas que divergem justamente
//     quando importa (o mês em que a integração quebrou). A última tentativa é preservada em separado,
//     inclusive quando falhou.
//   • SEM COLETA × SEM OBJETOS. Um mês sem nenhuma aquisição é gravado como tal, sem conjuntos e sem
//     fotografia — nunca com zero, que significaria "olhamos e não havia ninguém".
//
// Esta consolidação NÃO é o relatório publicado e congelado do produto, não tem hash, não é comprovação e não
// participa de score, cobertura ou veredito.

/// <summary>
/// A consolidação de UM mês de calendário UTC para UMA origem — <c>(tenant, provedor, namespace do diretório)</c>.
///
/// Carrega a sua PRÓPRIA cópia agregada da proveniência e dos valores por conjunto, e é isso que a faz
/// sobreviver de forma independente à retenção do detalhe e à remoção da aquisição que a originou: o histórico
/// de 12 meses não pode depender de uma evidência operacional cujo prazo é de 90 dias.
///
/// Chave natural <c>(TenantId, Provider, DirectoryNamespace, Month)</c>.
/// </summary>
public class IdentityMonthlyRollup : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Provedor da origem consolidada.</summary>
    public KnightSourceType Provider { get; set; } = KnightSourceType.MicrosoftEntraId;

    /// <summary>
    /// NAMESPACE real do diretório. Faz parte da chave pelo mesmo motivo que faz parte da identidade no
    /// ADM-01: dois diretórios distintos observam objetos distintos, e misturá-los num só mês inventaria uma
    /// população que nunca foi observada junta.
    /// </summary>
    public string DirectoryNamespace { get; set; } = "";

    /// <summary>Primeiro dia do mês de calendário UTC consolidado (o mês é a unidade; o dia é sempre 1).</summary>
    public DateOnly Month { get; set; }

    /// <summary>
    /// TRUE enquanto este for o mês CORRENTE: ele ainda pode receber coletas, e apresentá-lo como fechado
    /// faria uma leitura parcial passar por consolidada.
    /// </summary>
    public bool IsProvisional { get; set; }

    // ---- Quantas coletas houve no mês (o denominador honesto do que se está olhando) ----
    //
    // ⚠️ SEMÂNTICA HISTÓRICA, e não "quantas linhas sobraram". Depois que a retenção remove as aquisições
    // vencidas do mês, contar as sobreviventes faria o passado ENCOLHER sozinho: um mês com dez coletas
    // passaria a exibir uma, e a série sugeriria uma queda de atividade que nunca houve. O número abaixo é
    // mantido como (sobreviventes + já removidas pela retenção) — as removidas ficam contadas em
    // <see cref="RetiredAcquisitionCount"/>, e a soma não muda quando uma migra de um lado para o outro.
    // É também por isso que uma reconsolidação não incrementa nada: ela recontabiliza as duas parcelas, em
    // vez de somar de novo o que já estava somado.

    /// <summary>Aquisições registradas neste mês, tenham produzido dados ou não. Zero = mês sem coleta.</summary>
    public int AcquisitionCount { get; set; }

    /// <summary>Quantas daquelas aquisições produziram dados legíveis (completos ou declaradamente parciais).</summary>
    public int DataProducingCount { get; set; }

    /// <summary>
    /// Quantas das aquisições contadas acima já foram REMOVIDAS por inteiro pela retenção. É a parcela que
    /// não pode mais ser recontada a partir das linhas, e existe para que o histórico do mês não dependa da
    /// existência da evidência operacional que o originou.
    /// </summary>
    public int RetiredAcquisitionCount { get; set; }

    /// <summary>Quantas das removidas haviam produzido dados — a mesma conservação aplicada ao outro contador.</summary>
    public int RetiredDataProducingCount { get; set; }

    /// <summary>
    /// Instante de aquisição até o qual a retenção JÁ VARREU este mês (<c>null</c> = nunca varrido).
    ///
    /// É a memória BOUNDED que impede o repovoamento silencioso: uma aquisição removida por inteiro deixa de
    /// existir como linha, e uma reapresentação do mesmo identificador cairia no caminho de criação como se
    /// fosse coleta nova. Guardar identificadores removidos seria um registro de deduplicação sem limite;
    /// guardar a FRONTEIRA do que já foi varrido custa uma coluna por mês e responde à mesma pergunta —
    /// "esta coleta é anterior ao que a retenção já processou aqui?".
    /// </summary>
    public DateTimeOffset? RetentionSweptThroughAt { get; set; }

    // ---- FOTOGRAFIA do mês: a ÚLTIMA aquisição que produziu dados -----------------------------------

    /// <summary>
    /// Aquisição escolhida como fotografia do mês. <c>null</c> quando nenhuma coleta do mês produziu dados —
    /// e nesse caso o mês NÃO recebe valores emprestados de outro mês.
    ///
    /// Deliberadamente SEM chave estrangeira: a aquisição tem prazo de retenção próprio, e uma FK obrigaria a
    /// escolher entre travar a retenção e destruir o histórico. A consolidação já guarda o que precisa.
    /// </summary>
    public Guid? SnapshotAcquisitionId { get; set; }

    public DateTimeOffset? SnapshotAcquiredAt { get; set; }

    /// <summary>Horário OBSERVADO segundo a fonte, quando ela o forneceu. Jamais copiado do horário da coleta.</summary>
    public DateTimeOffset? SnapshotObservedAt { get; set; }

    /// <summary>Conector que executou a coleta escolhida — proveniência, não chave.</summary>
    public Guid? SnapshotConnectorConfigId { get; set; }

    /// <summary>Rótulo legível da fonte na coleta escolhida.</summary>
    public string? SnapshotSourceLabel { get; set; }

    /// <summary>Versão do contrato canônico aplicada à coleta escolhida — eixo de COMPARABILIDADE entre meses.</summary>
    public string? SnapshotSchemaVersion { get; set; }

    /// <summary>Versão da normalização aplicada à coleta escolhida — o outro eixo de comparabilidade.</summary>
    public string? SnapshotNormalizationVersion { get; set; }

    /// <summary>Estado da coleta escolhida (Completed/PartialCollection). Uma parcial PERMANECE parcial.</summary>
    public KnightSourceState? SnapshotState { get; set; }

    // ---- ÚLTIMA TENTATIVA do mês, preservada em separado ---------------------------------------------

    /// <summary>Última aquisição do mês, tenha ela produzido dados ou não. <c>null</c> só em mês sem coleta.</summary>
    public Guid? LastAttemptAcquisitionId { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Estado da última tentativa — é aqui que uma integração quebrada aparece.</summary>
    public KnightSourceState? LastAttemptState { get; set; }

    /// <summary>Motivo sanitizado da última tentativa. Sem token, segredo ou payload.</summary>
    public string? LastAttemptDetail { get; set; }

    // ---- Proveniência da própria consolidação --------------------------------------------------------

    /// <summary>Instante em que esta linha foi (re)consolidada — o frescor do que se está lendo.</summary>
    public DateTimeOffset ConsolidatedAt { get; set; }

    /// <summary>Versão da REGRA de consolidação aplicada — distingue "a origem mudou" de "nós mudamos a regra".</summary>
    public string ConsolidationVersion { get; set; } = "";

    /// <summary>Valores por conjunto da fotografia do mês. Vazio quando não houve fotografia.</summary>
    public ICollection<IdentityMonthlyRollupSet> Sets { get; set; } = new List<IdentityMonthlyRollupSet>();

    /// <summary>True quando alguma coleta do mês produziu dados — o que autoriza ler <see cref="Sets"/>.</summary>
    public bool HasData => SnapshotAcquisitionId is not null;
}

/// <summary>
/// Os valores de UM conjunto na fotografia do mês, copiados INTEIROS da aquisição escolhida. A completude vem
/// junto de propósito: sem ela, um mês parcial e um mês completo com o mesmo número seriam indistinguíveis, e
/// a série sugeriria uma melhora que ninguém observou.
///
/// Chave natural <c>(TenantId, RollupId, Set)</c>.
/// </summary>
public class IdentityMonthlyRollupSet : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    public Guid RollupId { get; set; }
    public IdentityMonthlyRollup? Rollup { get; set; }

    public IdentityObservationSet Set { get; set; }

    /// <summary>Desfecho da coleta DESTE conjunto na fotografia — independente dos demais.</summary>
    public IdentityObservationSetOutcome Outcome { get; set; } = IdentityObservationSetOutcome.NotAttempted;

    /// <summary>Quantidade APURADA pela coleta escolhida. Só é um número comparável quando o desfecho é Collected.</summary>
    public int ObservedCount { get; set; }

    /// <summary>Observações que aquela coleta preservou — a contagem ORIGINAL, não o detalhe ainda disponível.</summary>
    public int PreservedCount { get; set; }

    /// <summary>True somente quando a lista preservada ERA o conjunto inteiro no momento da coleta.</summary>
    public bool IsComplete { get; set; }

    /// <summary>O que ficou de fora ou não pôde ser lido naquela coleta (sanitizado).</summary>
    public string? Limitation { get; set; }
}
