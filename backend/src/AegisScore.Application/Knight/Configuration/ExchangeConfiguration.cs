using System;
using System.Collections.Generic;
using System.Linq;

namespace AegisScore.Application.Knight.Configuration;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-03] Configuração do Exchange Online observada numa coleta
// ============================================================================
// Mesmo desenho dos dois blocos anteriores: cada contrato é a NORMALIZAÇÃO tipada do que a fonte devolveu, sem
// interpretação. Os nomes seguem as propriedades documentadas dos comandos oficiais de leitura, valores ausentes
// permanecem nulos — nulo NUNCA vira "desabilitado" nem "conforme" — e cada documento carrega o nome e a versão
// do contrato. A leitura só interpreta as versões que conhece.
//
// Três diferenças estruturais em relação ao bloco do Teams, e cada uma existe por um motivo:
//
//   1. HERANÇA. No Exchange, uma configuração da ORGANIZAÇÃO pode ser substituída por caixa de correio, e o
//      valor que significa "herda a organização" é a AUSÊNCIA do valor na caixa. Um contrato que colapsasse
//      ausência em "desabilitado" inverteria o veredito de SMTP AUTH. Por isso os valores por caixa são
//      anuláveis e a herança é dita explicitamente (ver ExchangeSmtpAuthOverrideInventory).
//
//   2. ENUMERAÇÃO. Caixas de correio, contas e associações são POPULAÇÕES, não configurações únicas. Guardar um
//      documento por caixa inflaria o ADM sem acrescentar evidência. O contrato é um INVENTÁRIO: o total lido, a
//      COMPLETUDE da enumeração e a lista limitada dos registros que o critério precisa nomear. A completude é o
//      que impede uma leitura truncada de sustentar a aprovação da organização inteira.
//
//   3. CORRELAÇÃO POR IDENTIFICADOR ESTÁVEL. "Caixa compartilhada" e "conta bloqueada" são leituras DIFERENTES,
//      e o critério depende das duas. O vínculo é feito pelo identificador do objeto de diretório que o próprio
//      Exchange devolve (ExternalDirectoryObjectId) — nunca por nome, endereço ou semelhança. Quando a conta
//      correspondente não aparece na leitura de contas, o estado fica NULO e a caixa não é aprovada.

// ---- Configurações ÚNICAS da organização --------------------------------------------------------------

/// <summary>
/// Configuração da organização do Exchange Online (instância única). Reúne as propriedades que os controles
/// leem de <c>Get-OrganizationConfig</c>.
/// </summary>
/// <param name="AuditDisabled">
/// Auditoria de caixa de correio DESABILITADA na organização. O valor esperado é <c>false</c>: a propriedade
/// nomeia a desabilitação, então <c>false</c> significa "auditoria por padrão ligada".
/// </param>
/// <param name="RejectDirectSend">
/// Rejeição de envios diretos (mensagens entregues ao ponto de extremidade da organização sem autenticação).
/// Propriedade recente: quando a fonte não a devolve, permanece nula e o controle não é avaliado.
/// </param>
public sealed record ExchangeOrganizationConfiguration(
    bool? AuditDisabled,
    bool? CustomerLockBoxEnabled,
    bool? OAuth2ClientProfileEnabled,
    bool? MailTipsAllTipsEnabled,
    bool? MailTipsExternalRecipientsTipsEnabled,
    bool? MailTipsGroupMetricsEnabled,
    int? MailTipsLargeAudienceThreshold,
    bool? BookingsEnabled,
    bool? RejectDirectSend)
{
    public const string SchemaVersion = "aegis-config-exo-organization-v1";
    public const string ExternalId = "exchangeOrganizationConfiguration";

    /// <summary>Limite de destinatários a partir do qual a dica de "público amplo" aparece, na referência.</summary>
    public const int LargeAudienceThreshold = 25;
}

