using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Connectors.Microsoft.Knight.Exchange;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-03] Ambiente SINTÉTICO do Exchange Online: monta exatamente o documento JSON que o
/// adaptador de coleta escreve na saída padrão e o faz passar pelo MESMO leitor do produto
/// (<c>PowerShellExchangeAdminReader.Parse</c>). O teste exercita o contrato de verdade — nomes de campo,
/// formato dos itens, sinalizador de truncamento, categorias de falha — em vez de um objeto montado à mão que
/// nunca existiu.
///
/// Dados 100% sintéticos (example.com). Nenhuma credencial, nenhum locatário real, nenhum processo externo.
/// </summary>
internal sealed class ExchangeCollectionScenario : IExchangeAdminReader
{
    internal enum Variant
    {
        /// <summary>Tudo como o critério espera, nas doze leituras.</summary>
        Compliant,

        /// <summary>Cada critério violado de forma comprovável.</summary>
        NonCompliant,

        /// <summary>
        /// HERANÇA: o SMTP AUTH está desabilitado na ORGANIZAÇÃO, e UMA caixa o habilita explicitamente. As
        /// demais herdam. É o caso que uma leitura só da organização aprovaria por engano.
        /// </summary>
        SmtpAuthOverride,

        /// <summary>
        /// ENUMERAÇÃO TRUNCADA: as listas atingiram o teto e nenhuma violação apareceu no trecho lido. Não pode
        /// virar aprovação da organização inteira.
        /// </summary>
        Truncated,

        /// <summary>A leitura das caixas de correio foi recusada por autorização; as outras concluíram.</summary>
        MailboxesDenied,

        /// <summary>
        /// A leitura das CONTAS foi OMITIDA pelo adaptador — ela simplesmente não aparece na resposta. Sem ela,
        /// "caixa compartilhada" não pode virar "conta bloqueada".
        /// </summary>
        SignInReadOmitted,

        /// <summary>
        /// Um dos TRÊS mecanismos de encaminhamento não foi lido (a política de filtro de spam de saída). O
        /// critério de encaminhamento não pode ser declarado fechado.
        /// </summary>
        ForwardingMechanismMissing,

        /// <summary>A conexão de aplicativo foi recusada por autorização — nenhuma leitura foi tentada.</summary>
        NotConnected,

        /// <summary>
        /// INÉRCIA: existe uma regra de transporte que isenta remetentes da filtragem, mas ela está
        /// DESABILITADA. Existe, não age — evidência, não exposição.
        /// </summary>
        InertTransportRule,

        /// <summary>
        /// PRECEDENCIA E ALCANCE: a politica PADRAO atende o criterio, mas existe uma politica NAO padrao
        /// permissiva DECLARADA por uma caixa de correio, e outra permissiva que nenhuma caixa declara. Ler so
        /// a padrao aprovaria o ambiente; contar alcance onde nao ha aprovaria de outro jeito.
        /// </summary>
        PolicyReach,

        /// <summary>
        /// NENHUM REGISTRO: todas as leituras CONCLUIRAM, com zero itens. Nao e recusa, nao e omissao, e nao
        /// pode virar aprovacao das configuracoes que dependem de um documento que nao veio.
        /// </summary>
        NoRecords,

        /// <summary>
        /// TEXTO HOSTIL nos nomes que a fonte devolve (marcação, aspas, quebras). Tem de atravessar o ADM, a
        /// avaliação e as exportações como TEXTO, sem virar marcação nem sumir.
        /// </summary>
        HostileText,
    }

    /// <summary>Marcação sintética usada no cenário de texto hostil. Não é conteúdo de cliente algum.</summary>
    internal const string HostileName = "<script>alert('aegis')</script> & \"aspas\"";

    private readonly Variant _variant;

    internal ExchangeCollectionScenario(Variant variant) => _variant = variant;

    public int Reads { get; private set; }

    public Task<ExchangeAdminOutput> ReadAsync(ExchangeAdminCredentials credentials, CancellationToken ct = default)
    {
        Reads++;
        return Task.FromResult(PowerShellExchangeAdminReader.Parse(Json()));
    }

