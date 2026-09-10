using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Application.Identity.Adm;

// ============================================================================
//  [AEGIS-ADM-01] Contratos CANÔNICOS do ADM de identidade
// ============================================================================
// Estes contratos são o que atravessa a fronteira entre a coleta e a persistência. Deliberadamente NÃO
// dependem do catálogo de indicadores, de IDs "AK-ENTRA" nem de qualquer enum de scoring: uma observação
// válida existe sem mapping NIST, e a INGESTÃO não decide conformidade.
//
// A semântica dos campos segue, de forma SELETIVA, o que OCSF, UDM e ECS já resolveram bem: separar o horário
// em que o dado foi RECEBIDO do horário em que o fato foi OBSERVADO; identificar a origem por um namespace
// próprio, e não só pelo identificador do objeto; e declarar o tipo do ator em vez de presumi-lo. Não há aqui
// nenhuma alegação de compatibilidade com esses schemas — nada disso foi implementado nem validado.

/// <summary>
/// Dimensão de garantia de autenticação que uma observação evidencia. Existe para que o produto pare de dizer
/// "MFA" para quatro coisas diferentes:
///   • um método capaz de MFA está REGISTRADO na conta (capacidade);
///   • uma política EXIGE MFA para aquele acesso (exigência);
///   • a autenticação efetivamente APLICOU o segundo fator (aplicação);
///   • o método resiste a phishing (qualidade do método).
///
/// Nada nesta entrega infere uma dimensão a partir de outra: um registro presente não prova aplicação, e uma
/// política existente não prova que ela alcançou aquela conta.
/// </summary>
public enum IdentityAuthenticationAssurance
{
    /// <summary>Nenhuma dimensão de autenticação é evidenciada por este conjunto.</summary>
    None = 0,

    /// <summary>Registro/CAPACIDADE de MFA na conta, conforme o relatório de registro do diretório.</summary>
    RegisteredCapability = 1,

    /// <summary>EXIGÊNCIA de MFA por política (acesso condicional/baseline).</summary>
    PolicyRequirement = 2,

    /// <summary>APLICAÇÃO EFETIVA do segundo fator numa autenticação real.</summary>
    EffectiveEnforcement = 3,

    /// <summary>RESISTÊNCIA A PHISHING do método (FIDO2/certificado/Windows Hello).</summary>
    PhishingResistance = 4,
}

/// <summary>
/// Descrição de um conjunto observado: o que ele É, qual dimensão de autenticação ele evidencia e — o mais
/// importante — o que NÃO se pode concluir dele. O catálogo existe para que essa honestidade fique no código,
/// e não apenas no comentário de quem escreveu a coleta.
/// </summary>
/// <param name="Set">O conjunto descrito.</param>
/// <param name="Label">Rótulo legível da POPULAÇÃO (nunca "todas as identidades").</param>
/// <param name="Assurance">Dimensão de autenticação evidenciada, quando alguma.</param>
/// <param name="DoesNotProve">O que a presença/ausência neste conjunto NÃO demonstra.</param>
public sealed record IdentityObservationSetDescriptor(
    IdentityObservationSet Set,
    string Label,
    IdentityAuthenticationAssurance Assurance,
    string DoesNotProve);

/// <summary>
/// Catálogo dos conjuntos deste recorte. Fechado de propósito — cobrir mais populações exigiria coletar mais,
/// e este pacote não acrescenta nenhuma chamada ao diretório.
/// </summary>
public static class IdentityObservationSetCatalog
{
    private static readonly IReadOnlyDictionary<IdentityObservationSet, IdentityObservationSetDescriptor> BySet =
        new[]
        {
            new IdentityObservationSetDescriptor(
                IdentityObservationSet.PrivilegedRoleMember,
                "Objetos com papel privilegiado no diretório",
                IdentityAuthenticationAssurance.None,
                "Não é o censo das identidades do tenant, e estar na lista não significa que o acesso seja indevido."),

            new IdentityObservationSetDescriptor(
                IdentityObservationSet.PrivilegedWithoutRegisteredMfaCapability,
                "Objetos privilegiados sem método capaz de MFA registrado",
                IdentityAuthenticationAssurance.RegisteredCapability,
                "Não demonstra que a autenticação ocorre sem segundo fator, que nenhuma política exige MFA, "
                + "nem diz nada sobre resistência a phishing."),

            new IdentityObservationSetDescriptor(
                IdentityObservationSet.InactiveGuest,
                "Convidados sinalizados pela regra de atividade da coleta",
                IdentityAuthenticationAssurance.None,
                "Não demonstra que a conta esteja desativada nem que o acesso tenha sido revogado."),
        }.ToDictionary(d => d.Set);

