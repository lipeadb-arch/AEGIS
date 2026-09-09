using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-ADM-01] AEGIS Data Model — recorte de IDENTIDADE
// ============================================================================
// O que muda em relação ao que já existia: a Evidence Fabric persistia um snapshot AGREGADO por conector
// (contagens, razões, flags) e o AEGIS KNIGHT avaliava o resultado TRANSITÓRIO da coleta. Ninguém guardava os
// OBJETOS efetivamente observados nem o registro da aquisição que os produziu, de modo que não havia como
// responder "qual coleta sustentou esta avaliação?" nem "este objeto privilegiado é o mesmo que apareceu sem
// método capaz de MFA registrado?".
//
// Este arquivo introduz cinco conceitos e nada além deles:
//   1. AQUISIÇÃO   (IdentityAcquisition)          — UMA coleta lógica, identificável e rastreável.
//   2. ENTIDADE    (IdentityEntity)               — o objeto de identidade CANÔNICO do tenant, com um
//                                                    identificador interno estável e os atributos ATUAIS.
//   3. VÍNCULO     (IdentitySourceLink)           — a amarra da entidade com a ORIGEM real (conector +
//                                                    namespace do diretório + identificador externo).
//   4. OBSERVAÇÃO  (IdentityEntityObservation)    — o objeto COMO FOI OBSERVADO numa aquisição, dentro de um
//                                                    conjunto específico.
//   5. COMPLETUDE  (IdentityObservationSetState)  — o estado e a completude POR CONJUNTO daquela aquisição.
//
// Três separações que este modelo existe para não deixar colapsar:
//   • EVIDÊNCIA × PROJEÇÃO. A aquisição e as observações são evidência (imutáveis depois de gravadas); a
//     entidade é PROJEÇÃO do estado atual, derivada delas. Renomear uma identidade muda a projeção e NUNCA a
//     evidência já registrada.
//   • POPULAÇÃO × CENSO. Os conjuntos aqui são populações específicas (membros privilegiados, privilegiados
//     sem método capaz de MFA registrado, convidados sinalizados pela regra de atividade). NÃO são o censo do
//     diretório, e ausência num conjunto NÃO é exclusão, revogação de privilégio nem conta desativada.
//   • UNIFICAÇÃO POR IDENTIFICADOR × POR SEMELHANÇA. Duas observações só apontam para a mesma entidade quando
//     compartilham (tenant, namespace do diretório, identificador externo). Nome, UPN ou e-mail iguais NUNCA
//     unificam — é o que impede fundir homônimos e convidados B2B de diretórios diferentes.
//
// Este pacote NÃO altera AEGIS Score, KNIGHT Score, cobertura, veredito ou regra de conformidade.

/// <summary>
/// Natureza de um objeto de identidade no vocabulário do ADM. Deliberadamente explícita: um membro de papel
/// privilegiado pode ser uma aplicação ou um grupo, e tratá-lo como pessoa levaria a recomendar ações
/// impossíveis. Quando a fonte não classifica o objeto, ele fica <see cref="Unknown"/> — nunca "usuário".
///
/// Espelha os membros de <see cref="KnightAffectedObjectKind"/> por uma razão de fronteira, não por acaso: a
/// superfície histórica de afetados do KNIGHT continua com o tipo DELA, e a conversão entre os dois é
/// determinística e total (ver a fronteira de compatibilidade na camada de aplicação). O ADM não herda o
/// vocabulário do avaliador — ele traduz.
/// </summary>
public enum IdentityEntityKind
{
    /// <summary>Conta de usuário interna do diretório.</summary>
    User = 0,

    /// <summary>Conta de convidado/externa (B2B). NUNCA é fundida com identidade de outro diretório por e-mail.</summary>
    Guest = 1,

    /// <summary>Identidade de aplicação/serviço (service principal, managed identity…). Não é uma pessoa.</summary>
    ServicePrincipal = 2,

    /// <summary>Grupo cujo pertencimento concede acesso.</summary>
    Group = 3,

