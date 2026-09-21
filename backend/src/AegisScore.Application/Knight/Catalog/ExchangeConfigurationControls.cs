using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Catalog;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-03] Controles de CONFIGURAÇÃO do Exchange Online
// ============================================================================
// Controles ORIGINAIS do AEGIS (textos e regras próprios) que cobrem as referências de Exchange Online do
// catálogo de referência. Cada um lê a configuração RELIDA da aquisição do ADM e devolve o veredito com os
// objetos que o sustentam. As regras dos blocos anteriores continuam valendo:
//   • dado ausente (permissão, licença, falha, coleta anterior) → NÃO AVALIADO com o motivo — nunca aprovado;
//   • a configuração errada é a própria evidência ("onde foi encontrado");
//   • critério que depende de contexto do cliente é dito no texto do achado, e não inventado;
//   • existir uma política adequada ≠ o critério valer no ambiente (ver ExchangePolicyReach).
//
// As regras NOVAS deste bloco, cada uma nascida de uma armadilha real do Exchange:
//
//   1. HERANÇA — uma configuração da organização pode ser substituída por caixa de correio, e a AUSÊNCIA do
//      valor na caixa significa "herda". Aprovar a organização sem ler as substituições é aprovar o que não foi
//      verificado (ver AK-EXO-016).
//
//   2. MECANISMOS MÚLTIPLOS — "todas as formas bloqueadas" é uma afirmação sobre um CONJUNTO de mecanismos.
//      Com um mecanismo não lido, a afirmação não pode ser feita, ainda que os outros dois estejam corretos
//      (ver AK-EXO-008).
//
//   3. ENUMERAÇÃO LIMITADA — uma lista truncada em que nada foi encontrado não é ambiente sem problema. As
//      violações encontradas continuam sendo violações; a APROVAÇÃO é que não se sustenta (ver Population).
//
//   4. INÉRCIA — uma regra desabilitada, ou em modo de auditoria, não age sobre mensagem alguma. Apresentá-la
//      como exposição ativa seria descrever um risco que o ambiente não tem (ver AK-EXO-009).

public static class ExchangeConfigurationControls
{
    private static readonly IReadOnlySet<KnightSourceType> ExchangeOnly =
        new HashSet<KnightSourceType> { KnightSourceType.MicrosoftExchangeOnline };

    private const string M365 = "CIS-M365-7.0.0:";

    private static KnightReferenceLink Ref(string key, KnightReferenceMatch match = KnightReferenceMatch.Exact, string? note = null) =>
        new(key, match, note);

    /// <summary>Sem o contexto da coleta nada pode ser afirmado — o mesmo princípio dos blocos anteriores.</summary>
    private static KnightIndicatorOutcome FactsOnly(KnightFactSet _) =>
        new(KnightIndicatorStatus.NotEvaluated,
            "Não avaliado: este controle lê a configuração coletada do Exchange Online, indisponível nesta avaliação.", 0,
            "Este controle lê a configuração coletada do Exchange Online, indisponível nesta avaliação.");

    private static KnightIndicatorDefinition Control(
        string id, string title, KnightIndicatorCategory category, SeverityLevel severity,
        string recommendation, string criterion, Func<KnightEvaluationContext, KnightControlOutcome> evaluate,
        params KnightReferenceLink[] references) =>
        new(id, "1", title, category, severity, ExchangeOnly, Array.Empty<string>(), Array.Empty<string>(),
            recommendation, criterion, FactsOnly)
        {
            Service = KnightService.ExchangeOnline,
            References = references,
            Evaluate = evaluate,
        };

    // ---- Leitores auxiliares ---------------------------------------------------------------------------

    /// <summary>Configuração de instância ÚNICA do locatário: ausente ou não coletada vira motivo, não veredito.</summary>
    private static KnightControlOutcome Single<T>(KnightEvaluationContext c, Func<T, KnightControlOutcome> rule) where T : class
    {
        var read = c.Configuration.Read<T>();
        if (!read.Collected) return KnightControlOutcome.NotEvaluated(read.MissingReason ?? "configuração não coletada.");
        return read.Single is { } doc
            ? rule(doc)
            : KnightControlOutcome.NotEvaluated("a coleta concluiu sem devolver esta configuração da organização.");
    }

    /// <summary>
    /// Configuração booleana da organização: esperado → aprovado; oposto → exposto; ausente → não avaliado.
    /// A configuração entra como EVIDÊNCIA, nunca na contagem de afetados — uma configuração da organização não
    /// é um objeto contável (a mesma regra já firmada nos blocos do Entra ID e do Teams).
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

