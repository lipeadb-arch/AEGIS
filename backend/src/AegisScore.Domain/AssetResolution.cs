using System;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-ENTITY-RESOLUTION-01] Resolução de dispositivos entre fontes por vínculo FORTE
// ============================================================================
// Até aqui cada fonte criava/reutilizava o SEU Asset pelo binding da própria fonte (conector + id na fonte).
// Duas fontes que observavam o MESMO dispositivo produziam dois ativos, e não havia como afirmar que eram o
// mesmo: o identificador de dispositivo no diretório Microsoft (aadDeviceId no Defender, azureADDeviceId no
// Intune) não era preservado.
//
// Este arquivo acrescenta UMA estrutura e o vocabulário de estado — nada de segundo inventário, grafo genérico
// ou pipeline paralelo:
//   • AssetStrongIdentifier — a CHAVE FORTE (tenant AEGIS, namespace do diretório de origem, tipo de
//     identificador, valor normalizado) → ativo canônico. É ela que torna "o mesmo dispositivo" uma invariante
//     de BANCO: dois índices únicos impedem que uma chave aponte para dois ativos e que um ativo carregue dois
//     dispositivos do mesmo diretório.
//   • estados de resolução POR BINDING (em AssetSourceBinding) — distinguem vínculo confirmado, falta de
//     evidência (fonte não informou / valor inválido / diretório não confirmado) e CONTRADIÇÃO, sem chamar
//     toda ausência de "conflito".
//
// Identificadores que NÃO se confundem aqui (verificados na documentação oficial das APIs):
//   • TenantId do AEGIS (isolamento interno) ≠ namespace do diretório Microsoft (tenant do Entra que emitiu a
//     credencial da integração);
//   • id do objeto NA FONTE (machineId do Defender, id do managedDevice do Intune) — vive em
//     AssetSourceBinding.ExternalId e nunca é chave entre fontes;
//   • identificador do dispositivo NO DIRETÓRIO (aadDeviceId / azureADDeviceId) — corresponde ao deviceId do
//     objeto device do Entra, a chave alternativa definida pelo Device Registration Service;
//   • id do OBJETO Entra (device.id, herdado de directoryObject) — campo DIFERENTE, não coletado aqui;
//   • AssetId — o identificador canônico interno.
//
// Nome, hostname, e-mail, IP ou semelhança textual NUNCA unificam. Usuário e dispositivo são entidades
// diferentes: nada aqui liga identidade do ADM a dispositivo.

/// <summary>Tipo do identificador forte. Fechado de propósito: só o que as fontes atuais comprovam.</summary>
public enum AssetStrongIdentifierType
{
    /// <summary>
    /// Identificador de dispositivo do Microsoft Entra ID (deviceId do objeto device; aadDeviceId no Defender,
    /// azureADDeviceId no Intune). NÃO é o id do objeto de diretório.
    /// </summary>
    EntraDeviceId = 1,
}

/// <summary>
/// O que a ÚLTIMA observação de UM binding disse sobre o identificador de diretório. Separado do estado de
/// resolução porque "o vínculo foi confirmado antes" e "a última coleta não trouxe o identificador" são fatos
/// diferentes — um não apaga o outro.
/// </summary>
public enum DirectoryIdentifierStatus
{
    /// <summary>Binding anterior a este pacote, ainda não reavaliado. Nada é presumido.</summary>
    NotEvaluated = 0,

    /// <summary>A fonte informou um identificador válido para o tipo.</summary>
    Provided = 1,

    /// <summary>A fonte não informou o campo (ou o informou vazio/nulo).</summary>
    NotProvided = 2,

    /// <summary>A fonte informou um valor que não é um identificador válido (formato, GUID vazio, placeholder).</summary>
    Invalid = 3,

    /// <summary>
    /// A MESMA coleta trouxe o mesmo registro da fonte mais de uma vez, com identificadores de diretório
    /// DIFERENTES (ex.: X numa página e Y em outra; ou X e ausente/inválido). Nenhum deles é usado para vincular
    /// nem para confirmar vínculo — escolher "o primeiro" dependeria da ordem das páginas.
    /// </summary>
    Contradictory = 4,
}

/// <summary>Estado de resolução entre fontes de UM binding.</summary>
public enum AssetBindingResolutionState
{
    /// <summary>Binding anterior a este pacote, ainda não reavaliado — sem conclusão alguma.</summary>
    NotEvaluated = 0,

    /// <summary>Vinculado ao ativo pela chave forte (tenant + diretório + identificador de dispositivo).</summary>
    Linked = 1,

    /// <summary>A fonte não informou identificador de diretório: ainda não é possível vincular entre fontes.</summary>
    NoIdentifier = 2,

    /// <summary>A fonte informou um identificador inválido: recusado, nenhuma união feita.</summary>
    InvalidIdentifier = 3,

    /// <summary>Identificador válido, mas o diretório de origem não foi confirmado pela integração.</summary>
    DirectoryUnconfirmed = 4,

    /// <summary>Contradição entre identificadores/ativos — preservada e explicada, nunca resolvida por escolha arbitrária.</summary>
    Conflict = 5,
}