    /// <summary>Dispositivo registrado no diretório.</summary>
    Device = 4,

    /// <summary>A fonte devolveu o objeto sem tipo reconhecível — declarado desconhecido.</summary>
    Unknown = 5,
}

/// <summary>
/// CONJUNTO observado de uma aquisição — a população específica à qual a observação pertence. O escopo é
/// fechado de propósito: são exatamente os conjuntos que a coleta atual do Entra já produz, sem uma única
/// chamada nova ao diretório.
///
/// Cada conjunto evidencia UMA dimensão e só ela (ver <c>IdentityObservationSetCatalog</c> na camada de
/// aplicação). Em particular, "sem MFA" aqui significa REGISTRO/CAPACIDADE ausente no relatório do diretório —
/// não exigência por política, não aplicação efetiva na autenticação e não resistência a phishing.
/// </summary>
public enum IdentityObservationSet
{
    /// <summary>Objeto com atribuição de papel privilegiado no diretório (usuário, convidado, aplicação ou grupo).</summary>
    PrivilegedRoleMember = 0,

    /// <summary>
    /// Subconjunto de <see cref="PrivilegedRoleMember"/> SEM método capaz de MFA REGISTRADO no relatório de
    /// registro do diretório. Não afirma que a autenticação ocorre sem segundo fator.
    /// </summary>
    PrivilegedWithoutRegisteredMfaCapability = 1,

    /// <summary>Conta de convidado sinalizada pela regra de atividade já existente da coleta.</summary>
    InactiveGuest = 2,
}

/// <summary>
/// Desfecho da coleta de UM conjunto numa aquisição. Existe separado do estado da aquisição inteira porque a
/// falha de um conjunto não pode contaminar os outros: um 403 no relatório de registro de MFA não torna
/// desconhecido o inventário de papéis privilegiados que já foi lido.
///
/// Regra que este enum sustenta: só <see cref="Collected"/> autoriza ler o conjunto como completo. Qualquer
/// outro desfecho significa que os objetos NÃO recebidos permanecem DESCONHECIDOS — nunca ausentes, nunca
/// resolvidos, nunca conformes.
/// </summary>
public enum IdentityObservationSetOutcome
{
    /// <summary>Não tentado nesta aquisição (a capacidade nem chegou a ser exercida).</summary>
    NotAttempted = 0,

    /// <summary>Coletado integralmente — a lista É o conjunto, e a contagem é a verdade.</summary>
    Collected = 1,

    /// <summary>Coletado de forma declaradamente incompleta (paginação truncada, campo ausente). A contagem é um PISO.</summary>
    Partial = 2,

    /// <summary>Permissão/consentimento ausente para ler o conjunto. Não comprovado.</summary>
    InsufficientPermission = 3,

    /// <summary>Fonte indisponível, com erro de transporte ou throttling persistente. Não comprovado.</summary>
    Unavailable = 4,

    /// <summary>Erro inesperado ao normalizar/ler o conjunto. Não comprovado.</summary>
    Error = 5,
}