/// <summary>
/// Configuração de TRANSPORTE da organização (instância única). Só o que o critério de SMTP AUTH precisa: o
/// valor da organização, que vale para toda caixa que não o substitua.
/// </summary>
public sealed record ExchangeTransportConfiguration(bool? SmtpClientAuthenticationDisabled)
{
    public const string SchemaVersion = "aegis-config-exo-transport-v1";
    public const string ExternalId = "exchangeTransportConfiguration";
}

/// <summary>
/// Identificação de remetentes EXTERNOS no Outlook (instância única do locatário). A lista de isenções é
/// preservada porque o critério da referência não aceita isenção alguma: habilitar com isenções não é o
/// mesmo que habilitar.
/// </summary>
public sealed record ExchangeExternalSenderIdentification(
    string? Identity,
    bool? Enabled,
    IReadOnlyList<string> AllowList)
{
    public const string SchemaVersion = "aegis-config-exo-external-sender-v1";
    public const string ExternalId = "exchangeExternalSenderIdentification";
}

// ---- Políticas (várias instâncias por locatário) -------------------------------------------------------

/// <summary>
/// O que toda política do Exchange Online tem em comum para efeito de ALCANCE: a identidade na fonte, o nome e
/// se é a política PADRÃO da organização. <see cref="Identity"/> é anulável de propósito — identificação
/// ausente é dado insuficiente, jamais convertida na política padrão (a mesma regra firmada no bloco do Teams).
/// </summary>
public interface IExchangePolicyDocument
{
    string? Identity { get; }
    string? Name { get; }

    /// <summary>A fonte declarou que esta é a política padrão? <c>null</c> = não informado.</summary>
    bool? IsDefault { get; }
}

/// <summary>
/// Uma política de COMPARTILHAMENTO. Cada entrada de <paramref name="Domains"/> é preservada como a fonte a
/// devolveu ("<c>Anonymous:CalendarSharingFreeBusySimple</c>", "<c>contoso.com:CalendarSharingFreeBusyDetail</c>"):
/// o critério depende do que foi compartilhado E com quem, e reescrever a entrada perderia um dos dois.
/// </summary>
public sealed record ExchangeSharingPolicyConfiguration(
    string? Identity,
    string? Name,
    bool? IsDefault,
    bool? Enabled,
    IReadOnlyList<string> Domains) : IExchangePolicyDocument
{
    public const string SchemaVersion = "aegis-config-exo-sharing-policy-v1";

    /// <summary>Marca que identifica uma entrada de compartilhamento de CALENDÁRIO na lista de domínios.</summary>
    public const string CalendarSharingToken = "CalendarSharing";

    /// <summary>Domínio de destino que representa "qualquer pessoa, sem identificação".</summary>
    public const string AnonymousDomain = "Anonymous";

    /// <summary>Entradas que compartilham CALENDÁRIO, com o domínio de destino separado do que é compartilhado.</summary>
    public IReadOnlyList<(string Entry, string Domain, string Shared)> CalendarSharingEntries =>
        Domains
            .Where(d => d.Contains(CalendarSharingToken, StringComparison.OrdinalIgnoreCase))
            .Select(d =>
            {
                var i = d.IndexOf(':');
                return i > 0
                    ? (Entry: d, Domain: d[..i].Trim(), Shared: d[(i + 1)..].Trim())
                    : (Entry: d, Domain: d.Trim(), Shared: "");
            })
            .ToList();
}

/// <summary>
/// Uma política de caixa de correio do OUTLOOK NA WEB. Três critérios diferentes leem esta política — provedores
/// de armazenamento adicionais, contas pessoais e criação de caixa do Bookings —, e cada um deles vale para
/// TODAS as instâncias, não só para a padrão.
/// </summary>
public sealed record ExchangeOwaMailboxPolicyConfiguration(
    string? Identity,
    string? Name,
    bool? IsDefault,
    bool? AdditionalStorageProvidersAvailable,
    bool? PersonalAccountsEnabled,
    bool? PersonalAccountCalendarsEnabled,
    bool? BookingsMailboxCreationEnabled) : IExchangePolicyDocument
{
    public const string SchemaVersion = "aegis-config-exo-owa-mailbox-policy-v1";
}

