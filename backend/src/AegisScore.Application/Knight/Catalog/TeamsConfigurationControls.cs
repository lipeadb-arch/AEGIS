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

    // ---- Quem entra sem passar pelo lobby -------------------------------------------------------------

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-02] Leitura do valor de “quem ignora o lobby”, com o que CADA valor documentado
    /// admite de fato. O texto observado é o mesmo em tela, HTML, CSV e PDF.
    ///
    /// A correção que este método carrega: <b>InvitedUsers não é “somente internos”</b>. A tabela oficial de
    /// opções do lobby mostra que, com “pessoas que foram convidadas”, também ignoram o lobby os CONVIDADOS e os
    /// participantes de ORGANIZAÇÕES CONFIÁVEIS que receberam o convite — ou a quem o convite foi encaminhado —,
    /// e ainda participantes de organizações não confiáveis autenticados no acesso externo. A própria
    /// documentação diz que a opção "inclui todos os participantes com uma conta corporativa ou de estudante e
    /// os convidados a quem o convite foi encaminhado, não apenas os que o organizador convidou diretamente".
    /// Tratar esse valor como equivalente a excluir convidados aprovava uma configuração que deixa gente de fora
    /// entrar direto, e ainda publicava a frase “somente pessoas da organização entram sem passar pelo lobby”,
    /// incompatível com o valor encontrado.
    ///
    /// Continuam válidos os dois valores que a referência sustenta: <c>EveryoneInCompanyExcludingGuests</c>
    /// (“pessoas da minha organização”) e <c>OrganizerOnly</c>, estritamente mais restritivo.
    /// </summary>
    private static TeamsPolicyReading AutoAdmittedUsers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return TeamsPolicyReading.Unknown();
        var v = value.Trim();

        if (v.Equals("OrganizerOnly", StringComparison.OrdinalIgnoreCase))
            return TeamsPolicyReading.Compliant(
                "somente organizadores e coorganizadores (todo o resto aguarda no lobby)");

        if (v.Equals("EveryoneInCompanyExcludingGuests", StringComparison.OrdinalIgnoreCase))
            return TeamsPolicyReading.Compliant(
                "somente pessoas da organização (convidados, organizações externas e anônimos aguardam no lobby)");

        if (v.Equals("InvitedUsers", StringComparison.OrdinalIgnoreCase))
            return TeamsPolicyReading.NonCompliant(
                "pessoas que foram convidadas — inclui CONVIDADOS e participantes de organizações externas que "
                + "receberam o convite ou a quem ele foi encaminhado, e não apenas pessoas da organização");

        if (v.Equals("EveryoneInCompany", StringComparison.OrdinalIgnoreCase))
            return TeamsPolicyReading.NonCompliant("pessoas da organização e convidados");

        if (v.Equals("EveryoneInSameAndFederatedCompany", StringComparison.OrdinalIgnoreCase))
            return TeamsPolicyReading.NonCompliant(
                "pessoas da organização, de organizações confiáveis e convidados");

        if (v.Equals("Everyone", StringComparison.OrdinalIgnoreCase))
            return TeamsPolicyReading.NonCompliant("todos, inclusive participantes anônimos");

        return TeamsPolicyReading.Unrecognized(v);
    }

    /// <summary>
    /// Quem recebe o papel de APRESENTADOR por padrão. Cada valor nomeia um conjunto DIFERENTE — e é o conjunto
    /// encontrado que o achado precisa dizer, em vez de “todos os participantes”.
    /// </summary>
    private static TeamsPolicyReading DesignatedPresenter(TeamsMeetingPolicyConfiguration p)
    {
        var value = p.DesignatedPresenterRoleMode;
        if (string.IsNullOrWhiteSpace(value)) return TeamsPolicyReading.Unknown();
        var v = value.Trim();

        if (v.Equals("OrganizerOnlyUserOverride", StringComparison.OrdinalIgnoreCase))
            return TeamsPolicyReading.Compliant("somente o organizador e os coorganizadores");

        if (v.Equals("EveryoneInCompanyUserOverride", StringComparison.OrdinalIgnoreCase))
            return TeamsPolicyReading.NonCompliant("todas as pessoas da organização presentes na reunião");

        if (v.Equals("EveryoneInSameAndFederatedCompanyUserOverride", StringComparison.OrdinalIgnoreCase))
            return TeamsPolicyReading.NonCompliant(
                "as pessoas da organização e as de organizações confiáveis presentes na reunião");

        if (v.Equals("EveryoneUserOverride", StringComparison.OrdinalIgnoreCase))
            return TeamsPolicyReading.NonCompliant("todos os participantes, inclusive os de fora da organização");

        return TeamsPolicyReading.Unrecognized(v);
    }

    // ---- Condição de aplicabilidade: quem governa os aplicativos do locatário -------------------------

    /// <summary>
    /// [Revisão dirigida] AK-TEAMS-007 NÃO CONCLUI, e não é uma escolha de cautela: é o que a evidência permite.
    ///
    /// O comando que lê as políticas de permissão é documentado como aplicável só a locatários NÃO migrados para
    /// ACM/UAM. Saber se este locatário migrou exigiria ler o modelo novo, e todos os comandos que fariam essa
    /// leitura estão na lista oficial de NÃO SUPORTADOS com autenticação de aplicativo — a única que este conector
    /// usa. Ver <see cref="TeamsAppGovernance"/>.
    ///
    /// Por isso não existe aqui um caminho que avalie: nem "aprovou porque veio política" (o comando pode devolver
    /// resíduo do modelo antigo), nem "reprovou porque não veio" (ausência não é migração). O que existe é a
    /// configuração legada PRESERVADA como evidência — ela foi lida e o achado diz onde está — com o veredito
    /// suspenso e a dependência real declarada. O parâmetro <paramref name="evaluate"/> guarda a regra do critério,
    /// pronta para o dia em que houver leitura compatível; ela não é chamada enquanto não houver.
    /// </summary>
    private static KnightControlOutcome WhenLegacyAppPoliciesGovern(
        KnightEvaluationContext c, Func<KnightEvaluationContext, KnightControlOutcome> evaluate)
    {
        ArgumentNullException.ThrowIfNull(c);

        var evidence = new List<KnightIndicatorObject>();
        var policies = c.Configuration.Read<TeamsAppPermissionPolicyConfiguration>();
        if (policies.Collected)
        {
            foreach (var p in policies.Items)
                evidence.Add(KnightObjects.Evidence(KnightAffectedObjectKind.Policy,
                    "TeamsAppPermissionPolicy/CatalogAppsType@" + (TeamsPolicyIdentities.Name(p.Identity) ?? "sem-identificacao"),
                    "Política de permissão de aplicativos — " + TeamsPolicyIdentities.Label(p.Identity),
                    "Configuração preservada como evidência: " + Join(p.Catalogs.Select(t => t.Label + ": " + (t.Type ?? "não informado")))
                    + ". Ela NÃO foi usada para concluir o critério porque a coleta não pode demonstrar que as políticas de permissão "
                    + "ainda governam o acesso a aplicativos deste locatário.",
                    "Catálogos de aplicativos: " + Join(p.Catalogs.Select(t => t.Label + " = " + (t.Type ?? "não informado")))));
        }

        return KnightControlOutcome.NotEvaluated(
            "não é possível determinar qual modelo governa o acesso a aplicativos deste locatário — "
            + TeamsAppGovernance.Reason + " Concluir pela configuração legada aprovaria (ou reprovaria) uma configuração que "
            + "pode não estar em vigor. " + TeamsAppGovernance.Requirement,
            evidence);
    }

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
            "Todas as políticas de permissão de aplicativos com os três catálogos restritos a uma lista de permitidos, "
            + "NO LOCATÁRIO em que essas políticas ainda governam o acesso a aplicativos.",
            c => WhenLegacyAppPoliciesGovern(c, c2 => TeamsPolicyReach.Evaluate<TeamsAppPermissionPolicyConfiguration>(c2,
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
                "Os catálogos de aplicativos do Teams estão restritos a listas de permitidos.")),
            // Sem vínculo de referência: 8.4.1 NÃO é avaliado por este controle. Um vínculo aqui faria a cobertura
            // declarar o controle como avaliado (integral ou parcialmente) só porque existe uma regra escrita — e a
            // regra não roda: falta a leitura que diria se a configuração lida governa. A disposição real do 8.4.1
            // está declarada em KnightReferenceDispositions ("exige acesso que o conector não tem"), com o motivo.
            Array.Empty<KnightReferenceLink>()),

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
                // A afirmação é sobre a CONFIGURAÇÃO, e a condição em que ela produz efeito é dita em seguida —
                // a documentação oficial é explícita: este ajuste só vale para participantes anônimos quando
                // "quem pode ignorar o lobby" está em "Todos" e a entrada de anônimos está habilitada; fora
                // disso, ele vale para quem entra por discagem telefônica. Dizer "anônimos iniciam reuniões" a
                // partir do booleano sozinho afirmaria mais do que o valor lido sustenta.
                "A reunião pode começar sem nenhum participante verificado presente. O efeito depende de duas condições "
                + "documentadas: para participantes ANÔNIMOS, é preciso que a entrada de anônimos esteja habilitada e que "
                + "“quem entra sem passar pelo lobby” esteja em “todos”; fora dessas condições, o ajuste vale para quem entra "
                + "por DISCAGEM TELEFÔNICA. Confira AK-TEAMS-008 e AK-TEAMS-010 para saber se as condições estão presentes "
                + "neste locatário.",
                "A reunião da organização só começa com a presença de um participante verificado."),
            Ref(M365 + "8.5.2")),

        Control("AK-TEAMS-010", "Pessoas de fora ignoram o lobby da reunião",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Medium,
            "Configurar a admissão automática para “pessoas da minha organização” — que exclui convidados e participantes "
            + "de organizações externas — ou para “somente organizadores e coorganizadores”, de modo que todo participante "
            + "de fora passe pelo lobby.",
            "Todas as políticas de reunião com admissão automática restrita a pessoas da organização (excluindo convidados) "
            + "ou somente ao organizador e coorganizadores.",
            c => TeamsPolicyReach.Evaluate<TeamsMeetingPolicyConfiguration>(c,
                "TeamsMeetingPolicy/AutoAdmittedUsers", "Quem entra sem passar pelo lobby",
                "pessoas da organização, exceto convidados (ou mais restritivo)",
                p => AutoAdmittedUsers(p.AutoAdmittedUsers),
                "A admissão automática deixa entrar na reunião, sem passar pelo lobby, pessoas que não são da organização.",
                "Somente pessoas da organização entram na reunião sem passar pelo lobby."),
            Ref(M365 + "8.5.3", KnightReferenceMatch.Exact,
                "O critério é o da referência (“somente pessoas da organização ignoram o lobby”). O AEGIS aceita também "
                + "“somente organizadores e coorganizadores”, que é ESTRITAMENTE mais restritivo: todo participante admitido "
                + "por esse valor também seria admitido pelo valor da referência. “Pessoas convidadas” (InvitedUsers) NÃO é "
                + "aceito — ver a nota do critério.")),

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
                DesignatedPresenter,
                // NÃO diz "todos os participantes": o conjunto que recebe o papel varia com o valor encontrado
                // (todos, só quem é da organização, ou a organização mais as confiáveis). O conjunto exato de
                // cada política está no “Encontrado” do objeto correspondente.
                "Pessoas que não organizaram a reunião recebem o papel de apresentador por padrão — podem compartilhar a tela, "
                + "exibir conteúdo e remover outras pessoas da sala. O conjunto que recebe o papel é o indicado em cada política "
                + "abaixo, e não necessariamente todos os presentes.",
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
                // NÃO afirma ausência de todos os controles de segurança: o chat externo é um canal que a
                // organização não administra, mas os controles do ponto final, do navegador e do próprio Teams
                // continuam existindo. O que o achado afirma é o que a configuração sustenta.
                "Pessoas da organização podem participar do chat de reuniões hospedadas por organizações com as quais não há "
                + "relação de confiança estabelecida. É um canal de entrega de mensagens, links e arquivos ADMINISTRADO POR "
                + "TERCEIROS: a organização não define quem participa, não retém a conversa e não a audita. Os controles de "
                + "ponto final e de navegação continuam valendo — o que este ajuste não oferece é controle sobre o canal.",
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
                // NÃO conclui que a descoberta só acontece depois de alguém agir sobre a mensagem: a detecção
                // pode vir de outras fontes (proteção de mensagens, sinais do ponto final, relato por outro
                // canal). O que falta é ESTE caminho — e é isso que o achado afirma.
                "As pessoas não têm no Teams um caminho para relatar uma mensagem suspeita. A denúncia pelo próprio destinatário "
                + "é a via mais rápida e, muitas vezes, a única que identifica uma abordagem dirigida por chat antes de qualquer "
                + "outro sinal. Sem ela, o que chegar à equipe de segurança dependerá de outras fontes de detecção ou de relato "
                + "por fora do Teams.",
                "As pessoas podem relatar mensagens suspeitas pelo próprio Teams."),
            Ref(M365 + "8.6.1", KnightReferenceMatch.Partial,
                "Equivalência parcial: avalia a metade do critério que vive no Teams (a política de mensagens). A outra metade — o destino "
                + "das mensagens denunciadas, definido na política de envio do Microsoft Defender para Office 365 — depende da coleta desse "
                + "serviço, prevista no bloco seguinte.")),
    };
}