/// <summary>
/// UMA aquisição lógica de identidade: a coleta identificável que produziu fatos e objetos. É o registro que
/// permite responder "qual coleta sustentou esta avaliação?" e reproduzir os fatos que ela consumiu.
///
/// Distingue quatro coisas que uma tabela ingênua confundiria:
///   • RETRY da mesma aquisição — a ESCRITA é idempotente: reprocessar a mesma aquisição (mesmo
///     <see cref="Entity.Id"/>) não duplica observações nem vínculos;
///   • nova aquisição que observou o MESMO conteúdo — duas linhas distintas com o mesmo
///     <see cref="ContentFingerprint"/>. Elas NÃO são deduplicadas: observar de novo é um fato novo;
///   • atualização dos atributos ATUAIS da entidade — acontece em <see cref="IdentityEntity"/>, não aqui;
///   • evidência histórica de uma avaliação — a aquisição referida por aquela execução permanece intocada.
///
/// PROVENIÊNCIA COMPLETA: tenant AEGIS, conector, provedor e NAMESPACE do diretório de origem, versões de
/// schema e normalização, horário da aquisição e — apenas quando a fonte o forneceu — o horário OBSERVADO.
/// Um horário de fornecedor jamais é inventado a partir do horário da coleta.
/// </summary>
public class IdentityAcquisition : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Conector que executou a aquisição — a autoridade da origem e o alvo da FK tenant-safe.</summary>
    public Guid ConnectorConfigId { get; set; }

    /// <summary>Provedor concreto da coleta. Demo NUNCA entra aqui (ver o guard no store).</summary>
    public KnightSourceType Provider { get; set; } = KnightSourceType.MicrosoftEntraId;

    /// <summary>
    /// NAMESPACE real do diretório de origem, derivado da configuração EFETIVAMENTE usada nesta coleta (para o
    /// Entra: o identificador do diretório Microsoft). É o que impede que trocar a configuração para outro
    /// diretório reaproveite os vínculos do anterior — dois namespaces nunca compartilham entidade.
    /// </summary>
    public string DirectoryNamespace { get; set; } = "";

    /// <summary>Rótulo legível da fonte (ex.: "Microsoft Entra ID"). Nunca endpoint, credencial ou segredo.</summary>
    public string SourceLabel { get; set; } = "";

    /// <summary>Versão do contrato canônico do ADM aplicado a esta aquisição.</summary>
    public string SchemaVersion { get; set; } = "";

    /// <summary>Versão da NORMALIZAÇÃO (a tradução fonte → observações tipadas) aplicada a esta aquisição.</summary>
    public string NormalizationVersion { get; set; } = "";

    /// <summary>Instante em que o AEGIS adquiriu/recebeu esta coleta. SEMPRE conhecido.</summary>
    public DateTimeOffset AcquiredAt { get; set; }

    /// <summary>
    /// Instante OBSERVADO segundo a própria fonte, quando ela o fornece. <c>null</c> é a resposta honesta
    /// quando só se conhece o horário da coleta — jamais preenchido com <see cref="AcquiredAt"/>.
    /// </summary>
    public DateTimeOffset? ObservedAt { get; set; }

    /// <summary>Estado/escopo da coleta inteira (Completed, PartialCollection, InsufficientPermission…).</summary>
    public KnightSourceState State { get; set; } = KnightSourceState.NotConfigured;

    /// <summary>Motivo sanitizado da ausência/limitação desta aquisição. Sem token, segredo ou payload.</summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Fatos AGREGADOS normalizados desta aquisição (envelope versionado, o mesmo formato do snapshot). É o que
    /// permite REPRODUZIR os fatos consumidos por uma avaliação sem consultar a fonte de novo.
    /// </summary>
    public string FactsJson { get; set; } = "{}";

    /// <summary>Estado por capacidade da fonte nesta aquisição (JSON sanitizado) — alimenta a cobertura.</summary>
    public string CapabilitiesJson { get; set; } = "[]";

    /// <summary>
    /// Fingerprint determinístico (SHA-256 hex) do CONTEÚDO observado. Serve para reconhecer que duas
    /// aquisições distintas viram a mesma coisa — e NÃO para deduplicá-las.
    /// </summary>
    public string ContentFingerprint { get; set; } = "";

    /// <summary>Estados/completude por conjunto observado nesta aquisição.</summary>
    public ICollection<IdentityObservationSetState> Sets { get; set; } = new List<IdentityObservationSetState>();

    /// <summary>Objetos observados nesta aquisição.</summary>
    public ICollection<IdentityEntityObservation> Observations { get; set; } = new List<IdentityEntityObservation>();

    /// <summary>True quando esta aquisição produziu dados legíveis (completos ou declaradamente parciais).</summary>
    public bool ProducedData => State is KnightSourceState.Completed or KnightSourceState.PartialCollection;
}