/// <summary>
/// Uma REGRA DE TRANSPORTE (regra de fluxo de emails). O contrato preserva o que decide se a regra PRODUZ
/// EFEITO — e é por isso que ele é maior do que a condição do critério:
///   • <paramref name="State"/> — uma regra DESABILITADA não age sobre mensagem alguma;
///   • <paramref name="Mode"/> — em modo de AUDITORIA a regra registra, mas não aplica a ação.
/// Sem esses dois campos, uma regra inerte seria apresentada como exposição ativa do ambiente.
/// </summary>
/// <param name="SetScl">
/// Nível de confiança de spam atribuído pela regra. O valor <c>-1</c> é o que ignora a filtragem de spam.
/// </param>
public sealed record ExchangeTransportRuleConfiguration(
    string? Identity,
    string? Name,
    string? State,
    string? Mode,
    int? Priority,
    int? SetScl,
    IReadOnlyList<string> SenderDomainIs,
    IReadOnlyList<string> FromAddressContainsWords,
    IReadOnlyList<string> FromAddressMatchesPatterns,
    IReadOnlyList<string> RedirectMessageTo,
    IReadOnlyList<string> BlindCopyTo,
    IReadOnlyList<string> AddToRecipients,
    IReadOnlyList<string> CopyTo)
{
    public const string SchemaVersion = "aegis-config-exo-transport-rule-v1";

    /// <summary>Valor de nível de confiança de spam que dispensa a filtragem.</summary>
    public const int BypassSpamFiltering = -1;

    /// <summary>Estado em que a regra age sobre as mensagens.</summary>
    public const string StateEnabled = "Enabled";

    /// <summary>Modo em que a regra APLICA a ação (os demais apenas registram ou testam).</summary>
    public const string ModeEnforce = "Enforce";

    /// <summary>A regra age sobre mensagens? Habilitada E em modo de aplicação. Valor ausente = indeterminado.</summary>
    public bool? ProducesEffect =>
        State is null || Mode is null
            ? null
            : State.Trim().Equals(StateEnabled, StringComparison.OrdinalIgnoreCase)
              && Mode.Trim().Equals(ModeEnforce, StringComparison.OrdinalIgnoreCase);

    /// <summary>Remetentes que a regra isenta, por qualquer dos três caminhos de identificação.</summary>
    public IReadOnlyList<string> SenderScope =>
        SenderDomainIs.Concat(FromAddressContainsWords).Concat(FromAddressMatchesPatterns).ToList();

    /// <summary>Destinos para onde a regra desvia ou copia a mensagem (o encaminhamento por regra de transporte).</summary>
    public IReadOnlyList<string> ForwardingTargets =>
        RedirectMessageTo.Concat(BlindCopyTo).Concat(AddToRecipients).Concat(CopyTo).ToList();
}

/// <summary>
/// Uma política de ATRIBUIÇÃO DE FUNÇÃO de usuário final: o que a pessoa pode fazer na própria caixa de correio.
/// As funções atribuídas são preservadas como vieram; o critério olha para três delas, e as demais continuam
/// visíveis para quem lê a evidência.
/// </summary>
public sealed record ExchangeRoleAssignmentPolicyConfiguration(
    string? Identity,
    string? Name,
    bool? IsDefault,
    IReadOnlyList<string> AssignedRoles) : IExchangePolicyDocument
{
    public const string SchemaVersion = "aegis-config-exo-role-assignment-policy-v1";

    /// <summary>
    /// Funções que permitem à pessoa instalar suplementos do Outlook por conta própria. Nomes oficiais das
    /// funções de gerenciamento do Exchange Online — não são texto de interface.
    /// </summary>
    public static IReadOnlyList<string> AddInRoles { get; } = new[]
    {
        "My Custom Apps",
        "My Marketplace Apps",
        "My ReadWriteMailbox Apps",
    };

    /// <summary>Funções de instalação de suplemento presentes nesta política.</summary>
    public IReadOnlyList<string> AddInRolesAssigned =>
        AssignedRoles.Where(r => AddInRoles.Contains(r?.Trim() ?? "", StringComparer.OrdinalIgnoreCase)).ToList();
}

