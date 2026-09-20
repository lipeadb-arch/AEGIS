using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Catalog;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-02] Controles de CONFIGURAÇÃO do Microsoft Teams
// ============================================================================
// Controles ORIGINAIS do AEGIS (textos e regras próprios) que cobrem as referências de Microsoft Teams do
// catálogo de referência. Cada um lê a configuração RELIDA da aquisição do ADM e devolve o veredito com os
// objetos que o sustentam. As três regras do bloco anterior continuam valendo:
//   • dado ausente (permissão, licença, falha, coleta anterior) → NÃO AVALIADO com o motivo — nunca aprovado;
//   • a configuração errada é a própria evidência ("onde foi encontrado");
//   • critério que depende de contexto do cliente é dito no texto do achado.
//
// A regra NOVA deste bloco é o ALCANCE. Existir uma política adequada não basta: quando o critério vive numa
// política, ele é verificado em TODAS as instâncias (a padrão da organização e as personalizadas), e o alcance de
// cada uma é dito com o que a coleta tem — atribuições a grupos, quando coletadas. A atribuição DIRETA por conta
// de usuário não é enumerada e fica declarada como limitação (ver <see cref="TeamsPolicyReach"/>).

public static class TeamsConfigurationControls
{
    private static readonly IReadOnlySet<KnightSourceType> TeamsOnly =
        new HashSet<KnightSourceType> { KnightSourceType.MicrosoftTeams };

    private const string M365 = "CIS-M365-7.0.0:";

    private static KnightReferenceLink Ref(string key, KnightReferenceMatch match = KnightReferenceMatch.Exact, string? note = null) =>
        new(key, match, note);

    /// <summary>Sem o contexto da coleta nada pode ser afirmado — o mesmo princípio dos controles do Entra ID.</summary>
    private static KnightIndicatorOutcome FactsOnly(KnightFactSet _) =>
        new(KnightIndicatorStatus.NotEvaluated,
            "Não avaliado: este controle lê a configuração coletada do Microsoft Teams, indisponível nesta avaliação.", 0,
            "Este controle lê a configuração coletada do Microsoft Teams, indisponível nesta avaliação.");

    private static KnightIndicatorDefinition Control(
        string id, string title, KnightIndicatorCategory category, SeverityLevel severity,
        string recommendation, string criterion, Func<KnightEvaluationContext, KnightControlOutcome> evaluate,
        params KnightReferenceLink[] references) =>
        new(id, "1", title, category, severity, TeamsOnly, Array.Empty<string>(), Array.Empty<string>(),
            recommendation, criterion, FactsOnly)
        {
            Service = KnightService.Teams,
            References = references,
            Evaluate = evaluate,
        };

    // ---- Leitor das configurações de instância ÚNICA do locatário -------------------------------------

    private static KnightControlOutcome Single<T>(KnightEvaluationContext c, Func<T, KnightControlOutcome> rule) where T : class
    {
        var read = c.Configuration.Read<T>();
        if (!read.Collected) return KnightControlOutcome.NotEvaluated(read.MissingReason ?? "configuração não coletada.");
        return read.Single is { } doc
            ? rule(doc)
            : KnightControlOutcome.NotEvaluated("a coleta concluiu sem devolver esta configuração do locatário.");
    }

