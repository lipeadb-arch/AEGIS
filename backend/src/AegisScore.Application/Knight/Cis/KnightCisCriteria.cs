using System;
using System.Collections.Generic;
using System.Linq;

namespace AegisScore.Application.Knight.Cis;

// ============================================================================
//  [AEGIS-KNIGHT-CIS-01] Confirmação do critério da EDIÇÃO CIS — separada do resultado técnico do KNIGHT
// ============================================================================
// O resultado de uma regra do KNIGHT é técnico: diz o que a coleta mostrou contra o critério da PRÓPRIA regra. Ele só vira
// Conforme, Não conforme ou Não aplicável numa recomendação CIS quando o critério daquela edição e seção foi conferido numa
// fonte oficial do CIS e o vínculo da regra foi revisado contra ele. Nada abaixo é inferido:
//   • a classificação "integral" do catálogo de referência, o perfil registrado no Monkey365 e a versão do catálogo da
//     execução NÃO confirmam o critério oficial — são rastreio e mapeamento, ditos como tais;
//   • cada confirmação registra a edição, a seção, a fonte oficial consultada (documento e localização), quem conferiu,
//     quando, as versões do catálogo KNIGHT revisadas e as regras cujo vínculo foi conferido contra o texto da edição.
// Sem confirmação, a recomendação fica "Não avaliado" com o motivo abaixo, e a leitura do KNIGHT (regra, resultado e
// evidência) continua visível ao lado.

/// <summary>Uma conferência do critério oficial de UMA recomendação (edição × seção) numa fonte oficial do CIS.</summary>
/// <remarks>
/// <c>SustainedResults</c> limita os desfechos do KNIGHT que a conferência sustenta como resultado CIS (nulo = todos). Quando a
/// regra do KNIGHT é MAIS restritiva que o critério da edição (lê mais políticas, aceita menos valores, ou lê a configuração que a
/// edição trata como estado de aprovação opcional), a aprovação dela prova a conformidade, mas a reprovação não prova a não
/// conformidade: o desfecho não sustentado fica "Não avaliado" com <c>UnsustainedReason</c>.
/// <para><c>ResultMap</c> traduz um desfecho do KNIGHT no resultado que a PRÓPRIA edição declara para aquela situação (ex.: a
/// edição diz que a recomendação está conforme com o compartilhamento externo desligado, e o KNIGHT diz "não aplicável"); o motivo
/// vai em <c>ResultMapReason</c>. <c>CollectionGap</c> diz, quando o KNIGHT não conclui, qual configuração exigida pela edição a
/// coleta atual não alcança. <c>AbsentFromEdition</c> registra uma chave do catálogo que NÃO existe na edição conferida.</para>
/// </remarks>
public sealed record KnightCisCriterionConfirmation(
    string RecommendationId, string OfficialSource, string Location, string ConsultedOn, string ReviewedBy,
    IReadOnlyList<string> ReviewedCatalogVersions, IReadOnlyList<string> ReviewedRules, string? Note,
    IReadOnlyList<string>? SustainedResults = null, string? UnsustainedReason = null,
    IReadOnlyDictionary<string, string>? ResultMap = null, string? ResultMapReason = null,
    string? CollectionGap = null, string? AbsentFromEdition = null);

public static class KnightCisCriteria
{
    public const string NotConfirmedReason = "Critério da edição CIS ainda não confirmado";

    /// <summary>Edição oficial consultada para os lotes das seções 8 (Microsoft Teams), 6 (Exchange Online) e 7 (SharePoint e OneDrive).</summary>
    public const string M365V700Pdf =
        "CIS Microsoft 365 Foundations Benchmark v7.0.0 — PDF oficial do CIS (CIS_Microsoft_365_Foundations_Benchmark_v7.0.0.pdf, edição de 20/05/2026, data impressa na capa)";

    private const string ConsultedOn = "2026-11-14";
    private const string Reviewer = "Claude (assistente de IA), a pedido de Felipe — conferência do texto oficial, sem segunda revisão humana";
    private static readonly string[] Reviewed = { "ak-knight-v13" };
    private static readonly string[] OnlyCompliant = { KnightCisStatus.Compliant };