    public Task<ExchangeAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) =>
        Task.FromResult(PowerShellExchangeAdminReader.Parse(new ExchangeCollectionScenario(Variant.Compliant).Json()));

    internal static string Json(Variant variant) => new ExchangeCollectionScenario(variant).Json();

    // ---- Documento do adaptador ------------------------------------------------------------------------

    internal string Json()
    {
        var v = _variant;

        if (v == Variant.NotConnected)
            return Serialize(new
            {
                runtime = Runtime,
                connected = false,
                // É a recusa REAL do Exchange quando falta o papel de diretório: autorização, não autenticação.
                connectionErrorId = "AuthorizationFailed/UnauthorizedAccessException",
                connectionErrorCategory = "InsufficientPermission",
                enumerationLimit = Limit,
                reads = Array.Empty<object>(),
            });

        if (v == Variant.NoRecords)
            return Serialize(new
            {
                runtime = Runtime,
                connected = true,
                connectionErrorId = (string?)null,
                connectionErrorCategory = (string?)null,
                enumerationLimit = Limit,
                // Cada leitura CONCLUIU — ok = true, sem erro — e voltou vazia. É o caso que um caminho
                // descuidado confunde com falta de permissão, ou pior, lê como "nada de errado".
                reads = AllCapabilities.Select(c => Read(c.Capability, c.Command, Array.Empty<object>())).ToArray(),
            });

        // O cenário de texto hostil é INADEQUADO de propósito: assim o nome com marcação chega ao relatório
        // como objeto AFETADO, que é onde ele precisa aparecer para o operador — e onde precisa ser texto.
        var ok = v is not (Variant.NonCompliant or Variant.HostileText);
        var truncated = v == Variant.Truncated;

        var reads = new List<object>
        {
            Read("ExchangeOrganizationConfig", "Get-OrganizationConfig", new[]
            {
                new Dictionary<string, object?>
                {
                    ["auditDisabled"] = !ok,
                    ["customerLockBoxEnabled"] = ok,
                    ["oAuth2ClientProfileEnabled"] = ok,
                    ["mailTipsAllTipsEnabled"] = ok,
                    ["mailTipsExternalRecipientsTipsEnabled"] = ok,
                    ["mailTipsGroupMetricsEnabled"] = ok,
                    ["mailTipsLargeAudienceThreshold"] = ok ? 25 : 100,
                    // Bookings LIGADO no cenário inadequado: é o que faz o critério depender da política do OWA.
                    ["bookingsEnabled"] = !ok,
                    ["rejectDirectSend"] = ok,
                },
            }),

            Read("ExchangeTransportConfig", "Get-TransportConfig", new[]
            {
                new Dictionary<string, object?>
                {
                    // Na herança, a ORGANIZAÇÃO desabilita — e é a substituição por caixa que expõe.
                    ["smtpClientAuthenticationDisabled"] = v == Variant.SmtpAuthOverride || ok,
                },
            }),

            Read("ExchangeSharingPolicies", "Get-SharingPolicy", SharingPolicies(v, ok)),

            Read("ExchangeOwaMailboxPolicies", "Get-OwaMailboxPolicy", OwaPolicies(v, ok)),

            Read("ExchangeTransportRules", "Get-TransportRule", TransportRules(v, ok)),

            Read("ExchangeRoleAssignmentPolicies", "Get-RoleAssignmentPolicy", new[]
            {
                new Dictionary<string, object?>
                {
                    ["identity"] = "Default Role Assignment Policy",
                    ["name"] = "Default Role Assignment Policy",
                    ["isDefault"] = true,
                    ["assignedRoles"] = ok
                        ? new[] { "MyContactInformation", "MyProfileInformation", "MyBaseOptions" }
                        : new[] { "MyContactInformation", "My Custom Apps", "My Marketplace Apps" },
                },
            }),

            Read("ExchangeExternalSenderIdentification", "Get-ExternalInOutlook", new[]
            {
                new Dictionary<string, object?>
                {
                    ["identity"] = "contoso.onmicrosoft.example.com",
                    ["enabled"] = true,
                    ["allowList"] = ok ? Array.Empty<string>() : new[] { "parceiro.example.com" },
                },
            }),

            v == Variant.ForwardingMechanismMissing
                ? Failed("ExchangeOutboundSpamFilterPolicies", "Get-HostedOutboundSpamFilterPolicy",
                    "InsufficientPermission", "AuthorizationFailed/UnauthorizedAccessException")
                : Read("ExchangeOutboundSpamFilterPolicies", "Get-HostedOutboundSpamFilterPolicy", new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["identity"] = "Default",
                        ["name"] = "Default",
                        ["isDefault"] = true,
                        ["autoForwardingMode"] = ok ? "Off" : "On",
                    },
                }),

            v == Variant.MailboxesDenied
                ? Failed("ExchangeMailboxes", "Get-Mailbox", "InsufficientPermission",
                    "AuthorizationFailed/UnauthorizedAccessException")
                : Read("ExchangeMailboxes", "Get-Mailbox", Mailboxes(v, ok), truncated),

            Read("ExchangeMailboxSignIn", "Get-User", Accounts(v, ok), truncated),

            Read("ExchangeCasMailboxes", "Get-CASMailbox", CasMailboxes(v, ok), truncated),

            Read("ExchangeAuditBypassAssociations", "Get-MailboxAuditBypassAssociation", new[]
            {
                new Dictionary<string, object?>
                {
                    ["identity"] = "servico-integracao@example.com",
                    ["name"] = "Serviço de integração",
                    ["auditBypassEnabled"] = !ok,
                },
            }),
        };

        // A OMISSÃO é o defeito em teste: a leitura SOME da resposta, em vez de voltar vazia ou com falha.
        if (v == Variant.SignInReadOmitted)
            reads.RemoveAt(reads.Count - 3);

        return Serialize(new
        {
            runtime = Runtime,
            connected = true,
            connectionErrorId = (string?)null,
            connectionErrorCategory = (string?)null,
            enumerationLimit = Limit,
            reads,
        });
    }

    // ---- Populações ------------------------------------------------------------------------------------

    private const int Limit = 5000;

    private const string SharedId = "11111111-1111-1111-1111-111111111111";
    private const string UserId = "22222222-2222-2222-2222-222222222222";

    /// <summary>Nomes das politicas NAO padrao do cenario de precedencia e alcance.</summary>
    internal const string OwaAlcancada = "OwaMailboxPolicy-Marketing";
    internal const string OwaSemAlcance = "OwaMailboxPolicy-Obsoleta";
    internal const string CompartilhamentoAlcancado = "Parceiros (ativa)";

    private static object[] SharingPolicies(Variant v, bool ok)
    {
        var politicas = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["identity"] = "Default Sharing Policy",
                ["name"] = v == Variant.HostileText ? HostileName : "Default Sharing Policy",
                ["isDefault"] = true,
                ["enabled"] = true,
                ["domains"] = ok
                    ? new[] { "contoso.example.com: ContactsSharing" }
                    : new[] { "Anonymous: CalendarSharingFreeBusyDetail", "parceiro.example.com: CalendarSharingFreeBusySimple" },
            },
            new Dictionary<string, object?>
            {
                // Política DESLIGADA com compartilhamento de calendário: existe, não concede — evidência.
                ["identity"] = "Parceiros (desativada)",
                ["name"] = "Parceiros (desativada)",
                ["isDefault"] = false,
                ["enabled"] = false,
                ["domains"] = new[] { "Anonymous: CalendarSharingFreeBusyDetail" },
            },
        };

        if (v == Variant.PolicyReach)
            politicas.Add(new Dictionary<string, object?>
            {
                // NÃO é a padrão, está LIGADA e é declarada por uma caixa de correio: a padrão atender não basta.
                ["identity"] = CompartilhamentoAlcancado,
                ["name"] = CompartilhamentoAlcancado,
                ["isDefault"] = false,
                ["enabled"] = true,
                ["domains"] = new[] { "Anonymous: CalendarSharingFreeBusyDetail" },
            });

        return politicas.ToArray();
    }

    private static object[] OwaPolicies(Variant v, bool ok)
    {
        var politicas = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["identity"] = "OwaMailboxPolicy-Default",
                ["name"] = "OwaMailboxPolicy-Default",
                ["isDefault"] = true,
                ["additionalStorageProvidersAvailable"] = !ok,
                ["personalAccountsEnabled"] = !ok,
                ["personalAccountCalendarsEnabled"] = false,
                ["bookingsMailboxCreationEnabled"] = !ok,
            },
        };

        if (v == Variant.PolicyReach)
        {
            // Permissiva E alcançada: uma caixa de correio a declara. O alcance é demonstrável pela enumeração.
            politicas.Add(new Dictionary<string, object?>
            {
                ["identity"] = OwaAlcancada,
                ["name"] = OwaAlcancada,
                ["isDefault"] = false,
                ["additionalStorageProvidersAvailable"] = true,
                ["personalAccountsEnabled"] = true,
                ["personalAccountCalendarsEnabled"] = false,
                ["bookingsMailboxCreationEnabled"] = false,
            });
            // Permissiva e SEM alcance demonstrado: nenhuma caixa lida a declara. Continua sendo um defeito de
            // configuração — o que muda é o que pode ser dito sobre quem ela atinge hoje.
            politicas.Add(new Dictionary<string, object?>
            {
                ["identity"] = OwaSemAlcance,
                ["name"] = OwaSemAlcance,
                ["isDefault"] = false,
                ["additionalStorageProvidersAvailable"] = true,
                ["personalAccountsEnabled"] = true,
                ["personalAccountCalendarsEnabled"] = false,
                ["bookingsMailboxCreationEnabled"] = false,
            });
        }

        return politicas.ToArray();
    }

    private static object[] TransportRules(Variant v, bool ok)
    {
        var rules = new List<object>();

        if (v == Variant.InertTransportRule)
        {
            rules.Add(new Dictionary<string, object?>
            {
                ["identity"] = "Isenta parceiro (desabilitada)",
                ["name"] = "Isenta parceiro (desabilitada)",
                ["state"] = "Disabled",
                ["mode"] = "Enforce",
                ["priority"] = 0,
                ["setScl"] = -1,
                ["senderDomainIs"] = new[] { "parceiro.example.com" },
                ["fromAddressContainsWords"] = Array.Empty<string>(),
                ["fromAddressMatchesPatterns"] = Array.Empty<string>(),
                ["redirectMessageTo"] = Array.Empty<string>(),
                ["blindCopyTo"] = Array.Empty<string>(),
                ["addToRecipients"] = Array.Empty<string>(),
                ["copyTo"] = Array.Empty<string>(),
            });
            return rules.ToArray();
        }

        if (!ok)
        {
            rules.Add(new Dictionary<string, object?>
            {
                ["identity"] = "Isenta parceiro",
                ["name"] = "Isenta parceiro",
                ["state"] = "Enabled",
                ["mode"] = "Enforce",
                ["priority"] = 0,
                ["setScl"] = -1,
                ["senderDomainIs"] = new[] { "parceiro.example.com", "fornecedor.example.com" },
                ["fromAddressContainsWords"] = Array.Empty<string>(),
                ["fromAddressMatchesPatterns"] = Array.Empty<string>(),
                ["redirectMessageTo"] = Array.Empty<string>(),
                ["blindCopyTo"] = Array.Empty<string>(),
                ["addToRecipients"] = Array.Empty<string>(),
                ["copyTo"] = Array.Empty<string>(),
            });
            rules.Add(new Dictionary<string, object?>
            {
                ["identity"] = "Copia auditoria",
                ["name"] = "Copia auditoria",
                ["state"] = "Enabled",
                ["mode"] = "Enforce",
                ["priority"] = 1,
                ["setScl"] = (int?)null,
                ["senderDomainIs"] = Array.Empty<string>(),
                ["fromAddressContainsWords"] = Array.Empty<string>(),
                ["fromAddressMatchesPatterns"] = Array.Empty<string>(),
                ["redirectMessageTo"] = new[] { "arquivo@externo.example.com" },
                ["blindCopyTo"] = Array.Empty<string>(),
                ["addToRecipients"] = Array.Empty<string>(),
                ["copyTo"] = Array.Empty<string>(),
            });
        }

        return rules.ToArray();
    }

    private static object[] Mailboxes(Variant v, bool ok)
    {
        var shared = new Dictionary<string, object?>
        {
            ["externalDirectoryObjectId"] = SharedId,
            ["userPrincipalName"] = "faturamento@example.com",
            ["displayName"] = v == Variant.HostileText ? HostileName : "Faturamento",
            ["recipientTypeDetails"] = "SharedMailbox",
            ["auditEnabled"] = true,
            ["auditOwner"] = AllOwner,
            ["auditDelegate"] = AllDelegate,
            ["auditAdmin"] = AllAdmin,
            ["forwardingSmtpAddress"] = (string?)null,
            ["forwardingAddress"] = (string?)null,
            ["deliverToMailboxAndForward"] = false,
            ["roleAssignmentPolicy"] = "Default Role Assignment Policy",
            ["sharingPolicy"] = v == Variant.PolicyReach ? CompartilhamentoAlcancado : "Default Sharing Policy",
        };

        var user = new Dictionary<string, object?>
        {
            ["externalDirectoryObjectId"] = UserId,
            ["userPrincipalName"] = "ana.silva@example.com",
            ["displayName"] = "Ana Silva",
            ["recipientTypeDetails"] = "UserMailbox",
            ["auditEnabled"] = true,
            // No cenário inadequado faltam ações no acesso DELEGADO — e só nele: os três tipos são independentes.
            ["auditOwner"] = AllOwner,
            ["auditDelegate"] = ok ? AllDelegate : new[] { "Create", "Update" },
            ["auditAdmin"] = AllAdmin,
            ["forwardingSmtpAddress"] = ok ? null : "arquivo.pessoal@externo.example.com",
            ["forwardingAddress"] = (string?)null,
            ["deliverToMailboxAndForward"] = !ok,
            ["roleAssignmentPolicy"] = "Default Role Assignment Policy",
            ["sharingPolicy"] = "Default Sharing Policy",
        };

        return [shared, user];
    }

    private static object[] Accounts(Variant v, bool ok) =>
    [
        new Dictionary<string, object?>
        {
            ["externalDirectoryObjectId"] = SharedId,
            ["userPrincipalName"] = "faturamento@example.com",
            // A conta da caixa COMPARTILHADA: bloqueada no cenário conforme, liberada no inadequado.
            ["accountDisabled"] = ok,
        },
        new Dictionary<string, object?>
        {
            ["externalDirectoryObjectId"] = UserId,
            ["userPrincipalName"] = "ana.silva@example.com",
            ["accountDisabled"] = false,
        },
    ];

    private static object[] CasMailboxes(Variant v, bool ok) =>
    [
        new Dictionary<string, object?>
        {
            ["externalDirectoryObjectId"] = SharedId,
            ["userPrincipalName"] = "faturamento@example.com",
            ["displayName"] = "Faturamento",
            // AUSENTE = herda a organização. É o valor que um contrato descuidado leria como "desabilitado".
            ["smtpClientAuthenticationDisabled"] = (bool?)null,
            ["owaMailboxPolicy"] = "OwaMailboxPolicy-Default",
        },
        new Dictionary<string, object?>
        {
            ["externalDirectoryObjectId"] = UserId,
            ["userPrincipalName"] = "ana.silva@example.com",
            ["displayName"] = "Ana Silva",
            ["smtpClientAuthenticationDisabled"] = v == Variant.SmtpAuthOverride || !ok ? false : (bool?)null,
            ["owaMailboxPolicy"] = v == Variant.PolicyReach ? OwaAlcancada : "OwaMailboxPolicy-Default",
        },
    ];

    private static readonly string[] AllOwner = ExchangeMailboxAuditInventory.RequiredOwnerActions.ToArray();
    private static readonly string[] AllDelegate = ExchangeMailboxAuditInventory.RequiredDelegateActions.ToArray();
    private static readonly string[] AllAdmin = ExchangeMailboxAuditInventory.RequiredAdminActions.ToArray();

    // ---- Forma do documento ----------------------------------------------------------------------------

    private static readonly object Runtime = new { powerShell = "7.4.7", module = "3.9.2", platform = "Unix" };

    /// <summary>As doze leituras do adaptador, na ordem em que ele as executa.</summary>
    private static readonly (string Capability, string Command)[] AllCapabilities =
    [
        ("ExchangeOrganizationConfig", "Get-OrganizationConfig"),
        ("ExchangeTransportConfig", "Get-TransportConfig"),
        ("ExchangeSharingPolicies", "Get-SharingPolicy"),
        ("ExchangeOwaMailboxPolicies", "Get-OwaMailboxPolicy"),
        ("ExchangeTransportRules", "Get-TransportRule"),
        ("ExchangeRoleAssignmentPolicies", "Get-RoleAssignmentPolicy"),
        ("ExchangeExternalSenderIdentification", "Get-ExternalInOutlook"),
        ("ExchangeOutboundSpamFilterPolicies", "Get-HostedOutboundSpamFilterPolicy"),
        ("ExchangeMailboxes", "Get-Mailbox"),
        ("ExchangeMailboxSignIn", "Get-User"),
        ("ExchangeCasMailboxes", "Get-CASMailbox"),
        ("ExchangeAuditBypassAssociations", "Get-MailboxAuditBypassAssociation"),
    ];

    private static object Read(string capability, string command, object items, bool truncated = false) => new
    {
        capability,
        command,
        ok = true,
        items,
        truncated,
        errorCategory = (string?)null,
        errorId = (string?)null,
    };

    private static object Failed(string capability, string command, string category, string errorId) => new
    {
        capability,
        command,
        ok = false,
        items = Array.Empty<object>(),
        truncated = false,
        errorCategory = category,
        errorId,
    };

    private static string Serialize(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });
}