    /// <summary>
    /// Configuração booleana do locatário: esperado → aprovado; oposto → exposto; ausente → não avaliado.
    /// A configuração entra como EVIDÊNCIA (“onde foi encontrado”), nunca na contagem de afetados — a mesma
    /// regra já firmada nos controles do Entra ID: uma configuração do locatário não é um objeto contável.
    /// </summary>
    private static KnightControlOutcome Flag(
        bool? value, bool expected, string settingId, string settingLabel,
        string whenTrue, string whenFalse, string exposedEvidence, string passedEvidence)
    {
        if (value is null)
            return KnightControlOutcome.NotEvaluated($"a fonte não informou “{settingLabel}” nesta coleta.");

        var found = value.Value ? whenTrue : whenFalse;
        var expectedText = expected ? whenTrue : whenFalse;
        var obj = KnightObjects.Setting(settingId, settingLabel, found, expectedText);
        return value.Value == expected
            ? KnightControlOutcome.Passed(passedEvidence, new[] { obj })
            : KnightControlOutcome.Exposed(exposedEvidence, Array.Empty<KnightIndicatorObject>(), new[] { obj });
    }

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Join(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " e " + list[^1],
        };
    }

    // ---- Federação: como a lista de domínios foi expressa ---------------------------------------------

    private static string AllowedDomainsText(TeamsFederationConfiguration f) => f.AllowedDomainsKind switch
    {
        null => "não informado pela fonte",
        TeamsFederationConfiguration.AllowAllKnownDomains =>
            "qualquer organização, exceto as bloqueadas"
            + (f.BlockedDomains.Count > 0 ? $" ({N(f.BlockedDomains.Count)} domínio(s) bloqueado(s))" : " (nenhum domínio bloqueado)"),
        TeamsFederationConfiguration.AllowList =>
            f.AllowedDomains.Count == 0
                ? "somente os domínios da lista — e a lista está vazia"
                : $"somente {N(f.AllowedDomains.Count)} domínio(s) da lista: " + Join(f.AllowedDomains.Take(10)),
        _ => "valor não reconhecido (" + f.AllowedDomainsKind + ")",
    };

    // ---- Catálogo ------------------------------------------------------------------------------------

    public static IReadOnlyList<KnightIndicatorDefinition> Definitions { get; } = new[]
    {
        // ==== Configuração do cliente do Teams ====

        Control("AK-TEAMS-001", "Armazenamento de terceiros habilitado no Teams",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.High,
            "Desabilitar no Teams os serviços de armazenamento de terceiros que não fazem parte do ambiente aprovado, "
            + "mantendo os arquivos em SharePoint e OneDrive, onde valem a retenção, a classificação e a auditoria da organização.",
            "Configuração do cliente do Teams sem nenhum provedor de armazenamento de terceiros habilitado.",
            c => Single<TeamsClientConfiguration>(c, cfg =>
            {
                var unknown = cfg.ThirdPartyStorage.Where(p => p.Allowed is null).Select(p => p.Label).ToList();
                var enabled = cfg.ThirdPartyStorage.Where(p => p.Allowed == true).Select(p => p.Label).ToList();

                if (enabled.Count == 0 && unknown.Count > 0)
                    return KnightControlOutcome.NotEvaluated(
                        $"a fonte não informou o estado de {N(unknown.Count)} provedor(es) — " + Join(unknown)
                        + " — e aprovar o controle exigiria supor que estão desabilitados.");

                var objects = cfg.ThirdPartyStorage
                    .Where(p => p.Allowed is not null)
                    .Select(p => KnightObjects.Setting("teamsClientConfiguration/" + p.Label, "Armazenamento " + p.Label,
                        p.Allowed == true ? "habilitado" : "desabilitado", "desabilitado"))
                    .ToList();

                if (enabled.Count == 0)
                    return KnightControlOutcome.Passed(
                        "Nenhum serviço de armazenamento de terceiros está habilitado no cliente do Teams: os arquivos ficam no armazenamento da organização.",
                        objects);

                return KnightControlOutcome.Exposed(
                    $"O cliente do Teams oferece {N(enabled.Count)} serviço(s) de armazenamento de terceiros: " + Join(enabled)
                    + ". Arquivos enviados por eles saem do SharePoint e do OneDrive e deixam de estar sujeitos à retenção, à classificação e à auditoria da organização."
                    + (unknown.Count > 0 ? $" O estado de {N(unknown.Count)} outro(s) provedor(es) não foi informado pela fonte." : ""),
                    Array.Empty<KnightIndicatorObject>(), objects,
                    complete: unknown.Count == 0,
                    limitation: unknown.Count == 0 ? null
                        : "A fonte não informou o estado de " + Join(unknown) + "; esses provedores permanecem sem veredito.");
            }),
            Ref(M365 + "8.1.1")),

        Control("AK-TEAMS-002", "Envio de e-mail para canal do Teams habilitado",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Medium,
            "Desabilitar o endereço de e-mail dos canais, ou — se o recurso for necessário — restringir os remetentes "
            + "aos domínios aceitos da organização.",
            "Configuração do cliente do Teams com “envio de e-mail para endereço de canal” = Não.",
            c => Single<TeamsClientConfiguration>(c, cfg => Flag(cfg.AllowEmailIntoChannel, false,
                "teamsClientConfiguration/AllowEmailIntoChannel", "Envio de e-mail para endereço de canal", "Sim", "Não",
                "Canais do Teams têm endereço de e-mail e aceitam mensagens enviadas de fora do cliente. "
                + "Uma mensagem entregue por esse caminho chega ao canal sem passar pela identidade de quem a enviou no Teams.",
                "O endereço de e-mail dos canais está desabilitado: mensagens só chegam ao canal pelo próprio Teams.")),
            Ref(M365 + "8.1.2")),

        // ==== Acesso externo (federação) ====

        Control("AK-TEAMS-003", "Acesso externo não restrito a organizações aprovadas",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Medium,
            "Trocar o acesso externo de “qualquer organização” para uma lista fechada de domínios aprovados, ou desligar "
            + "o acesso externo quando ele não for necessário.",
            "Federação desligada, ou lista de domínios permitidos FECHADA (somente os domínios listados).",
            c => Single<TeamsFederationConfiguration>(c, f =>
            {
                var found = AllowedDomainsText(f);
                var setting = "teamsFederationConfiguration/AllowedDomains";
                const string label = "Organizações externas permitidas";
                const string expected = "acesso externo desligado, ou somente domínios de uma lista fechada";

                if (f.AllowFederatedUsers is null)
                    return KnightControlOutcome.NotEvaluated("a fonte não informou se o acesso externo está habilitado.");

                if (f.AllowFederatedUsers == false)
                    return KnightControlOutcome.Passed(
                        "O acesso externo está desligado: contas do Teams de outras organizações não conversam com as desta organização.",
                        new[] { KnightObjects.Setting("teamsFederationConfiguration/AllowFederatedUsers", "Acesso externo", "desligado", "desligado ou restrito a uma lista") });

                if (f.AllowedDomainsKind is null)
                    return KnightControlOutcome.NotEvaluated("a fonte não informou como a lista de domínios permitidos foi expressa.");

                if (string.Equals(f.AllowedDomainsKind, TeamsFederationConfiguration.AllowList, StringComparison.OrdinalIgnoreCase))
                    return KnightControlOutcome.Passed(
                        "O acesso externo está restrito a uma lista fechada de organizações: " + found + ".",
                        new[] { KnightObjects.Setting(setting, label, found, expected) });

                if (!string.Equals(f.AllowedDomainsKind, TeamsFederationConfiguration.AllowAllKnownDomains, StringComparison.OrdinalIgnoreCase))
                    return KnightControlOutcome.NotEvaluated(
                        "a fonte devolveu para a lista de domínios permitidos um valor que este contrato não reconhece (" + f.AllowedDomainsKind + ").",
                        new[] { KnightObjects.Setting(setting, label, found, expected) });

                return KnightControlOutcome.Exposed(
                    "O acesso externo aceita qualquer organização que não esteja bloqueada: " + found
                    + ". Uma lista de bloqueio só barra o que já é conhecido — qualquer domínio novo é aceito por padrão.",
                    Array.Empty<KnightIndicatorObject>(), new[] { KnightObjects.Setting(setting, label, found, expected) });
            }),
            Ref(M365 + "8.2.1")),

        Control("AK-TEAMS-004", "Comunicação com contas não gerenciadas do Teams habilitada",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Medium,
            "Desligar a comunicação com contas do Teams que não são gerenciadas por nenhuma organização, "
            + "ou restringi-la a cenários aprovados e monitorados.",
            "Configuração de federação com “comunicação com contas não gerenciadas do Teams” = Não.",
            c => Single<TeamsFederationConfiguration>(c, f => Flag(f.AllowTeamsConsumer, false,
                "teamsFederationConfiguration/AllowTeamsConsumer", "Comunicação com contas não gerenciadas do Teams", "Sim", "Não",
                "Contas do Teams que não pertencem a nenhuma organização podem conversar com as contas desta organização. "
                + "Essas contas não têm administrador, não têm política corporativa e não respondem a nenhuma governança.",
                "A comunicação com contas do Teams não gerenciadas por uma organização está desligada.")),
            Ref(M365 + "8.2.2")),

        Control("AK-TEAMS-005", "Contas não gerenciadas podem iniciar conversas",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.High,
            "Desligar a entrada de conversas iniciadas por contas do Teams não gerenciadas: quem precisa falar com "
            + "alguém de fora continua podendo iniciar a conversa a partir da organização.",
            "Configuração de federação com “contas não gerenciadas podem iniciar conversas” = Não.",
            c => Single<TeamsFederationConfiguration>(c, f =>
            {
                // A entrada só existe quando a comunicação com contas não gerenciadas está ligada. Dizer "exposto"
                // com a comunicação desligada seria descrever um caminho que não existe no ambiente.
                if (f.AllowTeamsConsumer == false)
                    return KnightControlOutcome.NotApplicable(
                        "a comunicação com contas não gerenciadas do Teams está desligada (ver AK-TEAMS-004); não há conversa de entrada a restringir.",
                        new[] { KnightObjects.Setting("teamsFederationConfiguration/AllowTeamsConsumer", "Comunicação com contas não gerenciadas do Teams", "Não", "Não") });

                return Flag(f.AllowTeamsConsumerInbound, false,
                    "teamsFederationConfiguration/AllowTeamsConsumerInbound", "Contas não gerenciadas podem iniciar conversas", "Sim", "Não",
                    "Qualquer pessoa com uma conta do Teams não gerenciada pode localizar uma conta desta organização e iniciar uma conversa com ela, "
                    + "sem convite e sem relação prévia.",
                    "Conversas com contas não gerenciadas só podem ser iniciadas por quem é da organização.");
            }),
            Ref(M365 + "8.2.3")),

        Control("AK-TEAMS-006", "Comunicação com locatários de avaliação permitida",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.High,
            "Bloquear o acesso externo com locatários que só têm licenças de avaliação e, quando houver um parceiro "
            + "legítimo nessa situação, liberar apenas o domínio dele na lista de exceções.",
            "Configuração de federação com “acesso externo com locatários de avaliação” = Blocked.",
            c => Single<TeamsFederationConfiguration>(c, f =>
            {
                const string setting = "teamsFederationConfiguration/ExternalAccessWithTrialTenants";
                const string label = "Acesso externo com locatários de avaliação";
                const string expected = "bloqueado";
                var value = f.ExternalAccessWithTrialTenants;

                if (string.IsNullOrWhiteSpace(value))
                    return KnightControlOutcome.NotEvaluated($"a fonte não informou “{label}” nesta coleta.");

                if (string.Equals(value, TeamsFederationConfiguration.TrialTenantsBlocked, StringComparison.OrdinalIgnoreCase))
                {
                    var exceptions = f.AllowedTrialTenantDomains.Count;
                    return KnightControlOutcome.Passed(
                        "O acesso externo com locatários de avaliação está bloqueado"
                        + (exceptions > 0
                            ? $", com {N(exceptions)} domínio(s) liberado(s) por exceção: " + Join(f.AllowedTrialTenantDomains.Take(10)) + "."
                            : ", sem exceções."),
                        new[] { KnightObjects.Setting(setting, label, "bloqueado", expected) });
                }

                if (!string.Equals(value, TeamsFederationConfiguration.TrialTenantsAllowed, StringComparison.OrdinalIgnoreCase))
                    return KnightControlOutcome.NotEvaluated(
                        $"a fonte devolveu para “{label}” um valor que este contrato não reconhece ({value}).",
                        new[] { KnightObjects.Setting(setting, label, value, expected) });

                return KnightControlOutcome.Exposed(
                    "O acesso externo aceita locatários que só têm licenças de avaliação. Um locatário de avaliação pode ser criado em minutos, "
                    + "com qualquer nome de organização, e serve de origem barata para mensagens de golpe que chegam com a aparência de um contato corporativo.",
                    Array.Empty<KnightIndicatorObject>(), new[] { KnightObjects.Setting(setting, label, "permitido", expected) });
            }),
            Ref(M365 + "8.2.4")),

        // ==== Aplicativos ====

        Control("AK-TEAMS-007", "Aplicativos do Teams sem lista de permitidos",
            KnightIndicatorCategory.ApplicationGovernance, SeverityLevel.Medium,
            "Configurar as políticas de permissão de aplicativos com uma lista de PERMITIDOS em cada catálogo "
            + "(Microsoft, terceiros e personalizados da organização), em vez de permitir tudo e bloquear caso a caso.",
            "Todas as políticas de permissão de aplicativos com os três catálogos restritos a uma lista de permitidos.",
            c => TeamsPolicyReach.Evaluate<TeamsAppPermissionPolicyConfiguration>(c,
                "TeamsAppPermissionPolicy/CatalogAppsType", "Catálogos de aplicativos",
                "lista de permitidos nos três catálogos",
                p =>
                {
                    var types = p.Catalogs.ToList();
                    if (types.Any(t => string.IsNullOrWhiteSpace(t.Type))) return TeamsPolicyReading.Unknown();

                    var open = types.Where(t => string.Equals(t.Type, TeamsAppPermissionPolicyConfiguration.BlockedAppList, StringComparison.OrdinalIgnoreCase)).ToList();
                    var restricted = types.Where(t => string.Equals(t.Type, TeamsAppPermissionPolicyConfiguration.AllowedAppList, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (open.Count + restricted.Count != types.Count)
                        return TeamsPolicyReading.Unrecognized(string.Join("/", types.Select(t => t.Type)));

                    var described = Join(types.Select(t =>
                        t.Label + ": " + (string.Equals(t.Type, TeamsAppPermissionPolicyConfiguration.AllowedAppList, StringComparison.OrdinalIgnoreCase)
                            ? $"lista de permitidos com {N(t.Count)} item(ns)"
                            : $"tudo permitido exceto {N(t.Count)} item(ns) bloqueado(s)")));

                    return open.Count == 0 ? TeamsPolicyReading.Compliant(described) : TeamsPolicyReading.NonCompliant(described);
                },
                "Aplicativos do Teams podem ser adicionados sem passar por uma lista de permitidos: o padrão é liberar e bloquear caso a caso, "
                + "o que só alcança aplicativos que alguém já identificou como indesejados.",
                "Os catálogos de aplicativos do Teams estão restritos a listas de permitidos."),
            Ref(M365 + "8.4.1", KnightReferenceMatch.Partial,
                "Equivalência parcial: o critério é avaliado pelas políticas de permissão de aplicativos lidas pelo comando oficial. "
                + "A documentação declara que esse comando só se aplica a locatários AINDA NÃO migrados para o gerenciamento centrado em "
                + "aplicativos (ACM/UAM); nos locatários migrados, a configuração autoritativa é outra e não é lida neste bloco.")),

        // ==== Reuniões ====

        Control("AK-TEAMS-008", "Anônimos podem entrar em reuniões",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Medium,
            "Desligar a entrada de participantes anônimos nas políticas de reunião; quem precisa participar de fora "
            + "entra como convidado identificado ou passa pelo lobby.",
            "Todas as políticas de reunião com “anônimos podem entrar” = Não.",
            c => TeamsPolicyReach.Evaluate<TeamsMeetingPolicyConfiguration>(c,
                "TeamsMeetingPolicy/AllowAnonymousUsersToJoinMeeting", "Anônimos podem entrar em reuniões", "Não",
                p => TeamsPolicyReading.Flag(p.AllowAnonymousUsersToJoinMeeting, false),
                "Participantes anônimos podem entrar em reuniões da organização — sem conta, sem identificação verificável e sem registro de quem são.",
                "Participantes anônimos não entram em reuniões da organização."),
            Ref(M365 + "8.5.1")),

        Control("AK-TEAMS-009", "Anônimos podem iniciar reuniões",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Medium,
            "Desligar o início de reuniões por participantes anônimos: a reunião só começa quando alguém da organização entra.",
            "Todas as políticas de reunião com “anônimos podem iniciar reuniões” = Não.",
            c => TeamsPolicyReach.Evaluate<TeamsMeetingPolicyConfiguration>(c,
                "TeamsMeetingPolicy/AllowAnonymousUsersToStartMeeting", "Anônimos podem iniciar reuniões", "Não",
                p => TeamsPolicyReading.Flag(p.AllowAnonymousUsersToStartMeeting, false),
                "Participantes anônimos podem iniciar uma reunião da organização sem que ninguém de dentro esteja presente, "
                + "o que dispensa o lobby e deixa a sala disponível antes de qualquer controle humano.",
                "Uma reunião da organização só começa com a presença de alguém identificado."),
            Ref(M365 + "8.5.2")),

        Control("AK-TEAMS-010", "Pessoas de fora ignoram o lobby da reunião",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Medium,
            "Configurar a admissão automática para “pessoas da organização, exceto convidados” — ou mais restritivo —, "
            + "de modo que todo participante externo passe pelo lobby.",
            "Todas as políticas de reunião com admissão automática restrita a pessoas da organização (excluindo convidados), "
            + "somente o organizador ou somente pessoas convidadas.",
            c => TeamsPolicyReach.Evaluate<TeamsMeetingPolicyConfiguration>(c,
                "TeamsMeetingPolicy/AutoAdmittedUsers", "Quem entra sem passar pelo lobby",
                "pessoas da organização, exceto convidados (ou mais restritivo)",
                p => TeamsPolicyReading.OneOf(p.AutoAdmittedUsers,
                    new[] { "EveryoneInCompanyExcludingGuests", "OrganizerOnly", "InvitedUsers" },
                    new[]
                    {
                        "EveryoneInCompanyExcludingGuests", "OrganizerOnly", "InvitedUsers",
                        "EveryoneInCompany", "EveryoneInSameAndFederatedCompany", "Everyone",
                    }),
                "A admissão automática deixa entrar na reunião, sem passar pelo lobby, pessoas que não são da organização.",
                "Somente pessoas da organização entram na reunião sem passar pelo lobby."),
            Ref(M365 + "8.5.3")),

        Control("AK-TEAMS-011", "Chamadores por telefone ignoram o lobby",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Medium,
            "Exigir que quem entra por discagem telefônica passe pelo lobby, como qualquer outro participante externo.",
            "Todas as políticas de reunião com “chamadores por telefone ignoram o lobby” = Não.",
            c => TeamsPolicyReach.Evaluate<TeamsMeetingPolicyConfiguration>(c,
                "TeamsMeetingPolicy/AllowPSTNUsersToBypassLobby", "Chamadores por telefone ignoram o lobby", "Não",
                p => TeamsPolicyReading.Flag(p.AllowPSTNUsersToBypassLobby, false),
                "Quem disca o número da reunião entra direto, sem passar pelo lobby. O número de telefone não identifica a pessoa, "
                + "e o código de acesso pode ser repassado a qualquer um.",
                "Quem entra por telefone passa pelo lobby e precisa ser admitido."),
            Ref(M365 + "8.5.4")),

        Control("AK-TEAMS-012", "Chat da reunião aberto a participantes anônimos",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Medium,
            "Configurar o chat da reunião para excluir participantes anônimos (ou desligá-lo), mantendo a conversa "
            + "entre pessoas identificadas.",
            "Todas as políticas de reunião com chat habilitado exceto para anônimos, ou desabilitado.",
            c => TeamsPolicyReach.Evaluate<TeamsMeetingPolicyConfiguration>(c,
                "TeamsMeetingPolicy/MeetingChatEnabledType", "Chat da reunião",
                "habilitado exceto para anônimos, ou desabilitado",
                p => TeamsPolicyReading.OneOf(p.MeetingChatEnabledType,
                    new[] { "EnabledExceptAnonymous", "Disabled" },
                    new[] { "EnabledExceptAnonymous", "Disabled", "Enabled" }),
                "Participantes anônimos podem escrever no chat da reunião — inclusive links e arquivos — sem identificação verificável, "
                + "e o conteúdo fica registrado na conversa da reunião.",
                "O chat da reunião não aceita mensagens de participantes anônimos."),
            Ref(M365 + "8.5.5")),

        Control("AK-TEAMS-013", "Qualquer participante pode apresentar",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Medium,
            "Definir que somente o organizador (e os coorganizadores) apresentam por padrão; quem precisar apresentar "
            + "é promovido durante a reunião.",
            "Todas as políticas de reunião com o papel de apresentador restrito ao organizador por padrão.",
            c => TeamsPolicyReach.Evaluate<TeamsMeetingPolicyConfiguration>(c,
                "TeamsMeetingPolicy/DesignatedPresenterRoleMode", "Quem pode apresentar por padrão",
                "somente o organizador (e coorganizadores)",
                p => TeamsPolicyReading.OneOf(p.DesignatedPresenterRoleMode,
                    new[] { "OrganizerOnlyUserOverride" },
                    new[]
                    {
                        "OrganizerOnlyUserOverride", "EveryoneUserOverride", "EveryoneInCompanyUserOverride",
                        "EveryoneInSameAndFederatedCompanyUserOverride",
                    }),
                "Participantes que não organizaram a reunião podem apresentar por padrão: compartilhar a tela, exibir conteúdo "
                + "e remover outras pessoas da sala.",
                "Somente o organizador e os coorganizadores apresentam por padrão."),
            Ref(M365 + "8.5.6")),

        Control("AK-TEAMS-014", "Participantes externos dão ou pedem controle da tela",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Medium,
            "Desligar a transferência de controle da tela para participantes externos nas políticas de reunião.",
            "Todas as políticas de reunião com “participantes externos dão ou pedem controle” = Não.",
            c => TeamsPolicyReach.Evaluate<TeamsMeetingPolicyConfiguration>(c,
                "TeamsMeetingPolicy/AllowExternalParticipantGiveRequestControl", "Controle da tela por participantes externos", "Não",
                p => TeamsPolicyReading.Flag(p.AllowExternalParticipantGiveRequestControl, false),
                "Um participante externo pode pedir — e receber — o controle da tela compartilhada. Quem tem o controle age "
                + "no computador de quem compartilha, com o teclado e o mouse dessa pessoa.",
                "Participantes externos não dão nem pedem controle da tela compartilhada."),
            Ref(M365 + "8.5.7")),

        Control("AK-TEAMS-015", "Chat em reuniões de organizações não confiáveis",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.High,
            "Desligar o chat em reuniões hospedadas por organizações externas não confiáveis.",
            "Todas as políticas de reunião com “chat em reuniões externas não confiáveis” = Não.",
            c => TeamsPolicyReach.Evaluate<TeamsMeetingPolicyConfiguration>(c,
                "TeamsMeetingPolicy/AllowExternalNonTrustedMeetingChat", "Chat em reuniões externas não confiáveis", "Não",
                p => TeamsPolicyReading.Flag(p.AllowExternalNonTrustedMeetingChat, false),
                "Pessoas da organização podem participar do chat de reuniões hospedadas por organizações com as quais não há relação "
                + "de confiança estabelecida — um canal direto para links e arquivos que não passam pelos controles da organização.",
                "O chat de reuniões hospedadas por organizações não confiáveis está desligado."),
            Ref(M365 + "8.5.8")),

        Control("AK-TEAMS-016", "Gravação de reuniões habilitada por padrão",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Low,
            "Desligar a gravação de reuniões por padrão e habilitá-la por política apenas para quem tem necessidade "
            + "demonstrada, com retenção definida.",
            "Todas as políticas de reunião com gravação na nuvem desabilitada.",
            c => TeamsPolicyReach.Evaluate<TeamsMeetingPolicyConfiguration>(c,
                "TeamsMeetingPolicy/AllowCloudRecording", "Gravação de reuniões na nuvem", "Não",
                p => TeamsPolicyReading.Flag(p.AllowCloudRecording, false),
                "Qualquer reunião pode ser gravada. A gravação guarda o que foi dito e mostrado, fica armazenada e pode ser compartilhada depois — "
                + "inclusive quando a conversa tratou de assunto que não deveria ser retido.",
                "A gravação de reuniões na nuvem está desligada por padrão."),
            Ref(M365 + "8.5.9")),

        // ==== Denúncia pelo usuário ====

        Control("AK-TEAMS-017", "Usuários não podem relatar mensagens suspeitas no Teams",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Low,
            "Habilitar a denúncia de problemas de segurança nas políticas de mensagens do Teams e, no Microsoft Defender "
            + "para Office 365, definir o destino das mensagens denunciadas.",
            "Todas as políticas de mensagens com “relatar problemas de segurança” = Sim.",
            c => TeamsPolicyReach.Evaluate<TeamsMessagingPolicyConfiguration>(c,
                "TeamsMessagingPolicy/AllowSecurityEndUserReporting", "Relatar problemas de segurança no Teams", "Sim",
                p => TeamsPolicyReading.Flag(p.AllowSecurityEndUserReporting, true),
                "As pessoas não têm no Teams um caminho para relatar uma mensagem suspeita. Sem esse caminho, a tentativa de golpe "
                + "que chega por chat não vira registro e a equipe de segurança só fica sabendo quando alguém já agiu sobre ela.",
                "As pessoas podem relatar mensagens suspeitas pelo próprio Teams."),
            Ref(M365 + "8.6.1", KnightReferenceMatch.Partial,
                "Equivalência parcial: avalia a metade do critério que vive no Teams (a política de mensagens). A outra metade — o destino "
                + "das mensagens denunciadas, definido na política de envio do Microsoft Defender para Office 365 — depende da coleta desse "
                + "serviço, prevista no bloco seguinte.")),
    };
}