    /// <summary>Fontes oficiais do CIS efetivamente consultadas para as confirmações abaixo.</summary>
    public static IReadOnlyList<string> OfficialSourcesConsulted { get; } = new[] { M365V700Pdf };

    private const string GlobalMeetingPolicy =
        "a auditoria da edição confere a política de reunião padrão da organização (Global); o KNIGHT reprova quando QUALQUER política de "
        + "reunião, inclusive personalizada, viola o critério, então a reprovação pode vir só de uma política personalizada com a Global conforme.";

    private const string ExternalAccessPolicy =
        "a auditoria principal da edição é a política de acesso externo Global (Get-CsExternalAccessPolicy), que a coleta do KNIGHT não lê; "
        + "a configuração da organização (Get-CsTenantFederationConfiguration), que o KNIGHT lê, é o estado de aprovação opcional da edição. "
        + "Restrita ali, a edição aprova; aberta ali, a política Global ainda pode atender ao critério.";

    private static KnightCisCriterionConfirmation Teams(string section, string pages, string rule, string note,
        IReadOnlyList<string>? sustained = null, string? unsustained = null) => M365(section, pages, rule, note, sustained, unsustained);

    private static KnightCisCriterionConfirmation M365(string section, string pages, string rule, string note,
        IReadOnlyList<string>? sustained = null, string? unsustained = null,
        IReadOnlyDictionary<string, string>? map = null, string? mapReason = null, string? CollectionGap = null) =>
        new("CIS-M365-7.0.0:" + section, M365V700Pdf, $"seção {section}, p. {pages} (Descrição, Auditoria e Valor padrão)", ConsultedOn, Reviewer,
            Reviewed, new[] { rule }, note, sustained, unsustained, map, mapReason, CollectionGap);

