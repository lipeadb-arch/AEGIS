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

    /// <summary>Edição oficial consultada para o primeiro lote (seção 8 — Microsoft Teams admin center).</summary>
    public const string M365V700Pdf =
        "CIS Microsoft 365 Foundations Benchmark v7.0.0 — PDF oficial do CIS (CIS_Microsoft_365_Foundations_Benchmark_v7.0.0.pdf, documento de 19/05/2026)";

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
        IReadOnlyList<string>? sustained = null, string? unsustained = null) =>
        new("CIS-M365-7.0.0:" + section, M365V700Pdf, $"seção {section}, p. {pages} (Descrição, Auditoria e Valor padrão)", ConsultedOn, Reviewer,
            Reviewed, new[] { rule }, note, sustained, unsustained);

    /// <summary>
    /// Confirmações registradas — primeiro lote: CIS Microsoft 365 Foundations 7.0.0, seção 8 (Microsoft Teams), conferida no PDF
    /// oficial contra a regra KNIGHT e a evidência que ela coleta. Ficam FORA do lote, com o motivo: 8.4.1 (sem regra vinculada —
    /// a leitura exige acesso que o conector não tem) e 8.6.1 (composição com AK-MDO-019, cuja parte no Defender ainda não foi
    /// conferida contra o texto da edição).
    /// </summary>
    public static IReadOnlyList<KnightCisCriterionConfirmation> Registered { get; } = new[]
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

    public static IReadOnlyDictionary<string, KnightCisCriterionConfirmation> Index(IEnumerable<KnightCisCriterionConfirmation> confirmations) =>
        confirmations.GroupBy(c => c.RecommendationId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
}