    /// <summary>
    /// Veredito sobre uma POPULAÇÃO enumerada, com a regra de enumeração limitada aplicada num lugar só.
    ///
    /// A assimetria é deliberada e é o coração da regra: uma violação encontrada numa lista truncada CONTINUA
    /// sendo uma violação demonstrada — o problema existe e tem nome. Já a ausência de violações numa lista
    /// truncada não demonstra nada sobre a organização: o que não foi lido não foi verificado. Por isso o
    /// caminho de aprovação exige enumeração COMPLETA, e o de exposição não.
    /// </summary>
    private static KnightControlOutcome Population(
        bool listComplete,
        IReadOnlyList<KnightIndicatorObject> offenders,
        string exposedEvidence,
        string passedEvidence,
        string truncatedReason,
        IReadOnlyList<KnightIndicatorObject>? evidence = null,
        string? extraLimitation = null)
    {
        var limitation = string.Join(" ", new[]
            {
                listComplete ? null : truncatedReason,
                extraLimitation,
            }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        if (offenders.Count > 0)
            return KnightControlOutcome.Exposed(
                exposedEvidence, offenders, evidence ?? Array.Empty<KnightIndicatorObject>(),
                complete: listComplete && string.IsNullOrWhiteSpace(extraLimitation),
                limitation: string.IsNullOrWhiteSpace(limitation) ? null : limitation);

        if (!listComplete || !string.IsNullOrWhiteSpace(extraLimitation))
            return KnightControlOutcome.NotEvaluated(
                "nenhum problema foi encontrado no trecho lido, mas a leitura não cobre a organização inteira: "
                + limitation, evidence);

        return KnightControlOutcome.Passed(passedEvidence, evidence);
    }

    /// <summary>Alcance resolvido pelas declarações das caixas de correio (atribuição de função e compartilhamento).</summary>
    private static ExchangePolicyReachSource MailboxReach(KnightEvaluationContext c, string policyType)
    {
        var read = c.Configuration.Read<ExchangePolicyReachInventory>();
        if (!read.Collected || read.Single is not { } inv)
            return ExchangePolicyReachSource.NotDemonstrated(
                read.MissingReason ?? "a enumeração de caixas de correio não foi concluída nesta coleta.");
        return new ExchangePolicyReachSource(
            p => inv.CountFor(policyType, p.Name ?? p.Identity),
            true, null, inv.MailboxTotal, inv.ListComplete, inv.MailboxesWithoutDeclaration);
    }

    /// <summary>Alcance das políticas do Outlook na web, declarado pelas configurações de acesso de cliente.</summary>
    private static ExchangePolicyReachSource OwaReach(KnightEvaluationContext c)
    {
        var read = c.Configuration.Read<ExchangeOwaPolicyReachInventory>();
        if (!read.Collected || read.Single is not { } inv)
            return ExchangePolicyReachSource.NotDemonstrated(
                read.MissingReason ?? "a leitura das configurações de acesso de cliente não foi concluída nesta coleta.");
        return new ExchangePolicyReachSource(
            p => inv.CountFor(p.Name ?? p.Identity),
            true, null, inv.MailboxTotal, inv.ListComplete, inv.MailboxesWithoutDeclaration);
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

    /// <summary>Nome da caixa de correio como o operador a encontra: o endereço principal, ou o nome, ou o id.</summary>
    private static string MailboxName(string? upn, string? displayName, string id) =>
        upn ?? displayName ?? (string.IsNullOrWhiteSpace(id) ? "caixa sem identificação na coleta" : id);

    // ---- Catálogo ------------------------------------------------------------------------------------

    public static IReadOnlyList<KnightIndicatorDefinition> Definitions { get; } = new[]
    {
        // ==== Caixas de correio compartilhadas ====

        Control("AK-EXO-001", "Caixas de correio compartilhadas com entrada liberada",
            KnightIndicatorCategory.AccountHygiene, SeverityLevel.High,
            "Bloquear a entrada nas contas das caixas de correio compartilhadas. Quem precisa da caixa continua a "
            + "acessando pela própria conta, com a permissão de acesso delegado — sem que a conta da caixa possa entrar.",
            "Toda caixa de correio compartilhada com a conta correspondente impedida de entrar.",
            c =>
            {
                var read = c.Configuration.Read<ExchangeSharedMailboxInventory>();
                if (!read.Collected) return KnightControlOutcome.NotEvaluated(read.MissingReason ?? "as caixas de correio não foram enumeradas nesta coleta.");
                if (read.Single is not { } inv) return KnightControlOutcome.NotEvaluated("a coleta concluiu sem devolver o inventário de caixas compartilhadas.");

                // "Caixa compartilhada" NÃO implica "conta bloqueada": são dois fatos, lidos por dois comandos.
                // Sem o segundo, o critério fica sem veredito — jamais aprovado pelo tipo da caixa.
                if (!inv.SignInStateResolved)
                    return KnightControlOutcome.NotEvaluated(
                        "o estado de entrada das contas não pôde ser lido nesta coleta, e uma caixa ser compartilhada NÃO "
                        + "significa que a conta correspondente esteja bloqueada. "
                        + (inv.SignInStateLimitation ?? ""),
                        new[] { KnightObjects.Setting("exchangeSharedMailboxInventory/total", "Caixas de correio compartilhadas lidas",
                            N(inv.SharedMailboxTotal), "todas, com o estado de entrada de cada conta") });

                if (inv.SharedMailboxTotal == 0 && inv.ListComplete)
                    return KnightControlOutcome.NotApplicable(
                        "a enumeração concluída não encontrou nenhuma caixa de correio compartilhada neste locatário.");

                var open = inv.Mailboxes.Where(m => m.SignInBlocked == false).ToList();
                var unknown = inv.Mailboxes.Where(m => m.SignInBlocked is null).ToList();

                var offenders = open
                    .Select(m => KnightObjects.Affected(KnightAffectedObjectKind.User,
                        string.IsNullOrWhiteSpace(m.ExternalDirectoryObjectId) ? "caixa:" + (m.UserPrincipalName ?? "?") : m.ExternalDirectoryObjectId,
                        MailboxName(m.UserPrincipalName, m.DisplayName, m.ExternalDirectoryObjectId), m.UserPrincipalName,
                        "A conta desta caixa de correio compartilhada pode entrar. Uma caixa compartilhada não foi feita para ser "
                        + "usada como conta: ninguém é dono dela, a senha não é de uma pessoa e a entrada não fica atrelada a um "
                        + "responsável identificável.",
                        observed: "Entrada na conta: permitida"))
                    .ToList();

                var evidencia = new List<KnightIndicatorObject>
                {
                    KnightObjects.Setting("exchangeSharedMailboxInventory/total", "Caixas de correio compartilhadas lidas",
                        N(inv.SharedMailboxTotal), "todas, com o estado de entrada de cada conta"),
                };

                return Population(
                    inv.ListComplete, offenders,
                    $"{N(open.Count)} de {N(inv.SharedMailboxTotal)} caixa(s) de correio compartilhada(s) têm a conta correspondente "
                    + "habilitada para entrar. Esse é um caminho de acesso que não pertence a ninguém e que não aparece na revisão de contas de pessoas.",
                    $"As {N(inv.SharedMailboxTotal)} caixa(s) de correio compartilhada(s) lidas têm a conta correspondente impedida de entrar.",
                    "A enumeração de caixas de correio atingiu o teto desta coleta: caixas fora do trecho lido não foram verificadas.",
                    evidencia,
                    unknown.Count > 0
                        ? $"{N(unknown.Count)} caixa(s) compartilhada(s) não tiveram a conta correspondente encontrada na leitura de contas "
                          + "desta mesma coleta — o estado de entrada delas permanece desconhecido, e não foi presumido."
                        : null);
            },
            Ref(M365 + "1.2.2", KnightReferenceMatch.Exact,
                "O vínculo entre a caixa e a conta é feito pelo identificador do objeto de diretório que o próprio Exchange devolve, "
                + "na MESMA aquisição — nunca por nome ou endereço semelhante.")),

        // ==== Compartilhamento de calendários ====

        Control("AK-EXO-002", "Calendários compartilhados com quem está fora da organização",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Medium,
            "Remover o compartilhamento de calendário das políticas de compartilhamento, ou desligar as políticas que o "
            + "concedem. Quando houver necessidade demonstrada, restringir a domínios nomeados e ao nível mínimo de detalhe.",
            "Nenhuma política de compartilhamento em vigor concedendo compartilhamento de calendário a domínios externos.",
            c => ExchangePolicyReach.Evaluate<ExchangeSharingPolicyConfiguration>(c,
                MailboxReach(c, ExchangePolicyReachInventory.SharingPolicyType),
                "SharingPolicy/CalendarSharing", "Compartilhamento de calendário com domínios externos",
                "nenhuma entrada de compartilhamento de calendário",
                p =>
                {
                    // Uma política DESLIGADA não concede nada. Reprová-la descreveria um risco que o ambiente não tem;
                    // aprová-la sustentaria o ambiente com uma política que não está em vigor. Nem um, nem outro.
                    if (p.Enabled == false)
                        return ExchangePolicyReading.Inert("política desligada — não concede compartilhamento algum");
                    if (p.Enabled is null)
                        return ExchangePolicyReading.Unknown();

                    var entries = p.CalendarSharingEntries;
                    if (entries.Count == 0) return ExchangePolicyReading.Compliant("nenhuma entrada de compartilhamento de calendário");

                    var anonymous = entries
                        .Where(e => e.Domain.Equals(ExchangeSharingPolicyConfiguration.AnonymousDomain, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var described = Join(entries.Take(6).Select(e =>
                        (e.Domain.Equals(ExchangeSharingPolicyConfiguration.AnonymousDomain, StringComparison.OrdinalIgnoreCase)
                            ? "qualquer pessoa, sem identificação"
                            : e.Domain)
                        + (string.IsNullOrEmpty(e.Shared) ? "" : " (" + e.Shared + ")")));

                    return ExchangePolicyReading.NonCompliant(
                        $"{N(entries.Count)} entrada(s) de compartilhamento de calendário: {described}"
                        + (entries.Count > 6 ? "…" : "")
                        + (anonymous.Count > 0 ? " — inclui compartilhamento com quem não precisa se identificar" : ""));
                },
                "O calendário das pessoas da organização pode ser compartilhado para fora dela. Um calendário revela quem se reúne "
                + "com quem, quando a pessoa está fora e como o nome das reuniões descreve o que a organização está fazendo.",
                "Nenhuma política de compartilhamento em vigor concede compartilhamento de calendário a domínios externos."),
            Ref(M365 + "1.3.3")),

        // ==== Customer Lockbox ====

        Control("AK-EXO-003", "Customer Lockbox desabilitado",
            KnightIndicatorCategory.TenantConfiguration, SeverityLevel.Low,
            "Habilitar o Customer Lockbox, para que qualquer acesso de engenharia da Microsoft ao conteúdo da organização "
            + "passe por aprovação explícita de um administrador do cliente, com registro.",
            "Configuração da organização com Customer Lockbox habilitado.",
            c => Single<ExchangeOrganizationConfiguration>(c, cfg => Flag(cfg.CustomerLockBoxEnabled, true,
                "exchangeOrganizationConfiguration/CustomerLockBoxEnabled", "Customer Lockbox", "habilitado", "desabilitado",
                "Um pedido de acesso da engenharia do fornecedor ao conteúdo da organização pode ser concedido sem aprovação "
                + "explícita de alguém do cliente. O recurso não afirma que houve acesso: ele define quem autoriza, caso aconteça.",
                "O Customer Lockbox está habilitado: acesso da engenharia do fornecedor ao conteúdo depende de aprovação de um administrador do cliente.")),
            Ref(M365 + "1.3.6")),

        // ==== Bookings ====

        Control("AK-EXO-004", "Páginas de agendamento compartilhadas sem restrição",
            KnightIndicatorCategory.TenantConfiguration, SeverityLevel.High,
            "Desligar o Bookings na organização quando ele não for usado; quando for, restringir a criação de caixas de "
            + "agendamento às políticas do Outlook na web de quem tem necessidade demonstrada.",
            "Bookings desligado na organização, OU criação de caixa de agendamento desabilitada nas políticas do Outlook na web.",
            c =>
            {
                var org = c.Configuration.Read<ExchangeOrganizationConfiguration>();
                if (!org.Collected) return KnightControlOutcome.NotEvaluated(org.MissingReason ?? "a configuração da organização não foi coletada.");
                if (org.Single is not { } cfg) return KnightControlOutcome.NotEvaluated("a coleta concluiu sem devolver a configuração da organização.");

                var bookings = KnightObjects.Setting("exchangeOrganizationConfiguration/BookingsEnabled",
                    "Bookings na organização", KnightObjects.YesNo(cfg.BookingsEnabled), "desligado, ou com a criação de caixas restrita");

                // Bookings desligado na organização resolve o critério inteiro: não há página a restringir.
                if (cfg.BookingsEnabled == false)
                    return KnightControlOutcome.Passed(
                        "O Bookings está desligado na organização: não existe página de agendamento compartilhada a restringir.",
                        new[] { bookings });

                if (cfg.BookingsEnabled is null)
                    return KnightControlOutcome.NotEvaluated(
                        "a fonte não informou se o Bookings está habilitado na organização, e o critério depende disso.");

                return ExchangePolicyReach.Evaluate<ExchangeOwaMailboxPolicyConfiguration>(c, OwaReach(c),
                    "OwaMailboxPolicy/BookingsMailboxCreationEnabled", "Criação de caixa de agendamento",
                    "desabilitada (com o Bookings ligado na organização)",
                    p => ExchangePolicyReading.Flag(p.BookingsMailboxCreationEnabled, false),
                    "O Bookings está ligado na organização e há política do Outlook na web que permite criar caixas de agendamento. "
                    + "Uma página de agendamento é publicada na internet: quem tem o endereço vê nomes, funções, disponibilidade da "
                    + "equipe e pode marcar horários sem ser da organização.",
                    "O Bookings está ligado na organização, mas nenhuma política do Outlook na web em vigor permite criar caixas de agendamento.");
            },
            Ref(M365 + "1.3.9", KnightReferenceMatch.Exact,
                "A referência verifica a política PADRÃO do Outlook na web; o AEGIS verifica TODAS as instâncias, e a padrão está "
                + "sempre entre elas. Uma política personalizada que permita a criação também reprova — é estritamente mais completo.")),

        // ==== Auditoria ====

        Control("AK-EXO-005", "Auditoria de caixas de correio desabilitada na organização",
            KnightIndicatorCategory.TenantConfiguration, SeverityLevel.Medium,
            "Habilitar a auditoria de caixas de correio no nível da organização, para que as ações padrão sejam registradas em "
            + "toda caixa, inclusive nas criadas depois.",
            "Configuração da organização com a auditoria de caixas de correio NÃO desabilitada.",
            c => Single<ExchangeOrganizationConfiguration>(c, cfg => Flag(cfg.AuditDisabled, false,
                "exchangeOrganizationConfiguration/AuditDisabled", "Auditoria de caixas de correio na organização",
                "desabilitada", "habilitada",
                "A auditoria de caixas de correio está desabilitada no nível da organização. Sem ela, o acesso a mensagens, a "
                + "exclusão de itens e a criação de regras não deixam registro — e uma investigação posterior não tem o que examinar. "
                + "Este é o interruptor da organização; ele NÃO responde sozinho quais ações estão sendo auditadas nem se alguma "
                + "conta foi dispensada da auditoria (ver AK-EXO-006 e AK-EXO-007).",
                "A auditoria de caixas de correio está habilitada no nível da organização. Isso não conclui, por si, quais ações "
                + "são registradas nem se há contas dispensadas — esses são os controles AK-EXO-006 e AK-EXO-007.")),
            Ref(M365 + "6.1.1")),

        Control("AK-EXO-006", "Ações de auditoria incompletas nas caixas de correio",
            KnightIndicatorCategory.TenantConfiguration, SeverityLevel.Medium,
            "Configurar, em cada caixa de correio, as ações de auditoria dos três tipos de acesso — do próprio dono, do acesso "
            + "delegado e do acesso administrativo —, de modo que exclusão, movimentação, envio, leitura de itens e alteração de "
            + "regras deixem registro.",
            "Todas as caixas de correio de usuário e compartilhadas com as ações de auditoria dos três tipos de acesso configuradas.",
            c =>
            {
                var read = c.Configuration.Read<ExchangeMailboxAuditInventory>();
                if (!read.Collected) return KnightControlOutcome.NotEvaluated(read.MissingReason ?? "as caixas de correio não foram enumeradas nesta coleta.");
                if (read.Single is not { } inv) return KnightControlOutcome.NotEvaluated("a coleta concluiu sem devolver o inventário de auditoria das caixas.");
                if (inv.MailboxTotal == 0 && inv.ListComplete)
                    return KnightControlOutcome.NotApplicable("a enumeração concluída não encontrou caixa de correio de usuário ou compartilhada.");

                var offenders = inv.MailboxesWithGaps
                    .Select(m =>
                    {
                        var faltas = new List<string>();
                        if (m.MissingOwnerActions.Count > 0) faltas.Add($"acesso do dono: {Join(m.MissingOwnerActions)}");
                        if (m.MissingDelegateActions.Count > 0) faltas.Add($"acesso delegado: {Join(m.MissingDelegateActions)}");
                        if (m.MissingAdminActions.Count > 0) faltas.Add($"acesso administrativo: {Join(m.MissingAdminActions)}");
                        var detalhe = m.AuditEnabled == false
                            ? "A auditoria está desligada NESTA caixa. "
                            : "";
                        return KnightObjects.Affected(KnightAffectedObjectKind.User,
                            string.IsNullOrWhiteSpace(m.ExternalDirectoryObjectId) ? "caixa:" + (m.UserPrincipalName ?? "?") : m.ExternalDirectoryObjectId,
                            MailboxName(m.UserPrincipalName, m.DisplayName, m.ExternalDirectoryObjectId), m.UserPrincipalName,
                            detalhe + (faltas.Count > 0
                                ? "Ações de auditoria não configuradas — " + Join(faltas) + "."
                                : "A auditoria está desligada nesta caixa."),
                            observed: faltas.Count > 0
                                ? $"Ações faltando em {N(faltas.Count)} tipo(s) de acesso"
                                : "Auditoria desligada na caixa");
                    })
                    .ToList();

                return Population(
                    inv.ListComplete, offenders,
                    $"{N(offenders.Count)} de {N(inv.MailboxTotal)} caixa(s) de correio não registram todas as ações que o critério exige, "
                    + "em pelo menos um dos três tipos de acesso. Auditar o acesso do dono não audita o acesso delegado, e auditar o "
                    + "delegado não audita o administrativo: são registros independentes, e uma investigação que precise de um dos três "
                    + "não encontra o que não foi registrado.",
                    $"As {N(inv.MailboxTotal)} caixa(s) de correio lidas registram as ações exigidas nos três tipos de acesso.",
                    "A enumeração de caixas de correio atingiu o teto desta coleta: caixas fora do trecho lido não foram verificadas.",
                    new[]
                    {
                        KnightObjects.Setting("exchangeMailboxAuditInventory/total", "Caixas de correio verificadas",
                            N(inv.MailboxTotal), "todas as de usuário e compartilhadas"),
                    });
            },
            Ref(M365 + "6.1.2", KnightReferenceMatch.Exact,
                "O critério é verificado nas propriedades de auditoria que cada caixa devolve, tipo de acesso a tipo de acesso — o mesmo "
                + "procedimento da referência. Quando a auditoria padrão da organização está ligada (AK-EXO-005), ações não listadas na "
                + "caixa ainda podem ser registradas pelo conjunto padrão do serviço; por isso o achado descreve o que está CONFIGURADO "
                + "na caixa, e não afirma que a ação não é registrada em nenhuma hipótese.")),

        Control("AK-EXO-007", "Contas dispensadas da auditoria de caixa de correio",
            KnightIndicatorCategory.TenantConfiguration, SeverityLevel.Medium,
            "Remover o desvio de auditoria das contas que o tenham, salvo exceção escrita e revisada. Enquanto o desvio existir, "
            + "nenhuma ação da conta sobre qualquer caixa de correio é registrada.",
            "Nenhuma conta com desvio de auditoria de caixa de correio habilitado.",
            c =>
            {
                var read = c.Configuration.Read<ExchangeAuditBypassInventory>();
                if (!read.Collected) return KnightControlOutcome.NotEvaluated(read.MissingReason ?? "as associações de desvio de auditoria não foram lidas nesta coleta.");
                if (read.Single is not { } inv) return KnightControlOutcome.NotEvaluated("a coleta concluiu sem devolver o inventário de desvio de auditoria.");

                var offenders = inv.BypassEnabled
                    .Select(b => KnightObjects.Affected(KnightAffectedObjectKind.User, b.Identity,
                        b.DisplayName ?? b.Identity, null,
                        "Esta conta tem o desvio de auditoria habilitado: nenhuma ação dela sobre qualquer caixa de correio é "
                        + "registrada. O desvio existe para reduzir ruído de contas de serviço confiáveis — e, onde existe, ele "
                        + "também apaga o rastro de um uso indevido dessa conta.",
                        observed: "Desvio de auditoria: habilitado"))
                    .ToList();

                return Population(
                    inv.ListComplete, offenders,
                    $"{N(offenders.Count)} conta(s) estão dispensadas da auditoria de caixa de correio.",
                    inv.AssociationsTotal == 0
                        ? "A leitura concluída não encontrou nenhuma associação de desvio de auditoria neste locatário."
                        : $"Nenhuma das {N(inv.AssociationsTotal)} associação(ões) de desvio de auditoria lidas está habilitada.",
                    "A leitura das associações de desvio de auditoria atingiu o teto desta coleta: o que ficou fora não foi verificado.",
                    new[]
                    {
                        KnightObjects.Setting("exchangeAuditBypassInventory/total", "Associações de desvio lidas",
                            N(inv.AssociationsTotal), "todas, com o estado de cada uma"),
                    });
            },
            Ref(M365 + "6.1.3")),

        // ==== Encaminhamento ====

        Control("AK-EXO-008", "Encaminhamento de e-mail para fora da organização não está bloqueado",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.High,
            "Bloquear o encaminhamento automático nas políticas de filtro de spam de saída, remover o encaminhamento configurado "
            + "nas caixas de correio e eliminar as regras de transporte que desviam ou copiam mensagens para fora.",
            "Os TRÊS mecanismos verificados e fechados: encaminhamento automático desligado nas políticas de saída, nenhuma caixa "
            + "com encaminhamento configurado e nenhuma regra de transporte em vigor desviando mensagens.",
            c =>
            {
                // ESTE é o controle da regra nº 2 do cabeçalho: "todas as formas bloqueadas" é uma afirmação sobre um
                // CONJUNTO. Cada mecanismo é uma leitura própria; faltando uma, a afirmação não pode ser feita — ainda
                // que as outras duas estejam impecáveis. O que o controle NUNCA faz é dizer "todas as formas
                // bloqueadas" tendo verificado duas.
                var naoLidos = new List<string>();
                var afetados = new List<KnightIndicatorObject>();
                var evidencias = new List<KnightIndicatorObject>();
                var limitacoes = new List<string>();

                // Mecanismo 1 — encaminhamento automático nas políticas de filtro de spam de saída.
                var spam = c.Configuration.Read<ExchangeOutboundSpamPolicyConfiguration>();
                if (!spam.Collected)
                    naoLidos.Add("o encaminhamento automático das políticas de saída");
                else if (spam.Items.Count == 0)
                    naoLidos.Add("o encaminhamento automático (nenhuma política de saída devolvida)");
                else
                {
                    foreach (var p in spam.Items)
                    {
                        var modo = p.AutoForwardingMode;
                        var rotulo = ExchangePolicyIdentities.Label(p);
                        var id = "OutboundSpamFilterPolicy/AutoForwardingMode@" + (ExchangePolicyIdentities.Key(p) ?? "sem-identificacao");
                        if (string.IsNullOrWhiteSpace(modo))
                        {
                            naoLidos.Add("o encaminhamento automático da " + rotulo);
                            continue;
                        }
                        if (modo.Trim().Equals(ExchangeOutboundSpamPolicyConfiguration.ForwardingOff, StringComparison.OrdinalIgnoreCase))
                        {
                            evidencias.Add(KnightObjects.Evidence(KnightAffectedObjectKind.Policy, id, rotulo,
                                "Encaminhamento automático desligado nesta política.", "Encaminhamento automático: desligado"));
                            continue;
                        }
                        afetados.Add(KnightObjects.Affected(KnightAffectedObjectKind.Policy, id, rotulo, null,
                            modo.Trim().Equals(ExchangeOutboundSpamPolicyConfiguration.ForwardingAutomatic, StringComparison.OrdinalIgnoreCase)
                                ? "O encaminhamento automático está no modo em que o SERVIÇO decide. O comportamento padrão do serviço "
                                  + "hoje bloqueia o encaminhamento, mas é o serviço que o define e pode mudá-lo — não é a organização "
                                  + "que decidiu bloquear."
                                : "O encaminhamento automático está liberado por esta política: mensagens podem sair da organização "
                                  + "automaticamente para endereços de fora.",
                            observed: "Encaminhamento automático: " + modo.Trim()));
                    }
                }

                // Mecanismo 2 — encaminhamento configurado nas caixas de correio.
                var forwarding = c.Configuration.Read<ExchangeMailboxForwardingInventory>();
                if (!forwarding.Collected || forwarding.Single is null)
                    naoLidos.Add("o encaminhamento configurado nas caixas de correio");
                else
                {
                    var inv = forwarding.Single!;
                    if (!inv.ListComplete)
                        limitacoes.Add("A enumeração de caixas de correio atingiu o teto desta coleta: caixas fora do trecho lido não foram verificadas.");
                    foreach (var m in inv.MailboxesWithForwarding)
                    {
                        var destino = m.ForwardingSmtpAddress ?? m.ForwardingAddress ?? "destino não informado";
                        afetados.Add(KnightObjects.Affected(KnightAffectedObjectKind.User,
                            string.IsNullOrWhiteSpace(m.ExternalDirectoryObjectId) ? "caixa:" + (m.UserPrincipalName ?? "?") : m.ExternalDirectoryObjectId,
                            MailboxName(m.UserPrincipalName, m.DisplayName, m.ExternalDirectoryObjectId), m.UserPrincipalName,
                            $"Esta caixa de correio encaminha mensagens para {destino}"
                            + (m.DeliverToMailboxAndForward == true
                                ? ", mantendo também uma cópia na caixa — o que torna o encaminhamento menos perceptível para quem a usa."
                                : ".")
                            + " O AEGIS lê a CONFIGURAÇÃO: ela mostra para onde as mensagens vão, e não que alguma mensagem específica tenha sido lida por terceiros.",
                            observed: "Encaminhamento para: " + destino));
                    }
                    evidencias.Add(KnightObjects.Setting("exchangeMailboxForwardingInventory/total", "Caixas de correio verificadas",
                        N(inv.MailboxTotal), "todas, sem encaminhamento configurado"));
                }

                // Mecanismo 3 — regras de transporte que desviam ou copiam mensagens.
                var rules = c.Configuration.Read<ExchangeTransportRuleConfiguration>();
                if (!rules.Collected)
                    naoLidos.Add("as regras de transporte que desviam mensagens");
                else
                {
                    foreach (var r in rules.Items.Where(r => r.ForwardingTargets.Count > 0))
                    {
                        var nome = r.Name ?? r.Identity ?? "regra sem nome na coleta";
                        var id = "TransportRule/Forwarding@" + (r.Identity ?? r.Name ?? nome);
                        var destinos = Join(r.ForwardingTargets.Take(5));
                        if (r.ProducesEffect == false)
                        {
                            // Regra inerte é EVIDÊNCIA, não exposição: ela não age sobre mensagem alguma.
                            evidencias.Add(KnightObjects.Evidence(KnightAffectedObjectKind.Policy, id, "Regra de transporte “" + nome + "”",
                                $"A regra desvia mensagens para {destinos}, mas NÃO produz efeito no ambiente "
                                + $"(estado: {r.State ?? "não informado"}; modo: {r.Mode ?? "não informado"}). "
                                + "Registrada como evidência: se for habilitada, passa a encaminhar.",
                                "Regra sem efeito — desvia para: " + destinos));
                            continue;
                        }
                        if (r.ProducesEffect is null)
                        {
                            naoLidos.Add($"se a regra “{nome}” está em vigor");
                            continue;
                        }
                        afetados.Add(KnightObjects.Affected(KnightAffectedObjectKind.Policy, id, "Regra de transporte “" + nome + "”", null,
                            $"Esta regra está em vigor e desvia ou copia mensagens para {destinos}. Um encaminhamento por regra de "
                            + "transporte não aparece na caixa de ninguém: ele age no serviço, antes da entrega.",
                            observed: "Desvia para: " + destinos));
                    }
                }

                if (naoLidos.Count > 0)
                {
                    // O motivo é GRAVADO numa coluna com limite de 500 caracteres, e ele é montado a partir de uma
                    // lista de tamanho variável (uma entrada por mecanismo, mais uma por política ou regra sem
                    // estado). Por isso a lista é RESUMIDA aqui: os três primeiros itens, com a contagem do resto.
                    // O detalhe de cada leitura continua inteiro nos objetos de evidência, que não têm esse limite.
                    var faltando = naoLidos.Count <= 3
                        ? Join(naoLidos)
                        : Join(naoLidos.Take(3)) + $" e mais {N(naoLidos.Count - 3)}";
                    return KnightControlOutcome.NotEvaluated(
                        "o critério exige TRÊS mecanismos de encaminhamento fechados, e nesta coleta não foi possível ler "
                        + faltando + ". Declarar “todas as formas bloqueadas” sem ler um dos mecanismos afirmaria mais do que a "
                        + "coleta sustenta"
                        + (afetados.Count > 0 ? "; o encaminhamento JÁ encontrado está preservado abaixo" : "") + ".",
                        evidencias.Concat(afetados.Select(a => a with { Relation = KnightObjectRelation.Evidence })).ToList());
                }

                if (afetados.Count > 0)
                    return KnightControlOutcome.Exposed(
                        $"Há {N(afetados.Count)} caminho(s) pelos quais mensagens da organização saem automaticamente para endereços de fora. "
                        + "O encaminhamento automático é o passo seguinte típico de uma conta comprometida: ele mantém o acesso à correspondência "
                        + "mesmo depois de a senha ser trocada.",
                        afetados, evidencias,
                        complete: limitacoes.Count == 0,
                        limitation: limitacoes.Count == 0 ? null : Join(limitacoes));

                if (limitacoes.Count > 0)
                    return KnightControlOutcome.NotEvaluated(
                        "nenhum encaminhamento foi encontrado no trecho lido, mas a leitura não cobre a organização inteira: " + Join(limitacoes),
                        evidencias);

                return KnightControlOutcome.Passed(
                    "Os três mecanismos de encaminhamento foram verificados e estão fechados: as políticas de filtro de spam de saída "
                    + "desligam o encaminhamento automático, nenhuma caixa de correio lida tem encaminhamento configurado e nenhuma regra "
                    + "de transporte em vigor desvia mensagens.",
                    evidencias);
            },
            Ref(M365 + "6.2.1", KnightReferenceMatch.Exact,
                "O critério é verificado nos três mecanismos que o compõem — política de saída, caixa de correio e regra de transporte. "
                + "Um mecanismo não lido impede a aprovação: o controle fica não avaliado com o mecanismo nomeado.")),

        Control("AK-EXO-009", "Regras de transporte isentam remetentes da filtragem de spam",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.High,
            "Remover das regras de transporte a isenção de filtragem por domínio ou endereço de remetente. Quando um parceiro "
            + "legítimo precisar de tratamento diferenciado, usar autenticação do remetente em vez de uma lista por domínio.",
            "Nenhuma regra de transporte EM VIGOR isentando remetentes identificados por domínio ou endereço da filtragem de spam.",
            c =>
            {
                var read = c.Configuration.Read<ExchangeTransportRuleConfiguration>();
                if (!read.Collected) return KnightControlOutcome.NotEvaluated(read.MissingReason ?? "as regras de transporte não foram lidas nesta coleta.");
                if (read.Items.Count == 0)
                    return KnightControlOutcome.NotApplicable("a leitura concluída não encontrou nenhuma regra de transporte neste locatário.");

                var candidatas = read.Items
                    .Where(r => r.SetScl == ExchangeTransportRuleConfiguration.BypassSpamFiltering && r.SenderScope.Count > 0)
                    .ToList();

                var indeterminadas = candidatas.Where(r => r.ProducesEffect is null).ToList();
                var inertes = candidatas.Where(r => r.ProducesEffect == false).ToList();
                var ativas = candidatas.Where(r => r.ProducesEffect == true).ToList();

                var evidencias = new List<KnightIndicatorObject>
                {
                    KnightObjects.Setting("exchangeTransportRules/total", "Regras de transporte lidas",
                        N(read.Items.Count), "todas, com o estado e o modo de cada uma"),
                };

                // Regras inertes entram como EVIDÊNCIA, com o motivo: existem, não agem, e passam a agir se
                // alguém as habilitar. Contá-las como exposição descreveria um risco que o ambiente não tem.
                evidencias.AddRange(inertes.Select(r => KnightObjects.Evidence(KnightAffectedObjectKind.Policy,
                    "TransportRule/SetScl@" + (r.Identity ?? r.Name ?? "sem-identificacao"),
                    "Regra de transporte “" + (r.Name ?? r.Identity ?? "sem nome na coleta") + "”",
                    $"Isenta da filtragem de spam {N(r.SenderScope.Count)} remetente(s), mas NÃO produz efeito "
                    + $"(estado: {r.State ?? "não informado"}; modo: {r.Mode ?? "não informado"}). "
                    + "Registrada como evidência: habilitá-la basta para a isenção passar a valer.",
                    "Regra sem efeito — isenta: " + Join(r.SenderScope.Take(5)))));

                if (indeterminadas.Count > 0)
                    return KnightControlOutcome.NotEvaluated(
                        $"{N(indeterminadas.Count)} regra(s) isentam remetentes da filtragem, mas a fonte não informou o estado ou o modo "
                        + "delas — sem isso não é possível dizer se agem sobre as mensagens: "
                        + Join(indeterminadas.Select(r => "“" + (r.Name ?? r.Identity ?? "sem nome") + "”")),
                        evidencias);

                if (ativas.Count == 0)
                    return KnightControlOutcome.Passed(
                        $"Nenhuma das {N(read.Items.Count)} regra(s) de transporte em vigor isenta remetentes da filtragem de spam."
                        + (inertes.Count > 0
                            ? $" {N(inertes.Count)} regra(s) com essa isenção existem, mas não produzem efeito — estão registradas abaixo."
                            : ""),
                        evidencias);

                var afetadas = ativas
                    .Select(r => KnightObjects.Affected(KnightAffectedObjectKind.Policy,
                        "TransportRule/SetScl@" + (r.Identity ?? r.Name ?? "sem-identificacao"),
                        "Regra de transporte “" + (r.Name ?? r.Identity ?? "sem nome na coleta") + "”", null,
                        $"Esta regra está em vigor e dispensa da filtragem de spam as mensagens de {N(r.SenderScope.Count)} remetente(s): "
                        + Join(r.SenderScope.Take(8)) + (r.SenderScope.Count > 8 ? "…" : "")
                        + ". Domínio e endereço de remetente são campos que qualquer pessoa pode escrever numa mensagem; uma isenção "
                        + "por esses campos vale para quem os imitar.",
                        observed: $"Isenta da filtragem: {Join(r.SenderScope.Take(3))}{(r.SenderScope.Count > 3 ? "…" : "")}"))
                    .ToList();

                return KnightControlOutcome.Exposed(
                    $"{N(ativas.Count)} regra(s) de transporte em vigor dispensam remetentes da filtragem de spam a partir do domínio "
                    + "ou do endereço declarado na mensagem.",
                    afetadas, evidencias);
            },
            Ref(M365 + "6.2.2", KnightReferenceMatch.Exact,
                "O critério é o da referência. O AEGIS acrescenta a leitura do ESTADO e do MODO de cada regra: uma regra desabilitada, ou "
                + "em modo de auditoria, não age sobre mensagem alguma e por isso é registrada como evidência, não como exposição.")),

        Control("AK-EXO-010", "Mensagens de remetentes externos não são identificadas",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Medium,
            "Habilitar a identificação de remetentes externos no Outlook, sem isenções. A marca aparece na mensagem e ajuda quem a "
            + "recebe a perceber que ela não veio de dentro.",
            "Identificação de remetentes externos habilitada e SEM domínios ou endereços isentos.",
            c => Single<ExchangeExternalSenderIdentification>(c, cfg =>
            {
                const string settingId = "exchangeExternalSenderIdentification/Enabled";
                const string label = "Identificação de remetentes externos";
                const string expected = "habilitada, sem isenções";

                if (cfg.Enabled is null)
                    return KnightControlOutcome.NotEvaluated($"a fonte não informou se a “{label}” está habilitada.");

                if (cfg.Enabled == false)
                    return KnightControlOutcome.Exposed(
                        "Mensagens vindas de fora da organização chegam sem nenhuma marca que as distinga das internas. A identificação "
                        + "não bloqueia nada — ela dá a quem lê um sinal de que o remetente não é da organização, no momento em que a "
                        + "pessoa decide se confia na mensagem.",
                        Array.Empty<KnightIndicatorObject>(),
                        new[] { KnightObjects.Setting(settingId, label, "desabilitada", expected) });

                if (cfg.AllowList.Count == 0)
                    return KnightControlOutcome.Passed(
                        "A identificação de remetentes externos está habilitada, sem isenções: toda mensagem de fora chega marcada.",
                        new[] { KnightObjects.Setting(settingId, label, "habilitada, sem isenções", expected) });

                return KnightControlOutcome.Exposed(
                    $"A identificação de remetentes externos está habilitada, mas {N(cfg.AllowList.Count)} remetente(s) estão isentos: "
                    + Join(cfg.AllowList.Take(10)) + (cfg.AllowList.Count > 10 ? "…" : "")
                    + ". Mensagens desses remetentes chegam SEM a marca de externo — justamente a aparência que uma mensagem de golpe "
                    + "procura ter, e a isenção vale por domínio declarado, não por remetente comprovado.",
                    new[]
                    {
                        KnightObjects.AffectedSetting("exchangeExternalSenderIdentification/AllowList",
                            "Remetentes isentos da identificação de externo",
                            $"{N(cfg.AllowList.Count)}: " + Join(cfg.AllowList.Take(10)), "nenhum"),
                    },
                    new[] { KnightObjects.Setting(settingId, label, "habilitada, com isenções", expected) });
            }),
            Ref(M365 + "6.2.3")),

        // ==== Suplementos e contas pessoais ====

        Control("AK-EXO-011", "Usuários podem instalar suplementos do Outlook por conta própria",
            KnightIndicatorCategory.ApplicationGovernance, SeverityLevel.Low,
            "Remover das políticas de atribuição de função as funções que permitem instalar suplementos do Outlook, deixando a "
            + "distribuição de suplementos sob a administração da organização.",
            "Nenhuma política de atribuição de função com as funções de instalação de suplementos do Outlook.",
            c => ExchangePolicyReach.Evaluate<ExchangeRoleAssignmentPolicyConfiguration>(c,
                MailboxReach(c, ExchangePolicyReachInventory.RoleAssignmentPolicyType),
                "RoleAssignmentPolicy/AddInRoles", "Funções de instalação de suplementos",
                "nenhuma das três funções de suplemento",
                p =>
                {
                    var assigned = p.AddInRolesAssigned;
                    if (p.AssignedRoles.Count == 0) return ExchangePolicyReading.Unknown();
                    return assigned.Count == 0
                        ? ExchangePolicyReading.Compliant("nenhuma função de instalação de suplemento")
                        : ExchangePolicyReading.NonCompliant($"{N(assigned.Count)} função(ões): " + Join(assigned));
                },
                "As pessoas podem instalar suplementos do Outlook por conta própria. Um suplemento roda dentro do cliente de e-mail, "
                + "com acesso ao conteúdo das mensagens que a pessoa abre, e a instalação não passa por nenhuma avaliação da organização.",
                "Nenhuma política de atribuição de função permite que as pessoas instalem suplementos do Outlook por conta própria."),
            Ref(M365 + "6.3.1")),

        Control("AK-EXO-012", "Contas de e-mail e calendários pessoais permitidos no Outlook na web",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Medium,
            "Desabilitar, nas políticas do Outlook na web, a adição de contas de e-mail e de calendários pessoais.",
            "Todas as políticas do Outlook na web com contas pessoais e calendários pessoais desabilitados.",
            c => ExchangePolicyReach.Evaluate<ExchangeOwaMailboxPolicyConfiguration>(c, OwaReach(c),
                "OwaMailboxPolicy/PersonalAccounts", "Contas e calendários pessoais no Outlook na web",
                "ambos desabilitados",
                p =>
                {
                    if (p.PersonalAccountsEnabled is null && p.PersonalAccountCalendarsEnabled is null)
                        return ExchangePolicyReading.Unknown();

                    var ligados = new List<string>();
                    if (p.PersonalAccountsEnabled == true) ligados.Add("contas de e-mail pessoais");
                    if (p.PersonalAccountCalendarsEnabled == true) ligados.Add("calendários pessoais");

                    if (ligados.Count > 0) return ExchangePolicyReading.NonCompliant("permite " + Join(ligados));

                    // Um dos dois não informado, o outro desligado: não dá para aprovar sem supor o ausente.
                    if (p.PersonalAccountsEnabled is null || p.PersonalAccountCalendarsEnabled is null)
                        return ExchangePolicyReading.Unrecognized(
                            "contas pessoais: " + KnightObjects.YesNo(p.PersonalAccountsEnabled)
                            + "; calendários pessoais: " + KnightObjects.YesNo(p.PersonalAccountCalendarsEnabled));

                    return ExchangePolicyReading.Compliant("contas e calendários pessoais desabilitados");
                },
                "As pessoas podem ligar contas de e-mail e calendários pessoais ao Outlook na web, dentro do ambiente da organização. "
                + "Isso cria um caminho entre a correspondência da organização e uma conta que a organização não administra, sem passar "
                + "por nenhum controle de saída de dados.",
                "As políticas do Outlook na web não permitem adicionar contas de e-mail nem calendários pessoais."),
            Ref(M365 + "6.3.2", KnightReferenceMatch.Exact,
                "A referência verifica a política PADRÃO; o AEGIS verifica TODAS as instâncias, e a padrão está sempre entre elas.")),

        // ==== Autenticação e transporte ====

        Control("AK-EXO-013", "Autenticação moderna desabilitada no Exchange Online",
            KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.High,
            "Habilitar a autenticação moderna no Exchange Online e, em seguida, bloquear a autenticação legada por política de "
            + "acesso condicional ou por política de autenticação — são duas ações distintas.",
            "Configuração da organização com a autenticação moderna habilitada.",
            c => Single<ExchangeOrganizationConfiguration>(c, cfg => Flag(cfg.OAuth2ClientProfileEnabled, true,
                "exchangeOrganizationConfiguration/OAuth2ClientProfileEnabled", "Autenticação moderna no Exchange Online",
                "habilitada", "desabilitada",
                "A autenticação moderna está desabilitada no Exchange Online. Sem ela, os clientes de e-mail não usam o fluxo que "
                + "permite segundo fator e acesso condicional: a autenticação acontece pelo caminho legado, em que a senha basta.",
                // NÃO conclui que a autenticação legada foi eliminada. Habilitar a moderna não bloqueia a legada —
                // e afirmar o contrário daria por resolvido o problema que o cliente ainda tem.
                "A autenticação moderna está habilitada no Exchange Online. Isso NÃO significa que a autenticação legada esteja "
                + "eliminada: habilitar a moderna permite o fluxo novo, mas não bloqueia o antigo. O bloqueio é uma decisão à parte, "
                + "por acesso condicional ou por política de autenticação, e não é verificado por este controle.")),
            Ref(M365 + "6.5.1")),

        Control("AK-EXO-014", "Dicas de e-mail desabilitadas",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Low,
            "Habilitar as dicas de e-mail (todas, destinatários externos e métricas de grupo) e manter o limite de público amplo "
            + "em no máximo 25 destinatários.",
            "Configuração da organização com as três dicas habilitadas e limite de público amplo ≤ 25.",
            c => Single<ExchangeOrganizationConfiguration>(c, cfg =>
            {
                var desconhecidos = new List<string>();
                if (cfg.MailTipsAllTipsEnabled is null) desconhecidos.Add("dicas de e-mail");
                if (cfg.MailTipsExternalRecipientsTipsEnabled is null) desconhecidos.Add("dica de destinatário externo");
                if (cfg.MailTipsGroupMetricsEnabled is null) desconhecidos.Add("métricas de grupo");
                if (cfg.MailTipsLargeAudienceThreshold is null) desconhecidos.Add("limite de público amplo");
                if (desconhecidos.Count > 0)
                    return KnightControlOutcome.NotEvaluated(
                        "a fonte não informou " + Join(desconhecidos) + " nesta coleta, e aprovar exigiria supor esses valores.");

                var limite = cfg.MailTipsLargeAudienceThreshold!.Value;
                var problemas = new List<string>();
                if (cfg.MailTipsAllTipsEnabled != true) problemas.Add("as dicas de e-mail estão desligadas");
                if (cfg.MailTipsExternalRecipientsTipsEnabled != true) problemas.Add("a dica de destinatário externo está desligada");
                if (cfg.MailTipsGroupMetricsEnabled != true) problemas.Add("as métricas de grupo estão desligadas");
                if (limite > ExchangeOrganizationConfiguration.LargeAudienceThreshold)
                    problemas.Add($"o limite de público amplo é {N(limite)} (esperado no máximo {N(ExchangeOrganizationConfiguration.LargeAudienceThreshold)})");

                // Cada ajuste vira UM objeto, e o próprio ajuste sabe se está conforme: o objeto afetado é
                // escolhido por essa decisão, e não por inspecionar o texto já formatado de outro objeto.
                var ajustes = new (string Id, string Label, string Found, string Expected, bool Ok)[]
                {
                    ("exchangeOrganizationConfiguration/MailTipsAllTipsEnabled", "Dicas de e-mail",
                        KnightObjects.YesNo(cfg.MailTipsAllTipsEnabled), "sim", cfg.MailTipsAllTipsEnabled == true),
                    ("exchangeOrganizationConfiguration/MailTipsExternalRecipientsTipsEnabled", "Dica de destinatário externo",
                        KnightObjects.YesNo(cfg.MailTipsExternalRecipientsTipsEnabled), "sim", cfg.MailTipsExternalRecipientsTipsEnabled == true),
                    ("exchangeOrganizationConfiguration/MailTipsGroupMetricsEnabled", "Métricas de grupo",
                        KnightObjects.YesNo(cfg.MailTipsGroupMetricsEnabled), "sim", cfg.MailTipsGroupMetricsEnabled == true),
                    ("exchangeOrganizationConfiguration/MailTipsLargeAudienceThreshold", "Limite de público amplo",
                        N(limite), "no máximo " + N(ExchangeOrganizationConfiguration.LargeAudienceThreshold),
                        limite <= ExchangeOrganizationConfiguration.LargeAudienceThreshold),
                };

                var objetos = ajustes.Select(a => KnightObjects.Setting(a.Id, a.Label, a.Found, a.Expected)).ToList();

                if (problemas.Count == 0)
                    return KnightControlOutcome.Passed(
                        "As dicas de e-mail estão habilitadas, inclusive a de destinatário externo e as métricas de grupo, com o limite "
                        + "de público amplo dentro do esperado.", objetos);

                return KnightControlOutcome.Exposed(
                    "As dicas de e-mail não estão completas: " + Join(problemas)
                    + ". As dicas avisam quem está escrevendo — antes de enviar — que a mensagem vai para fora da organização ou para "
                    + "um número grande de pessoas. É o único aviso que aparece no momento em que o erro ainda pode ser evitado.",
                    ajustes.Where(a => !a.Ok)
                        .Select(a => KnightObjects.AffectedSetting(a.Id, a.Label, a.Found, a.Expected))
                        .ToList(),
                    objetos);
            }),
            Ref(M365 + "6.5.2")),

        Control("AK-EXO-015", "Provedores de armazenamento de terceiros disponíveis no Outlook na web",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.Low,
            "Desabilitar os provedores de armazenamento adicionais nas políticas do Outlook na web, mantendo os anexos no "
            + "armazenamento da organização.",
            "Todas as políticas do Outlook na web com provedores de armazenamento adicionais desabilitados.",
            c => ExchangePolicyReach.Evaluate<ExchangeOwaMailboxPolicyConfiguration>(c, OwaReach(c),
                "OwaMailboxPolicy/AdditionalStorageProvidersAvailable", "Provedores de armazenamento adicionais", "desabilitados",
                p => ExchangePolicyReading.Flag(p.AdditionalStorageProvidersAvailable, false),
                "O Outlook na web oferece serviços de armazenamento de terceiros para anexar arquivos. Um anexo enviado por esse "
                + "caminho sai do armazenamento da organização e deixa de estar sujeito à retenção, à classificação e à auditoria dela.",
                "Nenhuma política do Outlook na web oferece provedores de armazenamento de terceiros."),
            Ref(M365 + "6.5.3")),

        Control("AK-EXO-016", "SMTP AUTH habilitado no Exchange Online",
            KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.High,
            "Desabilitar o SMTP AUTH no nível da organização e remover as substituições por caixa de correio que o mantenham "
            + "habilitado. Onde um dispositivo ou aplicativo legado ainda depender dele, usar um conector autenticado por "
            + "certificado ou endereço, em vez de senha de conta.",
            "SMTP AUTH desabilitado na organização E nenhuma caixa de correio o habilitando por substituição.",
            c =>
            {
                var transport = c.Configuration.Read<ExchangeTransportConfiguration>();
                if (!transport.Collected) return KnightControlOutcome.NotEvaluated(transport.MissingReason ?? "a configuração de transporte não foi coletada.");
                if (transport.Single is not { } cfg) return KnightControlOutcome.NotEvaluated("a coleta concluiu sem devolver a configuração de transporte.");
                if (cfg.SmtpClientAuthenticationDisabled is null)
                    return KnightControlOutcome.NotEvaluated("a fonte não informou o SMTP AUTH da organização nesta coleta.");

                var orgObj = KnightObjects.Setting("exchangeTransportConfiguration/SmtpClientAuthenticationDisabled",
                    "SMTP AUTH na organização",
                    cfg.SmtpClientAuthenticationDisabled == true ? "desabilitado" : "habilitado", "desabilitado");

                // A leitura das substituições NÃO é opcional. Sem ela, "desabilitado na organização" é uma frase
                // sobre o padrão — e o padrão é justamente o que uma substituição por caixa ignora.
                var overrides = c.Configuration.Read<ExchangeSmtpAuthOverrideInventory>();
                if (!overrides.Collected || overrides.Single is null)
                    return KnightControlOutcome.NotEvaluated(
                        "o valor da organização foi lido, mas as SUBSTITUIÇÕES por caixa de correio não: "
                        + (overrides.MissingReason ?? "leitura não concluída")
                        + ". No Exchange, uma caixa pode habilitar o SMTP AUTH independentemente da organização — e a ausência do valor "
                        + "na caixa significa HERANÇA, não desabilitação. Concluir só pelo valor da organização descreveria o padrão, "
                        + "não o ambiente.",
                        new[] { orgObj });

                var inv = overrides.Single!;
                var afetadas = inv.ExplicitlyEnabled
                    .Select(m => KnightObjects.Affected(KnightAffectedObjectKind.User,
                        string.IsNullOrWhiteSpace(m.ExternalDirectoryObjectId) ? "caixa:" + (m.UserPrincipalName ?? "?") : m.ExternalDirectoryObjectId,
                        MailboxName(m.UserPrincipalName, m.DisplayName, m.ExternalDirectoryObjectId), m.UserPrincipalName,
                        "Esta caixa de correio HABILITA o SMTP AUTH explicitamente, ignorando o valor da organização. O SMTP AUTH "
                        + "autentica com usuário e senha e não passa por segundo fator: uma senha vazada basta para enviar mensagens "
                        + "em nome desta caixa.",
                        observed: "SMTP AUTH na caixa: habilitado (substitui a organização)"))
                    .ToList();

                var evidencias = new List<KnightIndicatorObject>
                {
                    orgObj,
                    KnightObjects.Setting("exchangeSmtpAuthOverrideInventory/herança", "Caixas que herdam o valor da organização",
                        N(inv.InheritingOrganizationCount) + " de " + N(inv.MailboxTotal),
                        cfg.SmtpClientAuthenticationDisabled == true ? "todas, com a organização desabilitando" : "—"),
                };
                if (inv.ExplicitlyDisabledCount > 0)
                    evidencias.Add(KnightObjects.Setting("exchangeSmtpAuthOverrideInventory/desabilitadas",
                        "Caixas que desabilitam explicitamente", N(inv.ExplicitlyDisabledCount), "—"));

                if (cfg.SmtpClientAuthenticationDisabled == false)
                {
                    var herdando = inv.InheritingOrganizationCount;
                    return KnightControlOutcome.Exposed(
                        "O SMTP AUTH está habilitado no nível da organização: ele vale para as "
                        + N(herdando) + " caixa(s) que herdam esse valor"
                        + (afetadas.Count > 0 ? $", além de {N(afetadas.Count)} caixa(s) que o habilitam explicitamente" : "")
                        + ". O SMTP AUTH autentica com usuário e senha, sem segundo fator — é o caminho por onde uma senha vazada vira "
                        + "envio de mensagens em nome da organização.",
                        afetadas.Count > 0
                            ? afetadas
                            : new[]
                            {
                                KnightObjects.AffectedSetting("exchangeTransportConfiguration/SmtpClientAuthenticationDisabled",
                                    "SMTP AUTH na organização", "habilitado", "desabilitado"),
                            },
                        evidencias,
                        complete: inv.ListComplete,
                        limitation: inv.ListComplete ? null
                            : "A enumeração das configurações de acesso de cliente atingiu o teto desta coleta: caixas fora do trecho lido não foram verificadas.");
                }

                return Population(
                    inv.ListComplete, afetadas,
                    $"O SMTP AUTH está desabilitado na organização, mas {N(afetadas.Count)} caixa(s) de correio o habilitam por "
                    + "substituição — e uma substituição por caixa ignora o valor da organização.",
                    $"O SMTP AUTH está desabilitado na organização e nenhuma das {N(inv.MailboxTotal)} caixa(s) lidas o habilita por "
                    + $"substituição ({N(inv.InheritingOrganizationCount)} herdam o valor da organização).",
                    "A enumeração das configurações de acesso de cliente atingiu o teto desta coleta: caixas fora do trecho lido não foram verificadas.",
                    evidencias);
            },
            Ref(M365 + "6.5.4", KnightReferenceMatch.Exact,
                "O critério é o da referência (SMTP AUTH desabilitado). O AEGIS lê também as SUBSTITUIÇÕES por caixa de correio, porque "
                + "elas ignoram o valor da organização — e trata a ausência do valor na caixa como HERANÇA, nunca como desabilitação.")),

        Control("AK-EXO-017", "Envios diretos não são rejeitados",
            KnightIndicatorCategory.CollaborationSecurity, SeverityLevel.High,
            "Habilitar a rejeição de envios diretos na organização, de modo que mensagens entregues ao ponto de extremidade da "
            + "organização sem autenticação sejam recusadas. Onde um dispositivo ou aplicativo legado ainda dependa desse caminho, "
            + "migrar para um conector autenticado antes de habilitar.",
            "Configuração da organização com a rejeição de envios diretos habilitada.",
            c => Single<ExchangeOrganizationConfiguration>(c, cfg =>
            {
                // Propriedade recente do serviço. Ausente não é "desligada": é locatário ou versão de módulo que
                // ainda não a expõe — e o critério fica sem veredito, com o motivo.
                if (cfg.RejectDirectSend is null)
                    return KnightControlOutcome.NotEvaluated(
                        "a fonte não devolveu a configuração de rejeição de envios diretos nesta coleta. Ela é recente no serviço: "
                        + "a ausência indica locatário ou versão do módulo que ainda não a expõe, e não uma configuração desligada.");

                return Flag(cfg.RejectDirectSend, true,
                    "exchangeOrganizationConfiguration/RejectDirectSend", "Rejeição de envios diretos",
                    "habilitada", "desabilitada",
                    "A organização aceita mensagens entregues diretamente ao seu ponto de extremidade sem autenticação. Esse caminho "
                    + "existe para dispositivos e aplicativos internos, e qualquer remetente pode usá-lo declarando um endereço da "
                    + "organização: a mensagem chega às caixas internas com aparência de interna.",
                    "A organização rejeita envios diretos: mensagens entregues ao ponto de extremidade sem autenticação são recusadas.");
            }),
            Ref(M365 + "6.5.5")),
    };
}