/// <summary>
/// Uma política de filtro de spam de SAÍDA. Só o modo de encaminhamento automático interessa a este bloco — é
/// um dos mecanismos que o critério de encaminhamento exige ler, e ele não vive no Exchange "puro".
/// </summary>
public sealed record ExchangeOutboundSpamPolicyConfiguration(
    string? Identity,
    string? Name,
    bool? IsDefault,
    string? AutoForwardingMode) : IExchangePolicyDocument
{
    public const string SchemaVersion = "aegis-config-exo-outbound-spam-policy-v1";

    /// <summary>Encaminhamento automático DESLIGADO para as caixas alcançadas pela política.</summary>
    public const string ForwardingOff = "Off";

    /// <summary>Encaminhamento automático LIBERADO.</summary>
    public const string ForwardingOn = "On";

    /// <summary>O serviço decide (o comportamento padrão do serviço, que hoje bloqueia — mas não é o mesmo que desligar).</summary>
    public const string ForwardingAutomatic = "Automatic";
}

// ---- Inventários (populações enumeradas) ---------------------------------------------------------------

/// <summary>
/// Uma caixa de correio COMPARTILHADA e o estado de entrada da conta correspondente.
/// </summary>
/// <param name="ExternalDirectoryObjectId">
/// Identificador do objeto de diretório que o próprio Exchange devolve. É por ELE que a caixa é casada com a
/// conta — nunca pelo nome de exibição ou pelo endereço.
/// </param>
/// <param name="SignInBlocked">
/// A conta correspondente está impedida de entrar? <c>null</c> significa que a conta NÃO foi encontrada na
/// leitura de contas desta mesma aquisição — e uma caixa nessa situação não pode ser aprovada.
/// </param>
public sealed record ExchangeSharedMailboxRecord(
    string ExternalDirectoryObjectId,
    string? UserPrincipalName,
    string? DisplayName,
    bool? SignInBlocked);

/// <summary>
/// Inventário das caixas de correio COMPARTILHADAS da organização.
///
/// <para><b>Por que o estado de entrada tem um campo próprio de resolução:</b> "caixa compartilhada" e "conta
/// bloqueada" são duas leituras. Quando a leitura de contas não foi concluída, a coleta tem as caixas mas não o
/// estado delas — e afirmar que uma caixa compartilhada implica conta bloqueada seria inventar o critério
/// inteiro. <paramref name="SignInStateResolved"/> separa os dois casos.</para>
/// </summary>
/// <param name="ListComplete">
/// A enumeração terminou? <c>false</c> quando o teto de leitura foi atingido. Enumeração truncada nunca sustenta
/// a aprovação de toda a organização — mas as violações encontradas continuam sendo violações.
/// </param>
public sealed record ExchangeSharedMailboxInventory(
    int SharedMailboxTotal,
    IReadOnlyList<ExchangeSharedMailboxRecord> Mailboxes,
    bool ListComplete,
    bool SignInStateResolved,
    string? SignInStateLimitation)
{
    public const string SchemaVersion = "aegis-config-exo-shared-mailbox-inventory-v1";
    public const string ExternalId = "exchangeSharedMailboxInventory";

    /// <summary>Teto de caixas compartilhadas preservadas nominalmente (o total lido é sempre registrado).</summary>
    public const int MaxListed = 500;
}