    public static IdentityObservationSetDescriptor Describe(IdentityObservationSet set) =>
        BySet.TryGetValue(set, out var d)
            ? d
            : new IdentityObservationSetDescriptor(set, set.ToString(), IdentityAuthenticationAssurance.None,
                "Conjunto sem descrição catalogada — nada pode ser concluído dele.");

    /// <summary>Conjuntos deste recorte, em ordem estável.</summary>
    public static IReadOnlyList<IdentityObservationSet> All { get; } =
        BySet.Keys.OrderBy(s => (int)s).ToList();
}

/// <summary>
/// UM objeto observado por uma aquisição, no vocabulário do ADM. É o mínimo necessário para reconhecer o
/// objeto na origem, correlacioná-lo entre conjuntos e explicar por que ele entrou — nada além disso.
/// </summary>
/// <param name="ExternalId">Identificador do objeto NA FONTE. Compõe a identidade; nunca vazio.</param>
/// <param name="Kind">Natureza do objeto, jamais presumida como pessoa.</param>
/// <param name="DisplayName">Nome de exibição SOMENTE quando a fonte o devolveu.</param>
/// <param name="UserPrincipalName">UPN SOMENTE quando aplicável ao tipo e devolvido pela fonte.</param>
/// <param name="Roles">Papéis associados quando a coleta os conhece.</param>
/// <param name="Detail">A constatação que colocou o objeto no conjunto.</param>
public sealed record IdentityObservedObject(
    string ExternalId,
    IdentityEntityKind Kind,
    string? DisplayName = null,
    string? UserPrincipalName = null,
    IReadOnlyList<string>? Roles = null,
    string? Detail = null);

/// <summary>
/// UM conjunto observado por uma aquisição, com seu desfecho e sua completude. A completude é DECLARADA pela
/// coleta, nunca deduzida do tamanho da lista: uma lista vazia com <see cref="IdentityObservationSetOutcome.Collected"/>
/// significa "o conjunto está vazio"; a mesma lista vazia com qualquer outro desfecho significa "não sabemos".
/// </summary>
/// <param name="Set">O conjunto.</param>
/// <param name="Outcome">Desfecho da coleta deste conjunto.</param>
/// <param name="ObservedCount">
/// Quantidade APURADA pela coleta. Pode ser maior que <paramref name="Objects"/> quando a enumeração foi
/// truncada — e a divergência é declarada, não escondida.
/// </param>
/// <param name="Objects">Objetos preservados, já deduplicados pela mesma regra que produziu a contagem.</param>
/// <param name="IsComplete"><c>false</c> quando a coleta não enumerou tudo.</param>
/// <param name="Limitation">O que ficou de fora ou não pôde ser lido (sanitizado).</param>
public sealed record IdentityObservedSet(
    IdentityObservationSet Set,
    IdentityObservationSetOutcome Outcome,
    int ObservedCount,
    IReadOnlyList<IdentityObservedObject> Objects,
    bool IsComplete = true,
    string? Limitation = null)
{
    /// <summary>Conjunto não coletado: os objetos permanecem DESCONHECIDOS — nunca ausentes nem resolvidos.</summary>
    public static IdentityObservedSet NotCollected(
        IdentityObservationSet set, IdentityObservationSetOutcome outcome, string? limitation) =>
        new(set, outcome, 0, Array.Empty<IdentityObservedObject>(), IsComplete: false, limitation);
}

