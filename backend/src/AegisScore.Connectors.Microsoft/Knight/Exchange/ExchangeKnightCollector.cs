using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Domain;

namespace AegisScore.Connectors.Microsoft.Knight.Exchange;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-03] Coletor REAL do Exchange Online (somente leitura) para o AEGIS KNIGHT.
///
/// <para><b>MÉTODO DE COLETA — a decisão e o porquê, para não ser redescoberta.</b></para>
/// <list type="number">
///   <item><b>Autenticação.</b> <c>Connect-ExchangeOnline</c> aceita o parâmetro <c>-AccessToken</c> (documentado
///   a partir da versão 3.1.0-Preview1 do módulo) e, para token de APLICATIVO, a documentação manda usá-lo junto
///   de <c>-Organization</c>. É esse o caminho aqui: nenhum certificado é emitido, distribuído ou rotacionado
///   pelo cliente. O certificado da documentação é uma forma de OBTER o token — não um requisito do serviço.</item>
///
///   <item><b>Permissão de API ≠ autorização.</b> São coisas diferentes e é preciso pedir as duas:
///   <c>Exchange.ManageAsApp</c> (permissão de aplicativo da API <i>Office 365 Exchange Online</i>, com
///   consentimento do administrador) autoriza a APLICAÇÃO a falar com o Exchange; ela NÃO decide o que a sessão
///   pode ler. Quem decide é o PAPEL DE DIRETÓRIO atribuído à aplicação, que a documentação descreve como
///   devolvido dentro do token e usado para montar o controle de acesso da sessão. Sem papel, a conexão é
///   recusada — e a recusa é por AUTORIZAÇÃO, não por autenticação.</item>
///
///   <item><b>Qual papel.</b> A lista oficial de papéis suportados para o Exchange Online PowerShell é fechada;
///   dela, o menor que é SOMENTE LEITURA e cobre as leituras deste coletor é <b>Leitor Global</b>. Os papéis de
///   administração (Exchange, Exchange Recipient, Helpdesk, Global) concedem ESCRITA e não são pedidos aqui.
///   <b>O papel que serve ao Teams não serve ao Exchange</b>: são listas de papéis distintas, e Leitor do Teams
///   não está na do Exchange. Se ainda assim uma leitura específica for recusada por controle de acesso, ela é
///   declarada com o COMANDO nomeado e os controles que dependem dela ficam não avaliados — nunca aprovados por
///   ausência.</item>
///
///   <item><b>Abrangência dos recursos visíveis.</b> Distinta das três anteriores. Mesmo com autenticação,
///   permissão e papel corretos, uma enumeração pode não cobrir a organização: há teto de leitura. Por isso cada
///   enumeração devolve se foi TRUNCADA, e o inventário carrega essa informação até a avaliação.</item>
/// </list>
///
/// <para><b>O que este coletor NÃO faz:</b> não cria, altera nem remove nada no locatário (todos os comandos são
/// de verbo Get); não executa comando vindo do cliente; não produz fato de identidade — a avaliação do Entra ID
/// e a do Teams continuam sendo de outras fontes e não são tocadas por uma sincronização do Exchange.</para>
/// </summary>
public sealed class ExchangeKnightCollector : IKnightCollector
{
    private const string Label = "Exchange Online";

    private readonly IExchangeTokenClient _tokens;
    private readonly IExchangeAdminReader _reader;
    private readonly ILogger<ExchangeKnightCollector>? _log;
    private readonly TimeProvider _time;

    public ExchangeKnightCollector(
        IExchangeTokenClient tokens,
        IExchangeAdminReader reader,
        ILogger<ExchangeKnightCollector>? log = null,
        TimeProvider? time = null)
    {
        _tokens = tokens;
        _reader = reader;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public KnightSourceType Source => KnightSourceType.MicrosoftExchangeOnline;

    /// <summary>Capacidades deste coletor, na ordem em que o adaptador as executa.</summary>
    internal static IReadOnlyList<KnightCapability> Capabilities { get; } = new[]
    {
        KnightCapability.ExchangeOrganizationConfig,
        KnightCapability.ExchangeTransportConfig,
        KnightCapability.ExchangeSharingPolicies,
        KnightCapability.ExchangeOwaMailboxPolicies,
        KnightCapability.ExchangeTransportRules,
        KnightCapability.ExchangeRoleAssignmentPolicies,
        KnightCapability.ExchangeExternalSenderIdentification,
        KnightCapability.ExchangeOutboundSpamFilterPolicies,
        KnightCapability.ExchangeMailboxes,
        KnightCapability.ExchangeMailboxSignIn,
        KnightCapability.ExchangeCasMailboxes,
        KnightCapability.ExchangeAuditBypassAssociations,
    };

    public async Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Configuration is not KnightExchangeOnlineConfiguration cfg)
            return KnightCollectionResult.NotConfigured(Source, Label);