/// <summary>
/// Ações de auditoria configuradas numa caixa de correio, separadas por TIPO DE ACESSO. Os três tipos são
/// independentes: auditar o dono não audita o delegado, e auditar o delegado não audita o administrador.
/// </summary>
public sealed record ExchangeMailboxAuditRecord(
    string ExternalDirectoryObjectId,
    string? UserPrincipalName,
    string? DisplayName,
    bool? AuditEnabled,
    IReadOnlyList<string> MissingOwnerActions,
    IReadOnlyList<string> MissingDelegateActions,
    IReadOnlyList<string> MissingAdminActions)
{
    /// <summary>Falta alguma ação em qualquer um dos três tipos de acesso.</summary>
    public bool HasGap =>
        MissingOwnerActions.Count > 0 || MissingDelegateActions.Count > 0 || MissingAdminActions.Count > 0;
}

/// <summary>
/// Inventário das AÇÕES de auditoria por caixa de correio. Só as caixas com alguma lacuna são preservadas
/// nominalmente; o total lido e a completude dizem sobre quantas o veredito fala.
/// </summary>
public sealed record ExchangeMailboxAuditInventory(
    int MailboxTotal,
    IReadOnlyList<ExchangeMailboxAuditRecord> MailboxesWithGaps,
    bool ListComplete)
{
    public const string SchemaVersion = "aegis-config-exo-mailbox-audit-inventory-v1";
    public const string ExternalId = "exchangeMailboxAuditInventory";

    public const int MaxListed = 500;

    /// <summary>
    /// Ações que a referência exige para o acesso do PRÓPRIO DONO da caixa. Nomes oficiais das ações de
    /// auditoria de caixa de correio do Exchange Online.
    /// </summary>
    public static IReadOnlyList<string> RequiredOwnerActions { get; } = new[]
    {
        "ApplyRecord", "Create", "HardDelete", "MailItemsAccessed", "Move", "MoveToDeletedItems",
        "Send", "SoftDelete", "Update", "UpdateCalendarDelegation", "UpdateFolderPermissions",
        "UpdateInboxRules",
    };

    /// <summary>Ações que a referência exige para o acesso DELEGADO (quem tem permissão sobre a caixa de outra pessoa).</summary>
    public static IReadOnlyList<string> RequiredDelegateActions { get; } = new[]
    {
        "ApplyRecord", "Create", "HardDelete", "MailItemsAccessed", "Move", "MoveToDeletedItems",
        "SendAs", "SendOnBehalf", "SoftDelete", "Update", "UpdateFolderPermissions", "UpdateInboxRules",
    };

    /// <summary>Ações que a referência exige para o acesso ADMINISTRATIVO.</summary>
    public static IReadOnlyList<string> RequiredAdminActions { get; } = new[]
    {
        "ApplyRecord", "Copy", "Create", "HardDelete", "MailItemsAccessed", "Move", "MoveToDeletedItems",
        "SendAs", "SendOnBehalf", "SoftDelete", "Update", "UpdateCalendarDelegation",
        "UpdateFolderPermissions", "UpdateInboxRules",
    };
}

/// <summary>
/// Encaminhamento configurado NUMA caixa de correio. Os dois caminhos são distintos: um aponta para um
/// destinatário interno do diretório, o outro para um endereço SMTP arbitrário.
/// </summary>
/// <param name="DeliverToMailboxAndForward">
/// A cópia também fica na caixa. Não muda o fato de a mensagem sair — muda apenas a chance de alguém perceber.
/// </param>
public sealed record ExchangeMailboxForwardingRecord(
    string ExternalDirectoryObjectId,
    string? UserPrincipalName,
    string? DisplayName,
    string? ForwardingSmtpAddress,
    string? ForwardingAddress,
    bool? DeliverToMailboxAndForward);

/// <summary>Inventário do encaminhamento configurado nas caixas de correio (só as que têm algum configurado).</summary>
public sealed record ExchangeMailboxForwardingInventory(
    int MailboxTotal,
    IReadOnlyList<ExchangeMailboxForwardingRecord> MailboxesWithForwarding,
    bool ListComplete)
{
    public const string SchemaVersion = "aegis-config-exo-mailbox-forwarding-inventory-v1";
    public const string ExternalId = "exchangeMailboxForwardingInventory";

    public const int MaxListed = 500;
}