    /// <summary>
    /// Primeiro lote: CIS Microsoft 365 Foundations 7.0.0, seção 8 (Microsoft Teams), conferida no PDF
    /// oficial contra a regra KNIGHT e a evidência que ela coleta. Ficam FORA do lote, com o motivo: 8.4.1 (sem regra vinculada —
    /// a leitura exige acesso que o conector não tem) e 8.6.1 (composição com AK-MDO-019, cuja parte no Defender ainda não foi
    /// conferida contra o texto da edição).
    /// </summary>
    private static readonly KnightCisCriterionConfirmation[] TeamsLot =
    {
        Teams("8.1.1", "500–503", "AK-TEAMS-001",
            "Edição: na configuração do cliente do Teams (Global), somente os provedores autorizados pela organização ficam True. "
            + "O KNIGHT aprova quando nenhum provedor de terceiros está habilitado, o que atende à edição.",
            OnlyCompliant, "a edição admite provedores AUTORIZADOS pela organização; o KNIGHT reprova qualquer provedor habilitado e não sabe quais foram autorizados."),
        Teams("8.1.2", "504–507", "AK-TEAMS-002",
            "Edição: AllowEmailIntoChannel = False na configuração do cliente do Teams (Global). Mesmo valor e mesma configuração que o KNIGHT lê; "
            + "a aprovação e a reprovação são sustentadas."),
        Teams("8.2.1", "508–511", "AK-TEAMS-003",
            "Edição: EnableFederationAccess = False na política de acesso externo Global, ou, como estado de aprovação opcional na configuração da "
            + "organização, acesso externo desligado, todos os domínios bloqueados ou somente domínios autorizados. O KNIGHT aprova nos estados opcionais.",
            OnlyCompliant, ExternalAccessPolicy),
        Teams("8.2.2", "512–515", "AK-TEAMS-004",
            "Edição: EnableTeamsConsumerAccess = False na política de acesso externo Global, ou AllowTeamsConsumer = False na configuração da "
            + "organização (estado de aprovação opcional). O KNIGHT aprova com AllowTeamsConsumer = False.",
            OnlyCompliant, ExternalAccessPolicy),
        Teams("8.2.3", "516–520", "AK-TEAMS-005",
            "Edição: EnableTeamsConsumerInbound = False na política de acesso externo Global, ou AllowTeamsConsumerInbound = False na configuração "
            + "da organização (estado de aprovação opcional). O KNIGHT aprova com AllowTeamsConsumerInbound = False.",
            OnlyCompliant, ExternalAccessPolicy + " Com a comunicação com contas não gerenciadas desligada, o KNIGHT diz Não aplicável; a edição não "
            + "trata esse caso como não aplicável."),
        Teams("8.2.4", "521–525", "AK-TEAMS-006",
            "Edição: ExternalAccessWithTrialTenants = Blocked na configuração da organização. Mesmo valor e mesma configuração que o KNIGHT lê; "
            + "a aprovação e a reprovação são sustentadas."),
        Teams("8.5.1", "529–531", "AK-TEAMS-008",
            "Edição: AllowAnonymousUsersToJoinMeeting = False na política de reunião Global. O KNIGHT exige o mesmo valor em todas as políticas.",
            OnlyCompliant, GlobalMeetingPolicy),
        Teams("8.5.2", "532–534", "AK-TEAMS-009",
            "Edição: AllowAnonymousUsersToStartMeeting = False na política de reunião Global. O KNIGHT exige o mesmo valor em todas as políticas.",
            OnlyCompliant, GlobalMeetingPolicy),
        Teams("8.5.3", "535–538", "AK-TEAMS-010",
            "Edição: AutoAdmittedUsers = InvitedUsers, EveryoneInCompanyExcludingGuests ou OrganizerOnly na política de reunião Global. O KNIGHT "
            + "aceita só os dois últimos, em todas as políticas.",
            OnlyCompliant, GlobalMeetingPolicy + " Além disso, a edição aceita “Pessoas convidadas” (InvitedUsers), que o KNIGHT reprova de propósito."),
        Teams("8.5.4", "539–540", "AK-TEAMS-011",
            "Edição: AllowPSTNUsersToBypassLobby = False na política de reunião Global. O KNIGHT exige o mesmo valor em todas as políticas.",
            OnlyCompliant, GlobalMeetingPolicy),
        Teams("8.5.5", "541–542", "AK-TEAMS-012",
            "Edição: MeetingChatEnabledType = EnabledExceptAnonymous, EnabledInMeetingOnlyForAllExceptAnonymous ou Disabled na política de reunião "
            + "Global. O KNIGHT aceita EnabledExceptAnonymous e Disabled; EnabledInMeetingOnlyForAllExceptAnonymous fica sem conclusão no KNIGHT.",
            OnlyCompliant, GlobalMeetingPolicy),
        Teams("8.5.6", "543–544", "AK-TEAMS-013",
            "Edição: DesignatedPresenterRoleMode = OrganizerOnlyUserOverride na política de reunião Global. O KNIGHT exige o mesmo valor em todas as políticas.",
            OnlyCompliant, GlobalMeetingPolicy),
        Teams("8.5.7", "545–547", "AK-TEAMS-014",
            "Edição: AllowExternalParticipantGiveRequestControl = False na política de reunião Global. O KNIGHT exige o mesmo valor em todas as políticas.",
            OnlyCompliant, GlobalMeetingPolicy),
        Teams("8.5.8", "548–549", "AK-TEAMS-015",
            "Edição: AllowExternalNonTrustedMeetingChat = False na política de reunião Global. O KNIGHT exige o mesmo valor em todas as políticas.",
            OnlyCompliant, GlobalMeetingPolicy),
        Teams("8.5.9", "550–553", "AK-TEAMS-016",
            "Edição: AllowCloudRecording = False na política de reunião Global. O KNIGHT exige o mesmo valor em todas as políticas.",
            OnlyCompliant, GlobalMeetingPolicy),
    };

    private static readonly string[] Nothing = Array.Empty<string>();

    private const string DefaultOwaPolicy =
        "a edição audita só a política PADRÃO do Outlook na web (IsDefault); o KNIGHT reprova quando QUALQUER política viola o critério, "
        + "então a reprovação pode vir só de uma política personalizada com a padrão conforme.";