/// <summary>
/// A ORIGEM de uma aquisição, resolvida a partir da configuração EFETIVAMENTE usada na coleta. O namespace do
/// diretório vem da credencial resolvida, não de um rótulo escolhido pela UI: é ele que delimita o espaço de
/// identificadores e impede que trocar a configuração para outro diretório reaproveite vínculos antigos.
/// </summary>
public sealed record IdentityAcquisitionOrigin(
    Guid ConnectorConfigId,
    KnightSourceType Provider,
    string DirectoryNamespace,
    string SourceLabel);

/// <summary>
/// A aquisição NORMALIZADA pronta para persistir: proveniência, versões, horários, estado, fatos agregados e
/// os conjuntos observados. É o contrato que a fronteira de compatibilidade produz a partir da coleta.
/// </summary>
/// <param name="AcquisitionId">
/// Identificador da aquisição. Reprocessar a MESMA aquisição (mesmo id) é idempotente; uma coleta nova sempre
/// traz um id novo, mesmo que observe exatamente o mesmo conteúdo.
/// </param>
/// <param name="AcquiredAt">Instante em que o AEGIS recebeu esta coleta. Sempre conhecido.</param>
/// <param name="ObservedAt">Instante segundo a fonte, quando ela o fornece. Nunca inventado.</param>
public sealed record IdentityAcquisitionRequest(
    Guid AcquisitionId,
    IdentityAcquisitionOrigin Origin,
    string SchemaVersion,
    string NormalizationVersion,
    DateTimeOffset AcquiredAt,
    DateTimeOffset? ObservedAt,
    KnightSourceState State,
    string? Detail,
    string FactsJson,
    string CapabilitiesJson,
    IReadOnlyList<IdentityObservedSet> Sets);

/// <summary>
/// A aquisição COMO FOI PERSISTIDA — o que os consumidores leem. Devolvida pela releitura do registro gravado,
/// e não pelo objeto que entrou: é assim que "a avaliação leu a aquisição persistida" deixa de ser promessa e
/// vira uma propriedade verificável do caminho.
/// </summary>
/// <param name="DetailRetiredAt">
/// [AEGIS-ADM-02] Instante em que o DETALHE (as observações) foi removido pela retenção operacional; <c>null</c>
/// quando o detalhe é o que sempre foi. Quem lê PRECISA deste campo: sem ele, uma lista de objetos vazia seria
/// indistinguível de "a coleta não encontrou ninguém", e detalhe expirado passaria por evidência íntegra.
/// As contagens e a completude originais continuam nos conjuntos, intactas.
/// </param>
public sealed record IdentityAcquisitionRecord(
    Guid AcquisitionId,
    Guid TenantId,
    IdentityAcquisitionOrigin Origin,
    string SchemaVersion,
    string NormalizationVersion,
    DateTimeOffset AcquiredAt,
    DateTimeOffset? ObservedAt,
    KnightSourceState State,
    string? Detail,
    string FactsJson,
    string CapabilitiesJson,
    string ContentFingerprint,
    IReadOnlyList<IdentityObservedSetRecord> Sets,
    DateTimeOffset? DetailRetiredAt = null);

/// <summary>Um conjunto como foi persistido, com os objetos preservados e a entidade canônica de cada um.</summary>
public sealed record IdentityObservedSetRecord(
    IdentityObservationSet Set,
    IdentityObservationSetOutcome Outcome,
    int ObservedCount,
    int PreservedCount,
    bool IsComplete,
    string? Limitation,
    IReadOnlyList<IdentityObservedObjectRecord> Objects);

/// <summary>
/// Um objeto como foi persistido: os atributos OBSERVADOS naquela aquisição (não os atuais) mais o
/// identificador CANÔNICO da entidade, que é o que permite dizer "este privilegiado é o mesmo que apareceu
/// sem método capaz de MFA registrado".
/// </summary>
public sealed record IdentityObservedObjectRecord(
    Guid IdentityEntityId,
    string ExternalId,
    IdentityEntityKind Kind,
    string? DisplayNameObserved,
    string? UserPrincipalNameObserved,
    IReadOnlyList<string> RolesObserved,
    string? Detail);