/// <summary>
/// ENTIDADE de identidade CANÔNICA do tenant — o identificador interno estável ao qual as observações de
/// qualquer conjunto apontam. É PROJEÇÃO do estado atual: os atributos aqui refletem a aquisição mais recente
/// que os observou, e mudá-los NÃO altera nenhuma evidência, avaliação, ação, validação ou relatório antigo.
///
/// Invariantes de identidade:
///   • mudança de nome/UPN NÃO cria outra entidade (a identidade vem do vínculo de origem, não do nome);
///   • nome igual em objetos diferentes NÃO os unifica;
///   • identificador igual em namespaces diferentes NÃO os unifica;
///   • um objeto presente em vários conjuntos aponta para ESTA mesma entidade, com as observações separadas.
/// </summary>
public class IdentityEntity : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Natureza do objeto — explícita, jamais presumida como pessoa.</summary>
    public IdentityEntityKind Kind { get; set; } = IdentityEntityKind.Unknown;

    /// <summary>Nome de exibição ATUAL, somente quando alguma fonte o devolveu. <c>null</c> não vira texto inventado.</summary>
    public string? DisplayName { get; set; }

    /// <summary>UPN/e-mail principal ATUAL, quando aplicável ao tipo e devolvido pela fonte.</summary>
    public string? UserPrincipalName { get; set; }

    /// <summary>Primeira aquisição que observou esta entidade.</summary>
    public DateTimeOffset FirstObservedAt { get; set; }

    /// <summary>Aquisição mais recente que observou esta entidade (em qualquer conjunto).</summary>
    public DateTimeOffset LastObservedAt { get; set; }

    /// <summary>
    /// Instante da aquisição que estabeleceu os atributos ATUAIS acima. É o critério de ordenação que impede
    /// uma coleta ATRASADA de sobrescrever silenciosamente um estado mais recente: só uma aquisição posterior
    /// a este instante pode reescrever nome/UPN/tipo.
    /// </summary>
    public DateTimeOffset CurrentAsOf { get; set; }

    /// <summary>Aquisição que estabeleceu os atributos atuais — desempate determinístico e rastreabilidade.</summary>
    public Guid CurrentAcquisitionId { get; set; }

    /// <summary>Vínculos com as origens que observaram esta entidade.</summary>
    public ICollection<IdentitySourceLink> SourceLinks { get; set; } = new List<IdentitySourceLink>();
}

/// <summary>
/// VÍNCULO da entidade canônica com a ORIGEM que a observou — espelha o idioma já usado por
/// <see cref="AssetSourceBinding"/> e <see cref="SoftwareProductSourceBinding"/>.
///
/// A chave natural é <c>(TenantId, DirectoryNamespace, ExternalId)</c>, e é ela que carrega as regras de
/// unificação do pacote: o mesmo objeto visto em dois conjuntos (ou em duas aquisições) resolve para a mesma
/// entidade; o mesmo identificador em OUTRO namespace resolve para uma entidade DIFERENTE. O conector fica
/// registrado como proveniência — mas não faz parte da chave, porque dois conectores apontando para o MESMO
/// diretório observam, de fato, os mesmos objetos.
/// </summary>
public class IdentitySourceLink : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Entidade canônica à qual esta origem se refere.</summary>
    public Guid IdentityEntityId { get; set; }
    public IdentityEntity? IdentityEntity { get; set; }

    /// <summary>Conector que observou o objeto por último — proveniência, NÃO componente da chave natural.</summary>
    public Guid ConnectorConfigId { get; set; }

    /// <summary>Provedor da origem.</summary>
    public KnightSourceType Provider { get; set; } = KnightSourceType.MicrosoftEntraId;

    /// <summary>Namespace real do diretório de origem — componente da chave natural.</summary>
    public string DirectoryNamespace { get; set; } = "";

    /// <summary>Identificador do objeto NA FONTE (object id do diretório) — componente da chave natural.</summary>
    public string ExternalId { get; set; } = "";

    public DateTimeOffset FirstLinkedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }

    /// <summary>Aquisição mais recente que observou o objeto por este vínculo.</summary>
    public Guid LastAcquisitionId { get; set; }
}