    /// <summary>
    /// Segundo lote: CIS Microsoft 365 Foundations 7.0.0, seção 6 (Exchange admin center), conferido no mesmo PDF oficial contra
    /// as regras AK-EXO e a configuração que elas coletam. Todas as 13 recomendações da seção estão aqui. Em 6.1.2, 6.2.1 e 6.2.2 o
    /// critério foi conferido, mas a regra não sustenta nenhum desfecho da edição (lista vazia): ficam inconclusivas, com o motivo.
    /// </summary>
    private static readonly KnightCisCriterionConfirmation[] ExchangeLot =
    {
        M365("6.1.1", "417–418", "AK-EXO-005",
            "Edição: AuditDisabled = False em Get-OrganizationConfig. Mesmo valor e mesma configuração que o KNIGHT lê; "
            + "a aprovação e a reprovação são sustentadas."),
        M365("6.1.2", "419–425", "AK-EXO-006",
            "Edição: em cada caixa UserMailbox, AuditEnabled = True e as ações exigidas nos três tipos de acesso — inclusive FolderBind e "
            + "Send (administrativo), FolderBind (delegado) e MailboxLogin (dono). As listas da regra no catálogo ak-knight-v13 não têm essas "
            + "ações, e a regra verifica também caixas compartilhadas, que a edição não audita.",
            Nothing,
            "as listas de ações da regra (catálogo ak-knight-v13) não incluem FolderBind e Send no acesso administrativo, FolderBind no "
            + "delegado e MailboxLogin no do dono, que a edição 7.0.0 exige — a aprovação do KNIGHT não prova a conformidade; e a regra "
            + "verifica também caixas compartilhadas, fora da auditoria da edição — a reprovação pode vir só delas."),
        M365("6.1.3", "426–429", "AK-EXO-007",
            "Edição: nenhuma associação com AuditBypassEnabled = True em Get-MailboxAuditBypassAssociation, salvo exceção escrita. "
            + "O KNIGHT lê as mesmas associações e só aprova com a leitura completa.",
            OnlyCompliant,
            "a edição admite o desvio de auditoria em contas com EXCEÇÃO ESCRITA, que a coleta não conhece; uma conta dispensada pode estar "
            + "coberta por exceção."),
        M365("6.2.1", "430–433", "AK-EXO-008",
            "Edição: auditoria em dois passos — regras de transporte sem redirecionamento (RedirectMessageTo) para domínios externos e todas as "
            + "políticas de saída (Get-HostedOutboundSpamFilterPolicy) com AutoForwardingMode = Off. O KNIGHT exige também a leitura do "
            + "encaminhamento nas caixas de correio.",
            Nothing,
            "a reprovação do KNIGHT pode vir só do encaminhamento configurado em caixas de correio, ou de cópia para destino interno, que "
            + "a auditoria da edição não verifica; e a aprovação do KNIGHT desconsidera regras de transporte desabilitadas, que a auditoria "
            + "da edição lista sem filtrar pelo estado."),
        M365("6.2.2", "434–436", "AK-EXO-009",
            "Edição: nenhuma regra de transporte com SetSCL = -1 e SenderDomainIs preenchido (Get-TransportRule, sem filtro de estado). "
            + "O KNIGHT considera também endereços e padrões de remetente, e só as regras em vigor.",
            Nothing,
            "a aprovação do KNIGHT desconsidera regras desabilitadas ou em modo de teste, que a auditoria da edição reprova; e a reprovação "
            + "do KNIGHT pode vir de regra que isenta por endereço ou padrão de remetente, sem domínio, fora do critério da edição."),
        M365("6.2.3", "437–439", "AK-EXO-010",
            "Edição: Get-ExternalInOutlook com Enabled = True e AllowList só com endereços permitidos pela organização. O KNIGHT aprova com "
            + "a identificação habilitada e a lista vazia.",
            OnlyCompliant,
            "a edição admite na AllowList os endereços que a organização permitiu; o KNIGHT reprova qualquer isenção e não sabe quais foram autorizadas."),
        M365("6.3.1", "440–443", "AK-EXO-011",
            "Edição: a política de atribuição de função PADRÃO sem My Custom Apps, My Marketplace Apps e My ReadWriteMailbox Apps. Mesmas "
            + "três funções na regra, que as verifica em todas as políticas e só aprova com a padrão conforme.",
            OnlyCompliant,
            "a edição audita só a política de atribuição de função PADRÃO; o KNIGHT reprova quando qualquer política atribui as funções, "
            + "então a reprovação pode vir só de uma política personalizada."),
        M365("6.3.2", "444–448", "AK-EXO-012",
            "Edição: PersonalAccountsEnabled = False e PersonalAccountCalendarsEnabled = False na política PADRÃO do Outlook na web. O "
            + "KNIGHT exige os mesmos valores em todas as políticas e só aprova com a padrão conforme.",
            OnlyCompliant, DefaultOwaPolicy),
        M365("6.5.1", "449–451", "AK-EXO-013",
            "Edição: OAuth2ClientProfileEnabled = True em Get-OrganizationConfig. Mesmo valor e mesma configuração que o KNIGHT lê; "
            + "a aprovação e a reprovação são sustentadas."),
        M365("6.5.2", "452–453", "AK-EXO-014",
            "Edição: MailTipsAllTipsEnabled, MailTipsExternalRecipientsTipsEnabled e MailTipsGroupMetricsEnabled = True e "
            + "MailTipsLargeAudienceThreshold num valor aceitável (25 é o padrão). O KNIGHT exige os três e o limite no máximo 25.",
            OnlyCompliant,
            "a edição deixa o limite de público amplo a critério da organização (“valor aceitável”); o KNIGHT reprova acima de 25, e a "
            + "reprovação pode vir só do limite."),
        M365("6.5.3", "454–455", "AK-EXO-015",
            "Edição: AdditionalStorageProvidersAvailable = False na política OwaMailboxPolicy-Default. O KNIGHT exige o mesmo valor em todas "
            + "as políticas e só aprova com a padrão conforme.",
            OnlyCompliant, DefaultOwaPolicy),
        M365("6.5.4", "456–457", "AK-EXO-016",
            "Edição: SmtpClientAuthenticationDisabled = True em Get-TransportConfig. O KNIGHT exige o mesmo valor na organização e nenhuma "
            + "caixa de correio habilitando o SMTP AUTH por substituição.",
            OnlyCompliant,
            "a edição audita só o valor da organização; o KNIGHT reprova também quando uma caixa habilita o SMTP AUTH por substituição, "
            + "então a reprovação pode vir só das caixas, com a organização conforme."),
        M365("6.5.5", "458–460", "AK-EXO-017",
            "Edição: RejectDirectSend = True em Get-OrganizationConfig. Mesmo valor e mesma configuração que o KNIGHT lê; a aprovação e a "
            + "reprovação são sustentadas."),
    };