/// <summary>
/// SUBSTITUIÇÃO de SMTP AUTH numa caixa de correio.
/// </summary>
/// <param name="SmtpClientAuthenticationDisabled">
/// O valor observado NA CAIXA. Três estados distintos, e confundi-los inverte o veredito:
/// <c>true</c> = a caixa desabilita explicitamente; <c>false</c> = a caixa HABILITA explicitamente, ignorando a
/// organização; <c>null</c> = a caixa HERDA a organização. Só o segundo caso é exposição por substituição.
/// </param>
public sealed record ExchangeSmtpAuthOverrideRecord(
    string ExternalDirectoryObjectId,
    string? UserPrincipalName,
    string? DisplayName,
    bool? SmtpClientAuthenticationDisabled);

/// <summary>
/// Inventário das substituições de SMTP AUTH por caixa de correio. Preserva as que HABILITAM explicitamente e
/// conta as que herdam — a contagem é o que permite ao achado dizer o alcance do valor da organização.
/// </summary>
public sealed record ExchangeSmtpAuthOverrideInventory(
    int MailboxTotal,
    int InheritingOrganizationCount,
    int ExplicitlyDisabledCount,
    IReadOnlyList<ExchangeSmtpAuthOverrideRecord> ExplicitlyEnabled,
    bool ListComplete)
{
    public const string SchemaVersion = "aegis-config-exo-smtp-auth-override-inventory-v1";
    public const string ExternalId = "exchangeSmtpAuthOverrideInventory";

    public const int MaxListed = 500;
}

/// <summary>Uma associação de DESVIO de auditoria: o objeto cujas ações não são registradas.</summary>
public sealed record ExchangeAuditBypassRecord(
    string Identity,
    string? DisplayName,
    bool? AuditBypassEnabled);

/// <summary>
/// Inventário das associações de desvio de auditoria. O total lido importa: "nenhum registro encontrado" só
/// significa "nenhum desvio" quando a leitura foi concluída.
/// </summary>
public sealed record ExchangeAuditBypassInventory(
    int AssociationsTotal,
    IReadOnlyList<ExchangeAuditBypassRecord> BypassEnabled,
    bool ListComplete)
{
    public const string SchemaVersion = "aegis-config-exo-audit-bypass-inventory-v1";
    public const string ExternalId = "exchangeAuditBypassInventory";

    public const int MaxListed = 500;
}

// ---- Convenções de identidade de política do Exchange --------------------------------------------------

/// <summary>
/// Convenções de identidade das políticas do Exchange Online — um lugar só, usado pela coleta e pela avaliação.
///
/// A regra central é a MESMA do bloco do Teams, e vale repeti-la porque o desenho da fonte é diferente:
/// identificação AUSENTE é <b>dado insuficiente</b>, e não a política padrão. O que muda é como a política
/// padrão é reconhecida: no Teams ela tem uma identidade literal; no Exchange a fonte declara um sinalizador
/// próprio (<c>IsDefault</c>). Um sinalizador não informado NÃO faz a política ser a padrão nem deixa de fazer —
/// ele deixa a pergunta sem resposta, e é assim que a avaliação o trata.
/// </summary>
public static class ExchangePolicyIdentities
{
    /// <summary>Rótulo de uma instância que a fonte devolveu SEM identificar.</summary>
    public const string UnidentifiedLabel = "política sem identificação na coleta";

    /// <summary>A fonte identificou esta política? Nulo, vazio ou só espaços = não.</summary>
    public static bool IsIdentified(string? identity) => !string.IsNullOrWhiteSpace(identity);

