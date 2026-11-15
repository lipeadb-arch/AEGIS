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
/// </remarks>
public sealed record KnightCisCriterionConfirmation(
    string RecommendationId, string OfficialSource, string Location, string ConsultedOn, string ReviewedBy,
    IReadOnlyList<string> ReviewedCatalogVersions, IReadOnlyList<string> ReviewedRules, string? Note,
    IReadOnlyList<string>? SustainedResults = null, string? UnsustainedReason = null);

public static class KnightCisCriteria
{
    public const string NotConfirmedReason = "Critério da edição CIS ainda não confirmado";

    /// <summary>Edição oficial consultada para os lotes da seção 8 (Microsoft Teams) e da seção 6 (Exchange Online).</summary>
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
        IReadOnlyList<string>? sustained = null, string? unsustained = null) =>
        new("CIS-M365-7.0.0:" + section, M365V700Pdf, $"seção {section}, p. {pages} (Descrição, Auditoria e Valor padrão)", ConsultedOn, Reviewer,
            Reviewed, new[] { rule }, note, sustained, unsustained);

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

    /// <summary>
    /// Confirmações registradas: lote 1 (seção 8, Teams) e lote 2 (seção 6, Exchange Online) do CIS Microsoft 365 Foundations 7.0.0.
    /// </summary>
    public static IReadOnlyList<KnightCisCriterionConfirmation> Registered { get; } = TeamsLot.Concat(ExchangeLot).ToArray();

    public static IReadOnlyDictionary<string, KnightCisCriterionConfirmation> Index(IEnumerable<KnightCisCriterionConfirmation> confirmations) =>
        confirmations.GroupBy(c => c.RecommendationId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
}