    private static readonly string[] CompliantOrNot = { KnightCisStatus.Compliant, KnightCisStatus.NonCompliant };

    /// <summary>A configuração que a edição audita em <c>Get-SPOTenant</c> e que só a API de administração do SharePoint expõe.</summary>
    private static string AdminOnly(string property, string scope) =>
        $"A edição audita {property} em Get-SPOTenant ({scope}). Essa configuração só é exposta pela API de administração do SharePoint, "
        + "que exige Sites.FullControl.All e que a coleta atual, somente leitura, não chama; o Microsoft Graph (/admin/sharepoint/settings) "
        + "não a expõe.";

    /// <summary>O "não aplicável" do KNIGHT com o compartilhamento externo desligado, quando a edição não trata esse caso à parte.</summary>
    private static string SharingOffNotApplicable(string property) =>
        $"com o compartilhamento externo desligado, o KNIGHT diz Não aplicável sem ler o valor; a edição audita {property} mesmo assim e "
        + "não trata esse caso como não aplicável.";

    private static readonly IReadOnlyDictionary<string, string> NotApplicableIsCompliant =
        new Dictionary<string, string> { [KnightCisStatus.NotApplicable] = KnightCisStatus.Compliant };

    private static readonly string[] CompliantNotOrSharingOff =
        { KnightCisStatus.Compliant, KnightCisStatus.NonCompliant, KnightCisStatus.NotApplicable };