    /// <summary>Nome legível: o nome declarado, ou a identidade, ou o rótulo de não identificada.</summary>
    public static string Label(IExchangePolicyDocument policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var name = !string.IsNullOrWhiteSpace(policy.Name) ? policy.Name!.Trim()
            : IsIdentified(policy.Identity) ? policy.Identity!.Trim()
            : null;
        if (name is null) return UnidentifiedLabel;
        return policy.IsDefault == true ? "política padrão “" + name + "”" : "política “" + name + "”";
    }

    /// <summary>Chave estável de uma política no identificador de objeto: o nome, a identidade, ou nada.</summary>
    public static string? Key(IExchangePolicyDocument policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!string.IsNullOrWhiteSpace(policy.Identity)) return policy.Identity!.Trim();
        return string.IsNullOrWhiteSpace(policy.Name) ? null : policy.Name!.Trim();
    }
}

// ---- Alcance das políticas: quantas caixas de correio cada uma governa ---------------------------------

/// <summary>
/// Quantas caixas de correio usam UMA política. É o que transforma "existe uma política adequada" em "o critério
/// vale para estas caixas": no Exchange, cada caixa declara qual política de cada tipo a governa, e essa
/// declaração é lida na MESMA enumeração que já é feita — sem nenhuma consulta nova.
/// </summary>
public sealed record ExchangePolicyReachEntry(string PolicyType, string PolicyName, int MailboxCount);

/// <summary>
/// Alcance das políticas declaradas PELAS CAIXAS DE CORREIO (atribuição de função e compartilhamento).
/// <paramref name="MailboxesWithoutDeclaration"/> conta as caixas que não declararam a política do tipo — elas
/// existem (por exemplo, quando a fonte omite a propriedade) e não podem ser distribuídas por suposição.
/// </summary>
public sealed record ExchangePolicyReachInventory(
    int MailboxTotal,
    IReadOnlyList<ExchangePolicyReachEntry> Entries,
    int MailboxesWithoutDeclaration,
    bool ListComplete)
{
    public const string SchemaVersion = "aegis-config-exo-policy-reach-inventory-v1";
    public const string ExternalId = "exchangePolicyReachInventory";

    /// <summary>Tipo de política de atribuição de função de usuário final.</summary>
    public const string RoleAssignmentPolicyType = "RoleAssignmentPolicy";

    /// <summary>Tipo de política de compartilhamento.</summary>
    public const string SharingPolicyType = "SharingPolicy";

    public int CountFor(string policyType, string? policyName) =>
        policyName is null ? 0
        : Entries
            .Where(e => e.PolicyType.Equals(policyType, StringComparison.OrdinalIgnoreCase)
                     && e.PolicyName.Equals(policyName.Trim(), StringComparison.OrdinalIgnoreCase))
            .Sum(e => e.MailboxCount);
}

/// <summary>
/// Alcance das políticas do OUTLOOK NA WEB, declaradas pelas configurações de acesso de cliente de cada caixa.
/// Fica separado de <see cref="ExchangePolicyReachInventory"/> porque vem de OUTRA leitura — e a falha de uma
/// leitura não pode ser preenchida com o resultado da outra.
/// </summary>
public sealed record ExchangeOwaPolicyReachInventory(
    int MailboxTotal,
    IReadOnlyList<ExchangePolicyReachEntry> Entries,
    int MailboxesWithoutDeclaration,
    bool ListComplete)
{
    public const string SchemaVersion = "aegis-config-exo-owa-policy-reach-inventory-v1";
    public const string ExternalId = "exchangeOwaPolicyReachInventory";

    /// <summary>Tipo de política de caixa de correio do Outlook na web.</summary>
    public const string OwaMailboxPolicyType = "OwaMailboxPolicy";

    public int CountFor(string? policyName) =>
        policyName is null ? 0
        : Entries
            .Where(e => e.PolicyName.Equals(policyName.Trim(), StringComparison.OrdinalIgnoreCase))
            .Sum(e => e.MailboxCount);
}