/// <summary>Natureza da contradição de um binding em <see cref="AssetBindingResolutionState.Conflict"/>.</summary>
public enum AssetBindingConflictKind
{
    None = 0,

    /// <summary>
    /// O identificador de diretório informado pela fonte MUDOU em relação ao vínculo já estabelecido, no MESMO
    /// diretório. O binding permanece no ativo original (nunca movido silenciosamente) até análise. Conflitos
    /// gravados antes de <see cref="DirectoryChanged"/> existir também usam este valor — sem o diretório da
    /// observação registrado, nada é presumido sobre qual dos dois campos mudou.
    /// </summary>
    IdentifierChanged = 1,

    /// <summary>
    /// O identificador informado já pertence a OUTRO ativo existente (duplicidade entre ativos já existentes,
    /// tipicamente legados). Os dois ativos são preservados; nada é fundido nem apagado.
    /// </summary>
    IdentifierHeldByOtherAsset = 2,

    /// <summary>O ativo deste binding já está vinculado a OUTRO dispositivo do mesmo diretório.</summary>
    AssetHeldByOtherIdentifier = 3,

    /// <summary>
    /// A fonte passou a informar o MESMO identificador de dispositivo em OUTRO diretório de origem. O vínculo
    /// estabelecido (diretório anterior) permanece; o par observado fica registrado à parte para análise.
    /// </summary>
    DirectoryChanged = 4,

    /// <summary>Diretório de origem E identificador de dispositivo mudaram em relação ao vínculo estabelecido.</summary>
    DirectoryAndIdentifierChanged = 5,

    /// <summary>
    /// A mesma coleta trouxe identificadores contraditórios para o mesmo registro da fonte
    /// (<see cref="DirectoryIdentifierStatus.Contradictory"/>). Nenhum foi usado para vincular ou confirmar; um
    /// vínculo estabelecido antes é mantido, e as demais informações do registro seguem utilizáveis.
    /// </summary>
    ContradictoryObservation = 6,
}

/// <summary>
/// De onde veio o NOME atual do ativo. Existe para que um nome PROVISÓRIO (fonte que não coleta nome, como a
/// leitura minimizada do Intune) possa ser substituído pelo primeiro nome observado — e para que nenhum outro
/// nome (curado, manual, legado) jamais seja sobrescrito por uma fonte.
/// </summary>
public enum AssetNameOrigin
{
    /// <summary>Manual, CMDB, legado ou desconhecido — tratado como curado: nunca sobrescrito por fonte.</summary>
    Unspecified = 0,

    /// <summary>Semeado pelo nome que uma fonte observou (ex.: computerDnsName do Defender).</summary>
    ObservedBySource = 1,

    /// <summary>Rótulo provisório gerado porque a fonte não coleta nome; substituível pelo primeiro nome observado.</summary>
    Placeholder = 2,
}

/// <summary>
/// CHAVE FORTE de um dispositivo no diretório de origem → ativo canônico do tenant. Uma linha por dispositivo de
/// diretório, criada na primeira observação válida e NUNCA reescrita para outro ativo nem removida pela ausência
/// numa coleta (ausência não é exclusão).
///
/// Invariantes de banco:
///   • <c>UX_AssetStrongIdentifier_Natural</c> — (TenantId, DirectoryNamespace, IdentifierType, IdentifierValue)
///     único: uma chave aponta para UM ativo; a corrida entre duas fontes vira violação reconhecida e releitura;
///   • <c>UX_AssetStrongIdentifier_AssetScope</c> — (TenantId, AssetId, DirectoryNamespace, IdentifierType)
///     único: um ativo não carrega dois dispositivos do mesmo diretório (isso seria uma fusão);
///   • FK composta (AssetId, TenantId) → Asset (Id, TenantId): o banco recusa chave de um tenant apontando para
///     ativo de outro.
///
/// Guarda o mínimo necessário à resolução: nada de nome, IP, usuário ou payload. O valor do identificador é
/// dado INTERNO de resolução — não vai para log, IA, relatório ou listagem pública.
/// </summary>
public class AssetStrongIdentifier : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Ativo canônico ao qual o dispositivo de diretório pertence.</summary>
    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }

    /// <summary>Namespace do diretório de origem (tenant do Entra, GUID normalizado em minúsculas).</summary>
    public string DirectoryNamespace { get; set; } = "";

    public AssetStrongIdentifierType IdentifierType { get; set; } = AssetStrongIdentifierType.EntraDeviceId;

    /// <summary>Valor normalizado (GUID em minúsculas, formato D).</summary>
    public string IdentifierValue { get; set; } = "";

    /// <summary>Instante em que a chave foi estabelecida.</summary>
    public DateTimeOffset EstablishedAt { get; set; }

    /// <summary>Conector que estabeleceu a chave — proveniência (sem FK: excluir o conector não apaga a chave).</summary>
    public Guid EstablishedByConnectorConfigId { get; set; }

    /// <summary>Rótulo da fonte que estabeleceu a chave (ex.: "Microsoft Intune").</summary>
    public string EstablishedBySource { get; set; } = "";
}