    /// <summary>
    /// Terceiro lote: CIS Microsoft 365 Foundations 7.0.0, seção 7 (SharePoint admin center), conferido no mesmo PDF oficial contra as
    /// regras AK-SPO e a configuração que elas coletam. São as 13 chaves do catálogo: as 12 seções da edição e a 7.3.2, que a edição
    /// 7.0.0 removeu (registrada como ausente). Pelo Microsoft Graph a coleta lê 7.2.1, 7.2.3, 7.2.5 e 7.2.6; as demais propriedades só
    /// existem na API de administração do SharePoint, que a coleta não chama — sem leitura, o motivo diz qual configuração falta.
    /// </summary>
    private static readonly KnightCisCriterionConfirmation[] SharePointLot =
    {
        M365("7.2.1", "463–465", "AK-SPO-001",
            "Edição: LegacyAuthProtocolsEnabled = False em Get-SPOTenant — configuração do locatário, vale para SharePoint e OneDrive. O KNIGHT lê "
            + "a mesma configuração pelo Microsoft Graph (isLegacyAuthProtocolsEnabled); a aprovação e a reprovação são sustentadas."),
        M365("7.2.2", "466–467", "AK-SPO-002",
            "Edição: EnableAzureADB2BIntegration = True em Get-SPOTenant (SharePoint e OneDrive). A regra compara o mesmo valor quando a API de "
            + "administração é lida.",
            CompliantOrNot, SharingOffNotApplicable("EnableAzureADB2BIntegration"),
            CollectionGap: AdminOnly("EnableAzureADB2BIntegration", "SharePoint e OneDrive")),
        M365("7.2.3", "468–470", "AK-SPO-003",
            "Edição: SharingCapability = ExternalUserSharingOnly, ExistingExternalUserSharingOnly ou Disabled em Get-SPOTenant — o nível do "
            + "SharePoint. O KNIGHT lê o mesmo nível pelo Microsoft Graph (sharingCapability) e reprova só “qualquer pessoa”; a aprovação e a "
            + "reprovação são sustentadas."),
        M365("7.2.4", "471–473", "AK-SPO-004",
            "Edição: OneDriveSharingCapability = Disabled — o nível do OneDrive, que a edição registra como nunca mais permissivo que o do "
            + "SharePoint. O KNIGHT aprova pelo Microsoft Graph quando o SharePoint está restrito à organização e, nos demais casos, compara o "
            + "mesmo valor pela API de administração.",
            CollectionGap: AdminOnly("OneDriveSharingCapability", "OneDrive")
                + " Pelo Graph, só se conclui quando o SharePoint está restrito à organização."),
        M365("7.2.5", "474–475", "AK-SPO-005",
            "Edição: PreventExternalUsersFromResharing = True em Get-SPOTenant (SharePoint). O KNIGHT lê a mesma configuração pelo Microsoft "
            + "Graph (isResharingByExternalUsersEnabled, invertido).",
            CompliantOrNot, SharingOffNotApplicable("PreventExternalUsersFromResharing")),
        M365("7.2.6", "476–479", "AK-SPO-006",
            "Edição: SharingCapability = Disabled, OU SharingDomainRestrictionMode = AllowList com domínios confiáveis (SharePoint). O KNIGHT lê "
            + "as mesmas configurações pelo Microsoft Graph; com o compartilhamento desligado diz Não aplicável, que a edição declara conforme.",
            CompliantNotOrSharingOff, null, NotApplicableIsCompliant,
            "A edição declara esta recomendação conforme quando o compartilhamento externo do SharePoint está em “Somente pessoas da "
            + "organização”, qualquer que seja a restrição por domínio (p. 477); a leitura do KNIGHT encontrou esse estado e o classificou "
            + "como não aplicável."),
        M365("7.2.7", "480–482", "AK-SPO-007",
            "Edição: DefaultSharingLinkType = Direct (pessoas específicas) ou Internal (somente pessoas da organização) em Get-SPOTenant — o "
            + "link padrão do SharePoint e do OneDrive. O KNIGHT aceita só Direct.",
            OnlyCompliant, "a edição aceita também “Somente pessoas da organização” (Internal), que o KNIGHT reprova.",
            CollectionGap: AdminOnly("DefaultSharingLinkType", "SharePoint e OneDrive")),
        M365("7.2.8", "483–486", "AK-SPO-008",
            "Edição: SharingCapability = Disabled, OU WhoCanShareAuthenticatedGuestAllowList com ao menos um grupo de segurança — configuração "
            + "global, vale para SharePoint e OneDrive. A regra lê outra propriedade (GuestSharingGroupAllowList); só o estado de compartilhamento "
            + "desligado, lido pelo Microsoft Graph, coincide com a edição.",
            new[] { KnightCisStatus.NotApplicable },
            "a regra lê GuestSharingGroupAllowList, e a edição audita WhoCanShareAuthenticatedGuestAllowList — outra propriedade do locatário.",
            NotApplicableIsCompliant,
            "A edição declara esta recomendação conforme quando o compartilhamento externo do SharePoint está em “Somente pessoas da "
            + "organização”, qualquer que seja a lista de grupos (p. 484); a leitura do KNIGHT encontrou esse estado e o classificou como não "
            + "aplicável.",
            AdminOnly("WhoCanShareAuthenticatedGuestAllowList", "SharePoint e OneDrive")
                + " A regra, além disso, compara GuestSharingGroupAllowList, que não é a propriedade da edição."),
        M365("7.2.9", "487–489", "AK-SPO-009",
            "Edição: ExternalUserExpirationRequired = True e ExternalUserExpireInDays = 30 (o mínimo aceito é 30) — convidados de sites do "
            + "SharePoint e do OneDrive. A regra aceita “até 30 dias”, equivalente a 30 pelo mínimo do serviço.",
            CompliantOrNot, SharingOffNotApplicable("ExternalUserExpirationRequired e ExternalUserExpireInDays"),
            CollectionGap: AdminOnly("ExternalUserExpirationRequired e ExternalUserExpireInDays", "sites do SharePoint e OneDrive")),
        M365("7.2.10", "490–492", "AK-SPO-010",
            "Edição: EmailAttestationRequired = True e EmailAttestationReAuthDays até 15 em Get-SPOTenant (SharePoint e OneDrive). Mesmo critério "
            + "na regra quando a API de administração é lida.",
            CompliantOrNot, SharingOffNotApplicable("EmailAttestationRequired e EmailAttestationReAuthDays"),
            CollectionGap: AdminOnly("EmailAttestationRequired e EmailAttestationReAuthDays", "SharePoint e OneDrive")),
        M365("7.2.11", "493–495", "AK-SPO-011",
            "Edição: DefaultLinkPermission = View em Get-SPOTenant (links de compartilhamento do SharePoint). Mesmo valor na regra quando a API de "
            + "administração é lida; a aprovação e a reprovação são sustentadas.",
            CollectionGap: AdminOnly("DefaultLinkPermission", "SharePoint")),
        M365("7.3.1", "496–497", "AK-SPO-012",
            "Edição: DisallowInfectedFileDownload = True em Get-SPOTenant (SharePoint). Mesmo valor na regra quando a API de administração é lida; "
            + "a aprovação e a reprovação são sustentadas.",
            CollectionGap: AdminOnly("DisallowInfectedFileDownload", "SharePoint")),
        new("CIS-M365-7.0.0:7.3.2", M365V700Pdf, "Appendix: Change History, versão 7.0.0, p. 613–615 (REMOVE - 7.3.2, Ticket #26841)",
            ConsultedOn, Reviewer, Reviewed, new[] { "AK-SPO-013" },
            "A chave CIS-M365-7.0.0:7.3.2 vem do ruleset do Monkey365 (commit 7cfa390); a edição oficial não tem essa seção. A regra AK-SPO-013 "
            + "continua como leitura técnica do KNIGHT.",
            AbsentFromEdition: "a recomendação “Ensure OneDrive sync is restricted for unmanaged devices” foi removida nesta edição (histórico de "
            + "mudanças da versão 7.0.0, Ticket #26841)."),
    };

    /// <summary>
    /// Confirmações registradas do CIS Microsoft 365 Foundations 7.0.0: lote 1 (seção 8, Teams), lote 2 (seção 6, Exchange Online) e
    /// lote 3 (seção 7, SharePoint e OneDrive).
    /// </summary>
    public static IReadOnlyList<KnightCisCriterionConfirmation> Registered { get; } = TeamsLot.Concat(ExchangeLot).Concat(SharePointLot).ToArray();

    public static IReadOnlyDictionary<string, KnightCisCriterionConfirmation> Index(IEnumerable<KnightCisCriterionConfirmation> confirmations) =>
        confirmations.GroupBy(c => c.RecommendationId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
}