        ExchangeAdminCredentials credentials;
        try
        {
            credentials = await _tokens.AcquireAsync(cfg, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EntraGraphException ex)
        {
            var tokenState = ex.Kind switch
            {
                EntraGraphErrorKind.AuthFailure => KnightSourceState.AuthenticationFailure,
                EntraGraphErrorKind.InsufficientPermission => KnightSourceState.InsufficientPermission,
                EntraGraphErrorKind.Throttled => KnightSourceState.Throttled,
                _ => KnightSourceState.Unavailable,
            };
            _log?.LogWarning(
                "Falha ao preparar a conexão de aplicativo com o Exchange Online: Kind={Kind}, HttpStatus={HttpStatus}, Endpoint={Endpoint}.",
                ex.Kind, ex.HttpStatusCode, ex.EndpointPath ?? "n/a");

            var detail = ex.EndpointPath is "/organization"
                ? "O domínio principal do locatário não pôde ser lido, e a conexão de aplicativo com o Exchange Online exige esse "
                  + "valor. Confira a permissão Organization.Read.All da aplicação. Nenhuma leitura foi tentada."
                : "Falha ao obter o token de aplicativo do recurso do Exchange Online. Nenhuma leitura foi tentada.";
            return Failure(tokenState, detail, OutcomeFor(tokenState), detail);
        }

        ExchangeAdminOutput output;
        try
        {
            output = await _reader.ReadAsync(credentials, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ExchangeAdminTransportException ex)
        {
            // A mensagem da exceção carrega o diagnóstico SANITIZADO do processo — útil para o operador, e por
            // isso vai ao log. O que chega ao ADM e ao relatório é a mensagem CONTROLADA abaixo.
            _log?.LogWarning(ex, "O adaptador de coleta do Exchange Online não pôde ser executado.");
            const string reason =
                "O adaptador de coleta do Exchange Online não pôde ser executado neste ambiente: o runtime do "
                + "PowerShell ou o módulo oficial não respondeu como esperado. Nenhuma leitura foi tentada — e "
                + "nenhum controle de Exchange Online foi avaliado a partir de coleta vazia.";
            return Failure(KnightSourceState.Unavailable, reason, KnightCapabilityOutcome.Unavailable, reason);
        }

        if (!output.Connected)
        {
            var outcome = ParseOutcome(output.ConnectionErrorCategory) ?? KnightCapabilityOutcome.AuthenticationFailure;
            var reason = ConnectionReason(outcome);
            _log?.LogWarning(
                "Conexão do adaptador do Exchange Online recusada: Categoria={Categoria}, Diagnostico={Diagnostico}.",
                output.ConnectionErrorCategory ?? "n/a", output.ConnectionErrorId ?? "n/a");
            return Failure(StateFor(outcome), reason, outcome, reason, output.Runtime);
        }

        var now = _time.GetUtcNow();
        var caps = new List<KnightCapabilityStatus>();
        var byCapability = output.Reads
            .GroupBy(r => r.Capability, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var capability in Capabilities)
        {
            if (!byCapability.TryGetValue(capability.ToString(), out var read))
            {
                caps.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.NotAttempted,
                    "O adaptador não registrou esta leitura nesta execução."));
                continue;
            }

            if (!read.Ok)
            {
                var outcome = ParseOutcome(read.ErrorCategory) ?? KnightCapabilityOutcome.Error;
                caps.Add(new KnightCapabilityStatus(capability, outcome, Describe(outcome, read)));
                continue;
            }

            caps.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.Collected,
                read.Truncated
                    ? $"Leitura concluída, mas a enumeração atingiu o teto desta coleta (comando: {read.Command}). "
                      + "A lista não cobre a organização inteira."
                    : null));
        }

        List<KnightConfigurationDocument> docs;
        try
        {
            docs = Translate(byCapability, caps).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Resposta com forma inesperada NUNCA vira coleção vazia. Ela derruba as capacidades para "erro",
            // com motivo, e os controles que dependem delas ficam não avaliados.
            _log?.LogWarning(ex, "Resposta do Exchange Online ilegível para o contrato desta versão.");
            const string reason = "A resposta desta leitura não pôde ser interpretada pelo contrato desta versão.";
            return Failure(KnightSourceState.Error, reason, KnightCapabilityOutcome.Error, reason, output.Runtime);
        }

        var state = DeriveState(caps);
        return new KnightCollectionResult(
            Source, state, Label, KnightFactSet.Empty, caps, now, DescribeState(state, output),
            TenantConfiguration: new KnightTenantConfiguration(docs, caps));
    }

    // ---- Tradução: resposta do comando → documento tipado do ADM --------------------------------------

    /// <summary>
    /// A tradução do Exchange olha para MAIS DE UMA leitura ao mesmo tempo, e isso é deliberado: o inventário de
    /// caixas compartilhadas precisa do estado de entrada das contas, e o alcance das políticas precisa da
    /// declaração que cada caixa faz. A alternativa — traduzir leitura a leitura — obrigaria a avaliação a
    /// refazer o vínculo, e a refazê-lo em cada controle.
    ///
    /// O que a tradução NÃO faz: preencher o que uma leitura ausente deixou de fora. Quando a leitura de contas
    /// falha, o estado de entrada fica NULO e o inventário diz por quê — em vez de deduzir do tipo da caixa.
    /// </summary>
    private static IEnumerable<KnightConfigurationDocument> Translate(
        IReadOnlyDictionary<string, ExchangeAdminRead> reads, IReadOnlyList<KnightCapabilityStatus> caps)
    {
        bool Collected(KnightCapability c) =>
            caps.Any(x => x.Capability == c && x.Outcome == KnightCapabilityOutcome.Collected);

        JsonElement? Items(KnightCapability c) =>
            Collected(c) && reads.TryGetValue(c.ToString(), out var r) && r.Items.ValueKind == JsonValueKind.Array
                ? r.Items
                : null;

        bool Truncated(KnightCapability c) =>
            reads.TryGetValue(c.ToString(), out var r) && r.Truncated;

        // ---- Configurações únicas ---------------------------------------------------------------------

        if (Items(KnightCapability.ExchangeOrganizationConfig) is { } orgItems)
        {
            foreach (var e in orgItems.EnumerateArray())
                yield return KnightTenantConfiguration.Document(
                    ExchangeOrganizationConfiguration.ExternalId, "Configuração da organização do Exchange Online",
                    new ExchangeOrganizationConfiguration(
                        Bool(e, "auditDisabled"), Bool(e, "customerLockBoxEnabled"), Bool(e, "oAuth2ClientProfileEnabled"),
                        Bool(e, "mailTipsAllTipsEnabled"), Bool(e, "mailTipsExternalRecipientsTipsEnabled"),
                        Bool(e, "mailTipsGroupMetricsEnabled"), Int(e, "mailTipsLargeAudienceThreshold"),
                        Bool(e, "bookingsEnabled"), Bool(e, "rejectDirectSend")));
        }

        if (Items(KnightCapability.ExchangeTransportConfig) is { } transportItems)
        {
            foreach (var e in transportItems.EnumerateArray())
                yield return KnightTenantConfiguration.Document(
                    ExchangeTransportConfiguration.ExternalId, "Configuração de transporte da organização",
                    new ExchangeTransportConfiguration(Bool(e, "smtpClientAuthenticationDisabled")));
        }

        if (Items(KnightCapability.ExchangeExternalSenderIdentification) is { } externalItems)
        {
            foreach (var e in externalItems.EnumerateArray())
                yield return KnightTenantConfiguration.Document(
                    ExchangeExternalSenderIdentification.ExternalId, "Identificação de remetentes externos no Outlook",
                    new ExchangeExternalSenderIdentification(
                        Text(e, "identity"), Bool(e, "enabled"), Strings(e, "allowList")));
        }

        // ---- Políticas --------------------------------------------------------------------------------

        if (Items(KnightCapability.ExchangeSharingPolicies) is { } sharingItems)
        {
            var index = 0;
            foreach (var e in sharingItems.EnumerateArray())
            {
                var policy = new ExchangeSharingPolicyConfiguration(
                    Text(e, "identity"), Text(e, "name"), Bool(e, "isDefault"), Bool(e, "enabled"), Strings(e, "domains"));
                yield return KnightTenantConfiguration.Document(
                    PolicyDocumentId("SharingPolicy", policy, index++),
                    "Política de compartilhamento — " + DisplayOf(policy), policy);
            }
        }

        if (Items(KnightCapability.ExchangeOwaMailboxPolicies) is { } owaItems)
        {
            var index = 0;
            foreach (var e in owaItems.EnumerateArray())
            {
                var policy = new ExchangeOwaMailboxPolicyConfiguration(
                    Text(e, "identity"), Text(e, "name"), Bool(e, "isDefault"),
                    Bool(e, "additionalStorageProvidersAvailable"), Bool(e, "personalAccountsEnabled"),
                    Bool(e, "personalAccountCalendarsEnabled"), Bool(e, "bookingsMailboxCreationEnabled"));
                yield return KnightTenantConfiguration.Document(
                    PolicyDocumentId("OwaMailboxPolicy", policy, index++),
                    "Política do Outlook na web — " + DisplayOf(policy), policy);
            }
        }

        if (Items(KnightCapability.ExchangeRoleAssignmentPolicies) is { } rapItems)
        {
            var index = 0;
            foreach (var e in rapItems.EnumerateArray())
            {
                var policy = new ExchangeRoleAssignmentPolicyConfiguration(
                    Text(e, "identity"), Text(e, "name"), Bool(e, "isDefault"), Strings(e, "assignedRoles"));
                yield return KnightTenantConfiguration.Document(
                    PolicyDocumentId("RoleAssignmentPolicy", policy, index++),
                    "Política de atribuição de função — " + DisplayOf(policy), policy);
            }
        }

        if (Items(KnightCapability.ExchangeOutboundSpamFilterPolicies) is { } spamItems)
        {
            var index = 0;
            foreach (var e in spamItems.EnumerateArray())
            {
                var policy = new ExchangeOutboundSpamPolicyConfiguration(
                    Text(e, "identity"), Text(e, "name"), Bool(e, "isDefault"), Text(e, "autoForwardingMode"));
                yield return KnightTenantConfiguration.Document(
                    PolicyDocumentId("OutboundSpamFilterPolicy", policy, index++),
                    "Política de filtro de spam de saída — " + DisplayOf(policy), policy);
            }
        }

        if (Items(KnightCapability.ExchangeTransportRules) is { } ruleItems)
        {
            var seq = 0;
            foreach (var e in ruleItems.EnumerateArray())
            {
                var rule = new ExchangeTransportRuleConfiguration(
                    Text(e, "identity"), Text(e, "name"), Text(e, "state"), Text(e, "mode"),
                    Int(e, "priority"), Int(e, "setScl"),
                    Strings(e, "senderDomainIs"), Strings(e, "fromAddressContainsWords"),
                    Strings(e, "fromAddressMatchesPatterns"), Strings(e, "redirectMessageTo"),
                    Strings(e, "blindCopyTo"), Strings(e, "addToRecipients"), Strings(e, "copyTo"));
                // A POSIÇÃO entra no identificador: duas regras sem nome continuam sendo duas regras.
                yield return KnightTenantConfiguration.Document(
                    "TransportRule:" + (rule.Identity ?? rule.Name ?? "#" + seq) + ":" + seq++,
                    "Regra de transporte — " + (rule.Name ?? rule.Identity ?? "sem nome na coleta"), rule);
            }
        }

        // ---- Inventários da enumeração de caixas de correio -------------------------------------------

        if (Items(KnightCapability.ExchangeMailboxes) is { } mailboxItems)
        {
            var mailboxes = mailboxItems.EnumerateArray().ToList();
            var complete = !Truncated(KnightCapability.ExchangeMailboxes);

            // Estado de entrada por identificador ESTÁVEL do objeto de diretório. Sem a leitura de contas o mapa
            // fica vazio — e é o sinalizador de resolução, não o mapa vazio, que a avaliação consulta.
            var signInCollected = Collected(KnightCapability.ExchangeMailboxSignIn);
            var signInTruncated = Truncated(KnightCapability.ExchangeMailboxSignIn);
            var signIn = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            if (Items(KnightCapability.ExchangeMailboxSignIn) is { } accountItems)
            {
                foreach (var a in accountItems.EnumerateArray())
                {
                    var id = Text(a, "externalDirectoryObjectId");
                    if (id is null) continue;
                    signIn[id] = Bool(a, "accountDisabled");
                }
            }

            var shared = mailboxes
                .Where(m => string.Equals(Text(m, "recipientTypeDetails"), "SharedMailbox", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var sharedRecords = shared
                .Select(m =>
                {
                    var id = Text(m, "externalDirectoryObjectId");
                    return new ExchangeSharedMailboxRecord(
                        id ?? "",
                        Text(m, "userPrincipalName"),
                        Text(m, "displayName"),
                        // Sem identificador, ou sem a conta na leitura, o estado é DESCONHECIDO — nunca "bloqueada".
                        id is not null && signIn.TryGetValue(id, out var disabled) ? disabled : null);
                })
                .Take(ExchangeSharedMailboxInventory.MaxListed)
                .ToList();

            yield return KnightTenantConfiguration.Document(
                ExchangeSharedMailboxInventory.ExternalId, "Caixas de correio compartilhadas",
                new ExchangeSharedMailboxInventory(
                    shared.Count, sharedRecords,
                    complete && shared.Count <= ExchangeSharedMailboxInventory.MaxListed,
                    signInCollected,
                    !signInCollected
                        ? "A leitura das contas não foi concluída nesta coleta: o estado de entrada das caixas compartilhadas não pôde ser verificado."
                        : signInTruncated
                            ? "A leitura das contas atingiu o teto da enumeração: contas fora do trecho lido aparecem sem estado de entrada."
                            : null));

            // Auditoria por tipo de acesso. O universo é o das caixas que a referência avalia: as de usuário e as
            // compartilhadas — as demais (salas, equipamentos, grupos) não têm dono que acesse a própria caixa.
            var auditable = mailboxes
                .Where(m => Text(m, "recipientTypeDetails") is { } t
                    && (t.Equals("UserMailbox", StringComparison.OrdinalIgnoreCase)
                        || t.Equals("SharedMailbox", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var auditGaps = auditable
                .Select(m => new ExchangeMailboxAuditRecord(
                    Text(m, "externalDirectoryObjectId") ?? "",
                    Text(m, "userPrincipalName"), Text(m, "displayName"), Bool(m, "auditEnabled"),
                    Missing(ExchangeMailboxAuditInventory.RequiredOwnerActions, Strings(m, "auditOwner")),
                    Missing(ExchangeMailboxAuditInventory.RequiredDelegateActions, Strings(m, "auditDelegate")),
                    Missing(ExchangeMailboxAuditInventory.RequiredAdminActions, Strings(m, "auditAdmin"))))
                .Where(r => r.HasGap || r.AuditEnabled == false)
                .ToList();

            yield return KnightTenantConfiguration.Document(
                ExchangeMailboxAuditInventory.ExternalId, "Ações de auditoria por caixa de correio",
                new ExchangeMailboxAuditInventory(
                    auditable.Count,
                    auditGaps.Take(ExchangeMailboxAuditInventory.MaxListed).ToList(),
                    complete && auditGaps.Count <= ExchangeMailboxAuditInventory.MaxListed));

            var forwarding = mailboxes
                .Select(m => new ExchangeMailboxForwardingRecord(
                    Text(m, "externalDirectoryObjectId") ?? "",
                    Text(m, "userPrincipalName"), Text(m, "displayName"),
                    Text(m, "forwardingSmtpAddress"), Text(m, "forwardingAddress"),
                    Bool(m, "deliverToMailboxAndForward")))
                .Where(r => r.ForwardingSmtpAddress is not null || r.ForwardingAddress is not null)
                .ToList();

            yield return KnightTenantConfiguration.Document(
                ExchangeMailboxForwardingInventory.ExternalId, "Encaminhamento configurado nas caixas de correio",
                new ExchangeMailboxForwardingInventory(
                    mailboxes.Count,
                    forwarding.Take(ExchangeMailboxForwardingInventory.MaxListed).ToList(),
                    complete && forwarding.Count <= ExchangeMailboxForwardingInventory.MaxListed));

            // Alcance: cada caixa declara qual política de cada tipo a governa. Nenhuma consulta nova.
            var reachEntries = new List<ExchangePolicyReachEntry>();
            reachEntries.AddRange(CountByPolicy(mailboxes, "roleAssignmentPolicy", ExchangePolicyReachInventory.RoleAssignmentPolicyType));
            reachEntries.AddRange(CountByPolicy(mailboxes, "sharingPolicy", ExchangePolicyReachInventory.SharingPolicyType));
            var undeclared = mailboxes.Count(m => Text(m, "roleAssignmentPolicy") is null)
                + mailboxes.Count(m => Text(m, "sharingPolicy") is null);

            yield return KnightTenantConfiguration.Document(
                ExchangePolicyReachInventory.ExternalId, "Alcance das políticas declaradas pelas caixas de correio",
                new ExchangePolicyReachInventory(mailboxes.Count, reachEntries, undeclared, complete));
        }

        // ---- Inventários da enumeração de configurações de acesso de cliente --------------------------

        if (Items(KnightCapability.ExchangeCasMailboxes) is { } casItems)
        {
            var cas = casItems.EnumerateArray().ToList();
            var complete = !Truncated(KnightCapability.ExchangeCasMailboxes);

            // Três estados, e só um deles é substituição que habilita: true = desabilita na caixa, false =
            // HABILITA na caixa (ignorando a organização), ausente = HERDA a organização.
            var enabled = cas
                .Where(m => Bool(m, "smtpClientAuthenticationDisabled") == false)
                .Select(m => new ExchangeSmtpAuthOverrideRecord(
                    Text(m, "externalDirectoryObjectId") ?? "",
                    Text(m, "userPrincipalName"), Text(m, "displayName"), false))
                .ToList();
            var explicitlyDisabled = cas.Count(m => Bool(m, "smtpClientAuthenticationDisabled") == true);
            var inheriting = cas.Count(m => Bool(m, "smtpClientAuthenticationDisabled") is null);

            yield return KnightTenantConfiguration.Document(
                ExchangeSmtpAuthOverrideInventory.ExternalId, "Substituições de SMTP AUTH por caixa de correio",
                new ExchangeSmtpAuthOverrideInventory(
                    cas.Count, inheriting, explicitlyDisabled,
                    enabled.Take(ExchangeSmtpAuthOverrideInventory.MaxListed).ToList(),
                    complete && enabled.Count <= ExchangeSmtpAuthOverrideInventory.MaxListed));

            yield return KnightTenantConfiguration.Document(
                ExchangeOwaPolicyReachInventory.ExternalId, "Alcance das políticas do Outlook na web",
                new ExchangeOwaPolicyReachInventory(
                    cas.Count,
                    CountByPolicy(cas, "owaMailboxPolicy", ExchangeOwaPolicyReachInventory.OwaMailboxPolicyType).ToList(),
                    cas.Count(m => Text(m, "owaMailboxPolicy") is null),
                    complete));
        }

        // ---- Desvio de auditoria ----------------------------------------------------------------------

        if (Items(KnightCapability.ExchangeAuditBypassAssociations) is { } bypassItems)
        {
            var all = bypassItems.EnumerateArray().ToList();
            var bypass = all
                .Select(e => new ExchangeAuditBypassRecord(
                    Text(e, "identity") ?? "", Text(e, "name"), Bool(e, "auditBypassEnabled")))
                .Where(r => r.AuditBypassEnabled == true)
                .ToList();

            yield return KnightTenantConfiguration.Document(
                ExchangeAuditBypassInventory.ExternalId, "Associações de desvio de auditoria",
                new ExchangeAuditBypassInventory(
                    all.Count,
                    bypass.Take(ExchangeAuditBypassInventory.MaxListed).ToList(),
                    !Truncated(KnightCapability.ExchangeAuditBypassAssociations)
                        && bypass.Count <= ExchangeAuditBypassInventory.MaxListed));
        }
    }

    /// <summary>Quantas caixas declaram cada política de um tipo. Caixas sem declaração não entram em nenhuma.</summary>
    private static IEnumerable<ExchangePolicyReachEntry> CountByPolicy(
        IReadOnlyList<JsonElement> mailboxes, string property, string policyType) =>
        mailboxes
            .Select(m => Text(m, property))
            .Where(p => p is not null)
            .GroupBy(p => p!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ExchangePolicyReachEntry(policyType, g.Key, g.Count()))
            .OrderByDescending(e => e.MailboxCount)
            .ThenBy(e => e.PolicyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Ações exigidas que NÃO estão configuradas. A comparação ignora caixa, como a fonte as devolve.</summary>
    private static IReadOnlyList<string> Missing(IReadOnlyList<string> required, IReadOnlyList<string> configured) =>
        required.Where(r => !configured.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();

    private static string DisplayOf(IExchangePolicyDocument policy) =>
        policy.Name ?? policy.Identity ?? "sem identificação na coleta";

    /// <summary>
    /// Identificador do documento no ADM. Com identidade ou nome, é o tipo mais essa chave — estável entre
    /// coletas. SEM nenhum dos dois, entra a POSIÇÃO na resposta: dois registros não identificados continuam
    /// sendo dois documentos, em vez de um sobrescrever o outro.
    /// </summary>
    private static string PolicyDocumentId(string policyType, IExchangePolicyDocument policy, int index) =>
        policyType + ":" + (ExchangePolicyIdentities.Key(policy) ?? "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && v.GetString() is { Length: > 0 } s
            ? s
            : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;

    private static IReadOnlyList<string> Strings(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    // ---- Estado ----------------------------------------------------------------------------------------

    /// <summary>
    /// Falha ANTES de qualquer leitura: todas as capacidades recebem o mesmo desfecho, com o motivo. É isso que
    /// impede uma coleta frustrada de virar "nada encontrado" — os controles ficam não avaliados.
    /// </summary>
    private KnightCollectionResult Failure(
        KnightSourceState state, string detail, KnightCapabilityOutcome outcome, string reason,
        ExchangeAdminRuntime? runtime = null)
    {
        var caps = Capabilities.Select(c => new KnightCapabilityStatus(c, outcome, reason)).ToList();
        return new KnightCollectionResult(
            Source, state, Label, KnightFactSet.Empty, caps, _time.GetUtcNow(),
            detail + (runtime?.Module is { Length: > 0 } m ? $" (módulo {m})" : ""),
            TenantConfiguration: new KnightTenantConfiguration(Array.Empty<KnightConfigurationDocument>(), caps));
    }

    private static KnightCapabilityOutcome? ParseOutcome(string? category) =>
        Enum.TryParse<KnightCapabilityOutcome>(category, ignoreCase: true, out var parsed) ? parsed : null;

    private static KnightCapabilityOutcome OutcomeFor(KnightSourceState state) => state switch
    {
        KnightSourceState.AuthenticationFailure => KnightCapabilityOutcome.AuthenticationFailure,
        KnightSourceState.Throttled => KnightCapabilityOutcome.Throttled,
        KnightSourceState.InsufficientPermission => KnightCapabilityOutcome.InsufficientPermission,
        _ => KnightCapabilityOutcome.Unavailable,
    };

    private static KnightSourceState StateFor(KnightCapabilityOutcome outcome) => outcome switch
    {
        KnightCapabilityOutcome.AuthenticationFailure => KnightSourceState.AuthenticationFailure,
        KnightCapabilityOutcome.InsufficientPermission => KnightSourceState.InsufficientPermission,
        KnightCapabilityOutcome.Throttled => KnightSourceState.Throttled,
        KnightCapabilityOutcome.Error => KnightSourceState.Error,
        _ => KnightSourceState.Unavailable,
    };

    /// <summary>
    /// Mensagem da falha de CONEXÃO, montada pela CATEGORIA. Nunca reproduz texto da fonte.
    ///
    /// <para><b>O que estas mensagens NÃO fazem: atribuir causa.</b> Uma recusa de autorização é compatível com
    /// várias causas — consentimento ausente, papel de diretório ausente, papel presente mas sem alcance, domínio
    /// de organização errado, ou o próprio método de autenticação não ser aceito. O sintoma não distingue entre
    /// elas. Por isso o texto informa o que o serviço RECUSOU e enumera as verificações a fazer, em vez de
    /// declarar o que falta: apontar uma causa específica a partir de uma falha ambígua mandaria o operador
    /// consertar o que talvez já esteja certo, e esconderia a causa verdadeira.</para>
    /// </summary>
    private static string ConnectionReason(KnightCapabilityOutcome outcome) => outcome switch
    {
        KnightCapabilityOutcome.InsufficientPermission =>
            "A conexão de aplicativo com o Exchange Online foi recusada por autorização. A recusa não identifica a causa; "
            + "verifique, nesta ordem: (1) a permissão de aplicativo Exchange.ManageAsApp (API Office 365 Exchange Online) "
            + "está consentida pelo administrador; (2) a aplicação tem um papel de diretório atribuído — para leitura, o "
            + "papel Leitor Global cobre as leituras deste coletor; (3) o papel usado pelo Microsoft Teams não vale aqui, e "
            + "a permissão sozinha não autoriza comando algum; (4) o domínio de organização enviado é o do locatário; e "
            + "(5) o método de autenticação: esta conexão usa token obtido por segredo de cliente, e o AEGIS não tem "
            + "confirmação documental de que o serviço o aceite — se (1) a (4) estiverem corretos, é este item que resta.",
        KnightCapabilityOutcome.AuthenticationFailure =>
            "A conexão de aplicativo com o Exchange Online falhou na autenticação. A falha não identifica a causa; verifique "
            + "o segredo da aplicação, se o token foi emitido para o recurso do Exchange Online (o do Microsoft Graph não é "
            + "aceito nesta conexão) e se o serviço aceita token obtido por segredo de cliente — o AEGIS não tem confirmação "
            + "documental desse último ponto.",
        KnightCapabilityOutcome.Throttled =>
            "O Exchange Online aplicou limite de taxa ao estabelecer a conexão. Nenhuma leitura foi tentada; a coleta pode ser "
            + "repetida mais tarde.",
        KnightCapabilityOutcome.LimitedByLicense =>
            "A conexão de aplicativo com o Exchange Online foi recusada por licença do locatário.",
        KnightCapabilityOutcome.Unavailable =>
            "O Exchange Online não respondeu ao estabelecer a conexão. Nenhuma leitura foi tentada.",
        _ =>
            "A conexão de aplicativo com o Exchange Online não foi estabelecida. Nenhuma leitura foi tentada.",
    };

    private static string Describe(KnightCapabilityOutcome outcome, ExchangeAdminRead read)
    {
        var head = outcome switch
        {
            KnightCapabilityOutcome.InsufficientPermission =>
                "Autorização recusada nesta leitura. A conexão foi estabelecida, então o que falta é alcance sobre ESTE comando; "
                + "verifique o papel de diretório atribuído à aplicação — para leitura ampla, o papel Leitor Global cobre as "
                + "leituras deste coletor. A recusa não diz qual papel está atribuído, e o AEGIS não deduz isso dela.",
            KnightCapabilityOutcome.AuthenticationFailure => "Falha de autenticação da aplicação nesta leitura.",
            KnightCapabilityOutcome.Throttled => "Limite de taxa do Exchange Online nesta leitura.",
            KnightCapabilityOutcome.LimitedByLicense => "O locatário não tem a licença que este recurso exige.",
            KnightCapabilityOutcome.Unavailable =>
                "A leitura não pôde ser concluída: o comando não foi importado nesta sessão, ou o serviço não respondeu.",
            _ => "Erro inesperado nesta leitura.",
        };
        return $"{head} (comando: {read.Command})";
    }

    /// <summary>
    /// Estado da coleta a partir do desfecho de TODAS as capacidades esperadas — inclusive as que o adaptador
    /// nem registrou. Uma leitura esperada que não aparece na resposta não é leitura concluída sem registros:
    /// é leitura AUSENTE, e impede o estado "concluída".
    /// </summary>
    private static KnightSourceState DeriveState(IReadOnlyList<KnightCapabilityStatus> all)
    {
        if (all.Count == 0) return KnightSourceState.Unavailable;

        var collected = all.Count(c => c.Outcome == KnightCapabilityOutcome.Collected);
        if (collected == all.Count) return KnightSourceState.Completed;
        if (collected > 0) return KnightSourceState.PartialCollection;

        var failed = all.Where(c => c.Outcome != KnightCapabilityOutcome.NotAttempted).ToList();
        if (failed.Count == all.Count)
        {
            if (failed.All(c => c.Outcome == KnightCapabilityOutcome.InsufficientPermission)) return KnightSourceState.InsufficientPermission;
            if (failed.All(c => c.Outcome == KnightCapabilityOutcome.Throttled)) return KnightSourceState.Throttled;
            if (failed.All(c => c.Outcome == KnightCapabilityOutcome.AuthenticationFailure)) return KnightSourceState.AuthenticationFailure;
            if (failed.All(c => c.Outcome == KnightCapabilityOutcome.Error)) return KnightSourceState.Error;
        }

        return KnightSourceState.Unavailable;
    }

    private static string DescribeState(KnightSourceState state, ExchangeAdminOutput output)
    {
        var suffix = output.Runtime.Module is { Length: > 0 } m ? $" (módulo Exchange Online PowerShell {m})" : "";
        var truncated = output.Reads.Any(r => r.Truncated)
            ? $" ⚠️ Alguma enumeração atingiu o teto de {output.EnumerationLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "registros"} desta coleta."
            : "";
        return state switch
        {
            KnightSourceState.Completed => "Coleta do Exchange Online concluída" + suffix + "." + truncated,
            KnightSourceState.PartialCollection => "Coleta parcial do Exchange Online — parte das leituras faltou" + suffix + "." + truncated,
            KnightSourceState.InsufficientPermission => "Autorização insuficiente para a coleta do Exchange Online" + suffix + ".",
            KnightSourceState.AuthenticationFailure => "Falha de autenticação da aplicação no Exchange Online" + suffix + ".",
            KnightSourceState.Throttled => "Limite de taxa do Exchange Online durante a coleta" + suffix + ".",
            KnightSourceState.Unavailable => "Exchange Online indisponível durante a coleta" + suffix + ".",
            KnightSourceState.Error => "Erro inesperado durante a coleta do Exchange Online" + suffix + ".",
            _ => "Coleta do Exchange Online" + suffix + ".",
        };
    }
}