/// <summary>
/// O objeto COMO FOI OBSERVADO por UMA aquisição, dentro de UM conjunto. É EVIDÊNCIA: os atributos aqui são os
/// daquele momento e não são reescritos quando o cadastro atual muda. É isso que permite reprojetar o que uma
/// avaliação viu sem apresentar o presente como prova do passado.
///
/// Chave natural <c>(TenantId, AcquisitionId, IdentityEntityId, Set)</c>: a mesma aquisição não registra o
/// mesmo objeto duas vezes no mesmo conjunto (idempotência de escrita), e o MESMO objeto em DOIS conjuntos
/// produz DUAS observações apontando para UMA entidade.
/// </summary>
public class IdentityEntityObservation : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Aquisição que observou — o vínculo que torna a evidência rastreável.</summary>
    public Guid AcquisitionId { get; set; }
    public IdentityAcquisition? Acquisition { get; set; }

    /// <summary>Entidade canônica observada.</summary>
    public Guid IdentityEntityId { get; set; }
    public IdentityEntity? IdentityEntity { get; set; }

    /// <summary>Conjunto/população ao qual esta observação pertence.</summary>
    public IdentityObservationSet Set { get; set; }

    /// <summary>Identificador do objeto na fonte, denormalizado para leitura sem join.</summary>
    public string ExternalId { get; set; } = "";

    /// <summary>Tipo do objeto COMO observado nesta aquisição.</summary>
    public IdentityEntityKind Kind { get; set; } = IdentityEntityKind.Unknown;

    /// <summary>Nome de exibição COMO observado nesta aquisição (não o atual). Ausente permanece ausente.</summary>
    public string? DisplayNameObserved { get; set; }

    /// <summary>UPN COMO observado nesta aquisição (não o atual).</summary>
    public string? UserPrincipalNameObserved { get; set; }

    /// <summary>Papéis associados COMO observados nesta aquisição, quando a coleta os conhece (jsonb).</summary>
    public List<string> RolesObserved { get; set; } = new();

    /// <summary>A constatação da coleta que colocou o objeto neste conjunto — nunca conclusão sobre a pessoa.</summary>
    public string? Detail { get; set; }

    /// <summary>Instante da aquisição, denormalizado para ordenação/leitura barata.</summary>
    public DateTimeOffset ObservedAt { get; set; }
}

/// <summary>
/// Estado e COMPLETUDE de UM conjunto numa aquisição. Sem esta linha, uma lista vazia seria ambígua: "o
/// conjunto está vazio" e "o conjunto não pôde ser lido" pareceriam iguais — e a segunda viraria falsa
/// resolução.
///
/// Chave natural <c>(TenantId, AcquisitionId, Set)</c>.
/// </summary>
public class IdentityObservationSetState : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    public Guid AcquisitionId { get; set; }
    public IdentityAcquisition? Acquisition { get; set; }

    public IdentityObservationSet Set { get; set; }

    /// <summary>Desfecho da coleta DESTE conjunto — independente dos demais.</summary>
    public IdentityObservationSetOutcome Outcome { get; set; } = IdentityObservationSetOutcome.NotAttempted;

    /// <summary>
    /// Quantidade que a coleta APUROU para este conjunto. Pode divergir do número de observações preservadas
    /// (ex.: enumeração truncada) — e essa divergência é declarada em <see cref="Limitation"/>, não escondida.
    /// </summary>
    public int ObservedCount { get; set; }

    /// <summary>Observações efetivamente preservadas neste conjunto.</summary>
    public int PreservedCount { get; set; }

    /// <summary>True somente quando a lista preservada É o conjunto inteiro.</summary>
    public bool IsComplete { get; set; }

    /// <summary>O que exatamente ficou de fora ou não pôde ser lido (sanitizado, sem segredo).</summary>
    public string? Limitation { get; set; }
}
