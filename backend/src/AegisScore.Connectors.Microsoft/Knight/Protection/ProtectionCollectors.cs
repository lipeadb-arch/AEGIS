using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Connectors.Microsoft.Knight.Exchange;
using AegisScore.Connectors.Microsoft.Knight.PowerShell;
using AegisScore.Connectors.Microsoft.Knight.Services;
using AegisScore.Domain;
using Microsoft.Extensions.Logging;
using static AegisScore.Connectors.Microsoft.Knight.Services.KnightJson;

namespace AegisScore.Connectors.Microsoft.Knight.Protection;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] Defender para Office 365 e Purview pelo módulo oficial do Exchange Online
// ============================================================================

public sealed class ProtectionPowerShellOptions : PowerShellAdapterOptions
{
    public int EnumerationLimit { get; set; } = 5000;
}

/// <summary>Saída do adaptador de proteção: a do Exchange mais o desfecho da sessão do Security &amp; Compliance.</summary>
public sealed record ProtectionAdminOutput(ExchangeAdminOutput Exchange, bool ComplianceConnected, string? ComplianceErrorCategory);

/// <summary>Pedido ao adaptador: perfil, organização e tokens (o do Security &amp; Compliance só no Purview).</summary>
public sealed record ProtectionAdminRequest(string Profile, string Organization, string AccessToken, string? ComplianceToken)
{
    public const string Defender = "defender";
    public const string Purview = "purview";

    public override string ToString() => $"ProtectionAdminRequest {{ Profile = {Profile}, Organization = {Organization}, tokens = *** }}";
}

public interface IProtectionAdminReader
{
    Task<ProtectionAdminOutput> ReadAsync(ProtectionAdminRequest request, CancellationToken ct = default);

    Task<ProtectionAdminOutput> TestConnectionAsync(ProtectionAdminRequest request, CancellationToken ct = default);
}

/// <summary>Executa o script embutido <c>m365-protection-collect.ps1</c> pelo mesmo processo isolado do Exchange.</summary>
public sealed class PowerShellProtectionAdminReader : IProtectionAdminReader
{
    private const string ScriptResourceName = "AegisScore.Knight.Protection.Collect.ps1";

    private readonly PowerShellAdapterProcess _process;
    private readonly ProtectionPowerShellOptions _options;

    public PowerShellProtectionAdminReader(ProtectionPowerShellOptions options, ILogger<PowerShellProtectionAdminReader>? log = null)
    {
        _options = options ?? new ProtectionPowerShellOptions();
        _process = new PowerShellAdapterProcess(
            _options, ScriptResourceName, "m365-protection-collect.ps1", "aegis-protection-", "Proteção do Microsoft 365", log);
    }

    public Task<ProtectionAdminOutput> ReadAsync(ProtectionAdminRequest request, CancellationToken ct = default) => RunAsync("collect", request, ct);

    public Task<ProtectionAdminOutput> TestConnectionAsync(ProtectionAdminRequest request, CancellationToken ct = default) => RunAsync("test", request, ct);

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] Verificação de runtime (sem rede, sem locatário): o módulo importa e os comandos de
    /// conexão existem. Ver <see cref="ProtectionRuntimeDiagnostics"/>.
    /// </summary>
    public Task<ProtectionAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) =>
        RunAsync("module-check", new ProtectionAdminRequest(ProtectionAdminRequest.Defender, "aegis-sintetico.example.invalid",
            ProtectionRuntimeDiagnostics.SyntheticToken, null), ct);

    private async Task<ProtectionAdminOutput> RunAsync(string mode, ProtectionAdminRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.Serialize(new
        {
            mode,
            profile = request.Profile,
            organization = request.Organization,
            accessToken = request.AccessToken,
            complianceToken = request.ComplianceToken,
            enumerationLimit = _options.EnumerationLimit,
        });

        PowerShellAdapterRun run;
        try
        {
            run = await _process.RunAsync(payload, [request.AccessToken, request.ComplianceToken], ct);
        }
        catch (PowerShellAdapterException ex) when (ex is not ExchangeAdminTransportException)
        {
            throw new ExchangeAdminTransportException(ex.Message, ex.InnerException);
        }
        return Parse(run.StandardOutput);
    }

    internal static ProtectionAdminOutput Parse(string json)
    {
        var exchange = PowerShellExchangeAdminReader.Parse(json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new ProtectionAdminOutput(exchange,
            root.TryGetProperty("complianceConnected", out var c) && c.ValueKind == JsonValueKind.True,
            root.TryGetProperty("complianceConnectionErrorCategory", out var cat) && cat.ValueKind == JsonValueKind.String ? cat.GetString() : null);
    }
}

/// <summary>Base dos dois coletores: token, organização, execução do adaptador e tradução das leituras.</summary>
public abstract class ProtectionCollectorBase : IKnightCollector
{
    /// <summary>Recurso documentado do Security &amp; Compliance PowerShell para token de aplicativo.</summary>
    internal const string ComplianceScope = "https://ps.compliance.protection.outlook.com/.default";

    protected readonly IExchangeTokenClient ExchangeTokens;
    protected readonly IProtectionAdminReader Reader;
    protected readonly ILogger? Log;
    protected readonly TimeProvider Time;

    protected ProtectionCollectorBase(IExchangeTokenClient exchangeTokens, IProtectionAdminReader reader, ILogger? log, TimeProvider? time)
    {
        ExchangeTokens = exchangeTokens;
        Reader = reader;
        Log = log;
        Time = time ?? TimeProvider.System;
    }

    public abstract KnightSourceType Source { get; }

    protected string Label => KnightSourceCatalog.Label(Source);

    public abstract IReadOnlyList<KnightCapability> Capabilities { get; }

    protected abstract string Profile { get; }

    /// <summary>Capacidades que dependem da sessão do Security &amp; Compliance (as demais usam a do Exchange Online).</summary>
    protected virtual IReadOnlySet<KnightCapability> ComplianceCapabilities { get; } = new HashSet<KnightCapability>();

    protected virtual Task<string?> ComplianceTokenAsync(IMicrosoftGraphCredentials cfg, CancellationToken ct) => Task.FromResult<string?>(null);

    public async Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Configuration is not IMicrosoftGraphCredentials cfg || context.Configuration.Source != Source)
            return KnightCollectionResult.NotConfigured(Source, Label);

        ExchangeAdminCredentials credentials;
        string? complianceToken;
        try
        {
            credentials = await ExchangeTokens.AcquireAsync(cfg, ct);
            complianceToken = await ComplianceTokenAsync(cfg, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EntraGraphException ex)
        {
            var state = KnightCapabilityRecorder.StateFor(ex.Kind);
            var (_, reason) = KnightCapabilityRecorder.Classify(ex, Label);
            return KnightCapabilityRecorder.Failure(Source, Label, Capabilities, state, KnightCapabilityRecorder.OutcomeFor(state),
                reason, Time.GetUtcNow());
        }

        ProtectionAdminOutput output;
        try
        {
            output = await Reader.ReadAsync(new ProtectionAdminRequest(Profile, credentials.Organization, credentials.AccessToken, complianceToken), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ExchangeAdminTransportException ex)
        {
            Log?.LogWarning(ex, "O adaptador de proteção do Microsoft 365 não pôde ser executado.");
            var reason = $"O adaptador de coleta de {Label} não pôde ser executado neste ambiente: o runtime do PowerShell ou o módulo "
                + "oficial não respondeu como esperado. Nenhuma leitura foi tentada.";
            return KnightCapabilityRecorder.Failure(Source, Label, Capabilities, KnightSourceState.Unavailable,
                KnightCapabilityOutcome.Unavailable, reason, Time.GetUtcNow());
        }

        var recorder = new KnightCapabilityRecorder(Label, Log);
        var reads = output.Exchange.Reads
            .GroupBy(r => r.Capability, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var capability in Capabilities)
        {
            var compliance = ComplianceCapabilities.Contains(capability);
            if (!reads.TryGetValue(capability.ToString(), out var read))
            {
                // Leitura ausente: o motivo é a conexão da sessão dela, quando ela não conectou.
                var category = compliance ? output.ComplianceErrorCategory : output.Exchange.ConnectionErrorCategory;
                var connected = compliance ? output.ComplianceConnected : output.Exchange.Connected;
                var outcome = connected ? KnightCapabilityOutcome.NotAttempted : ParseOutcome(category) ?? KnightCapabilityOutcome.AuthenticationFailure;
                recorder.Record(capability, outcome, connected
                    ? "O adaptador não registrou esta leitura nesta execução."
                    : ConnectionReason(outcome, compliance, cfg.ClientCertificate is not null));
                continue;
            }

            if (!read.Ok)
            {
                var outcome = ParseOutcome(read.ErrorCategory) ?? KnightCapabilityOutcome.Error;
                recorder.Record(capability, outcome, ReadReason(outcome, read));
                continue;
            }

            try
            {
                var docs = Translate(capability, read.Items).ToList();
                recorder.Documents.AddRange(docs);
                recorder.Record(capability, KnightCapabilityOutcome.Collected, read.Truncated
                    ? $"Leitura concluída, mas a enumeração atingiu o teto desta coleta (comando: {read.Command})."
                    : null);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                Log?.LogWarning(ex, "Resposta de {Capability} ilegível para o contrato desta versão.", capability);
                recorder.Record(capability, KnightCapabilityOutcome.Error,
                    $"A resposta desta leitura não pôde ser interpretada pelo contrato desta versão (comando: {read.Command}).");
            }
        }

        await AfterReadsAsync(recorder, ct);
        return recorder.Result(Source, Label, Time.GetUtcNow());
    }

    /// <summary>Leituras complementares FORA do PowerShell (ex.: DNS), depois das leituras do adaptador.</summary>
    protected virtual Task AfterReadsAsync(KnightCapabilityRecorder recorder, CancellationToken ct) => Task.CompletedTask;

    protected abstract IEnumerable<KnightConfigurationDocument> Translate(KnightCapability capability, JsonElement items);

    internal static KnightCapabilityOutcome? ParseOutcome(string? category) =>
        Enum.TryParse<KnightCapabilityOutcome>(category, ignoreCase: true, out var parsed) ? parsed : null;

    /// <summary>
    /// Motivo da falha de CONEXÃO, montado pela categoria — nunca texto da fonte. Uma recusa não identifica a causa;
    /// o texto enumera as verificações (a mesma regra firmada no Exchange Online).
    /// </summary>
    internal static string ConnectionReason(KnightCapabilityOutcome outcome, bool compliance, bool certificate)
    {
        var service = compliance ? "Security & Compliance PowerShell" : "Exchange Online";
        var permission = compliance
            ? "Exchange.ManageAsApp na API Microsoft Exchange Online Protection"
            : "Exchange.ManageAsApp na API Office 365 Exchange Online";
        var method = certificate
            ? "o token foi obtido por certificado, o método documentado"
            : "o token foi obtido por segredo de cliente, sem confirmação documental de que o serviço o aceite";
        return outcome switch
        {
            KnightCapabilityOutcome.InsufficientPermission =>
                $"A conexão com o {service} foi recusada por autorização, e a recusa não identifica a causa. Verifique: (1) {permission}; "
                + "(2) papel de diretório na aplicação — para leitura, Leitor Global; (3) o domínio de organização; "
                + $"(4) o método — {method}.",
            KnightCapabilityOutcome.AuthenticationFailure =>
                $"A conexão com o {service} falhou na autenticação, e a falha não identifica a causa. Verifique a credencial da "
                + $"aplicação e se o token foi emitido para o recurso deste serviço; {method}.",
            KnightCapabilityOutcome.Throttled => $"O {service} aplicou limite de taxa ao conectar. Nenhuma leitura desta sessão foi tentada.",
            KnightCapabilityOutcome.LimitedByLicense => $"A conexão de aplicativo com o {service} foi recusada por licença do locatário.",
            _ => $"A conexão de aplicativo com o {service} não foi estabelecida. Nenhuma leitura desta sessão foi tentada.",
        };
    }

    private static string ReadReason(KnightCapabilityOutcome outcome, ExchangeAdminRead read)
    {
        var head = outcome switch
        {
            KnightCapabilityOutcome.InsufficientPermission =>
                "Autorização recusada nesta leitura. A conexão foi estabelecida, então falta alcance sobre ESTE comando; verifique o "
                + "papel de diretório atribuído à aplicação (para leitura, Leitor Global).",
            KnightCapabilityOutcome.AuthenticationFailure => "Falha de autenticação da aplicação nesta leitura.",
            KnightCapabilityOutcome.Throttled => "Limite de taxa nesta leitura.",
            KnightCapabilityOutcome.LimitedByLicense =>
                "O locatário não tem a licença que este recurso exige (o comando não existe sem ela).",
            KnightCapabilityOutcome.Unavailable =>
                "A leitura não pôde ser concluída: o comando não foi importado nesta sessão (pode exigir licença), ou o serviço não respondeu.",
            _ => "Erro inesperado nesta leitura.",
        };
        return $"{head} (comando: {read.Command})";
    }

    // ---- Tradução compartilhada ----------------------------------------------------------------------

    protected static IEnumerable<JsonElement> Each(JsonElement items) =>
        items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object) : Enumerable.Empty<JsonElement>();

    protected static DefenderPolicyReach Reach(JsonElement policy)
    {
        var r = Prop(policy, "reach");
        return new DefenderPolicyReach(
            Bool(policy, "isDefault") == true,
            Bool(r, "hasRule") == true,
            Str(r, "state"),
            Int(r, "priority"),
            Strings(r, "sentTo"),
            Strings(r, "sentToMemberOf"),
            Strings(r, "recipientDomainIs"));
    }

    protected static string Id(JsonElement e) =>
        Str(e, "identity") ?? Str(e, "name") ?? throw new InvalidOperationException("política sem identificador");
}

/// <summary>
/// Microsoft Defender para Office 365: as políticas de proteção de e-mail e colaboração, lidas na sessão de aplicativo
/// do Exchange Online (sem permissão nova), e os registros SPF e DMARC dos domínios aceitos, por consulta DNS.
/// </summary>
public sealed class DefenderForOffice365KnightCollector : ProtectionCollectorBase
{
    private readonly IDnsTxtResolver _dns;

    public DefenderForOffice365KnightCollector(IExchangeTokenClient exchangeTokens, IProtectionAdminReader reader, IDnsTxtResolver dns,
        ILogger<DefenderForOffice365KnightCollector>? log = null, TimeProvider? time = null) : base(exchangeTokens, reader, log, time)
        => _dns = dns;

    public override KnightSourceType Source => KnightSourceType.MicrosoftDefenderForOffice365;

    protected override string Profile => ProtectionAdminRequest.Defender;

    /// <summary>Leituras do adaptador (a de DNS é feita pelo AEGIS depois, sobre os domínios aceitos lidos aqui).</summary>
    internal static readonly KnightCapability[] AdapterCapabilities =
    {
        KnightCapability.DefenderAtpPolicy,
        KnightCapability.DefenderSafeLinks,
        KnightCapability.DefenderSafeAttachments,
        KnightCapability.DefenderMalwareFilter,
        KnightCapability.DefenderInboundSpam,
        KnightCapability.DefenderOutboundSpam,
        KnightCapability.DefenderConnectionFilter,
        KnightCapability.DefenderAntiPhish,
        KnightCapability.DefenderDkim,
        KnightCapability.DefenderAcceptedDomains,
        KnightCapability.DefenderTeamsProtection,
        KnightCapability.DefenderPriorityAccounts,
        KnightCapability.DefenderPresetPolicies,
        KnightCapability.DefenderReportSubmissionPolicy,
    };

    public override IReadOnlyList<KnightCapability> Capabilities => AdapterCapabilities;

    /// <summary>Teto de domínios consultados no DNS numa coleta (os demais ficam declarados como não consultados).</summary>
    internal const int MaxDnsDomains = 200;

    protected override async Task AfterReadsAsync(KnightCapabilityRecorder recorder, CancellationToken ct)
    {
        // Sem a lista de domínios aceitos não há o que consultar: a leitura de DNS herda o motivo dela.
        var accepted = recorder.Capabilities.FirstOrDefault(c => c.Capability == KnightCapability.DefenderAcceptedDomains);
        if (accepted?.Outcome != KnightCapabilityOutcome.Collected)
        {
            recorder.Record(KnightCapability.DefenderDnsRecords, KnightCapabilityOutcome.NotAttempted,
                "Os registros DNS não foram consultados porque a lista de domínios aceitos não foi lida nesta coleta.");
            return;
        }

        var domains = new KnightTenantConfiguration(recorder.Documents, recorder.Capabilities).Read<DefenderAcceptedDomain>().Items
            .Where(d => !d.IsMicrosoftManaged)
            .Select(d => d.DomainName.Trim().ToLowerInvariant())
            .Distinct()
            .Take(MaxDnsDomains)
            .ToList();

        await recorder.RunAsync(KnightCapability.DefenderDnsRecords, async add =>
        {
            foreach (var domain in domains)
            {
                var spf = await _dns.ResolveTxtAsync(domain, ct);
                var dmarc = await _dns.ResolveTxtAsync("_dmarc." + domain, ct);
                add(KnightTenantConfiguration.Document(domain, domain, new DefenderDnsRecord(
                    domain,
                    spf.Resolved, spf.Records.Where(r => r.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList(),
                    dmarc.Resolved, dmarc.Records.Where(r => r.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)).ToList(),
                    spf.Failure ?? dmarc.Failure)));
            }
        }, ct);
    }

    protected override IEnumerable<KnightConfigurationDocument> Translate(KnightCapability capability, JsonElement items)
    {
        switch (capability)
        {
            case KnightCapability.DefenderAtpPolicy:
                foreach (var e in Each(items).Take(1))
                    yield return KnightTenantConfiguration.Document(DefenderAtpPolicy.ExternalId, "Configuração global do Defender para Office 365",
                        new DefenderAtpPolicy(Bool(e, "enableATPForSPOTeamsODB"), Bool(e, "enableSafeDocs"), Bool(e, "allowSafeDocsOpen")));
                break;
            case KnightCapability.DefenderSafeLinks:
                foreach (var e in Each(items))
                    yield return KnightTenantConfiguration.Document(Id(e), Str(e, "name"), new DefenderSafeLinksPolicy(Id(e), Str(e, "name"),
                        Bool(e, "enableSafeLinksForEmail"), Bool(e, "enableSafeLinksForTeams"), Bool(e, "enableSafeLinksForOffice"),
                        Bool(e, "trackClicks"), Bool(e, "allowClickThrough"), Bool(e, "scanUrls"), Bool(e, "enableForInternalSenders"),
                        Bool(e, "deliverMessageAfterScan"), Bool(e, "disableUrlRewrite"), Bool(e, "isBuiltInProtection") == true, Reach(e)));
                break;
            case KnightCapability.DefenderSafeAttachments:
                foreach (var e in Each(items))
                    yield return KnightTenantConfiguration.Document(Id(e), Str(e, "name"), new DefenderSafeAttachmentPolicy(Id(e), Str(e, "name"),
                        Bool(e, "enable"), Str(e, "action"), Bool(e, "isBuiltInProtection") == true, Reach(e)));
                break;
            case KnightCapability.DefenderMalwareFilter:
                foreach (var e in Each(items))
                    yield return KnightTenantConfiguration.Document(Id(e), Str(e, "name"), new DefenderMalwarePolicy(Id(e), Str(e, "name"),
                        Bool(e, "enableFileFilter"), Strings(e, "fileTypes"), Str(e, "fileTypeAction"),
                        Bool(e, "enableInternalSenderAdminNotifications"), Bool(e, "internalSenderAdminAddressSet") == true,
                        Bool(e, "zapEnabled"), Reach(e)));
                break;
            case KnightCapability.DefenderInboundSpam:
                foreach (var e in Each(items))
                    yield return KnightTenantConfiguration.Document(Id(e), Str(e, "name"), new DefenderInboundSpamPolicy(Id(e), Str(e, "name"),
                        Strings(e, "allowedSenderDomains"), Int(e, "allowedSendersCount") ?? 0, Reach(e)));
                break;
            case KnightCapability.DefenderOutboundSpam:
                foreach (var e in Each(items))
                    yield return KnightTenantConfiguration.Document(Id(e), Str(e, "name"), new DefenderOutboundSpamPolicy(Id(e), Str(e, "name"),
                        Int(e, "recipientLimitExternalPerHour"), Int(e, "recipientLimitInternalPerHour"), Int(e, "recipientLimitPerDay"),
                        Str(e, "actionWhenThresholdReached"), Bool(e, "notifyOutboundSpam"), Int(e, "notifyOutboundSpamRecipientsCount") ?? 0,
                        Bool(e, "bccSuspiciousOutboundMail"), Int(e, "bccSuspiciousOutboundAdditionalRecipientsCount") ?? 0, Reach(e)));
                break;
            case KnightCapability.DefenderConnectionFilter:
                foreach (var e in Each(items))
                    yield return KnightTenantConfiguration.Document(Id(e), Str(e, "name"), new DefenderConnectionFilterPolicy(Id(e), Str(e, "name"),
                        Strings(e, "ipAllowList"), Bool(e, "enableSafeList")));
                break;
            case KnightCapability.DefenderAntiPhish:
                foreach (var e in Each(items))
                    yield return KnightTenantConfiguration.Document(Id(e), Str(e, "name"), new DefenderAntiPhishPolicy(Id(e), Str(e, "name"),
                        Bool(e, "enabled"), Int(e, "phishThresholdLevel"), Bool(e, "enableTargetedUserProtection"),
                        Int(e, "targetedUsersToProtectCount") ?? 0, Bool(e, "enableOrganizationDomainsProtection"),
                        Bool(e, "enableMailboxIntelligence"), Bool(e, "enableMailboxIntelligenceProtection"), Bool(e, "enableSpoofIntelligence"),
                        Str(e, "targetedUserProtectionAction"), Str(e, "targetedDomainProtectionAction"),
                        Str(e, "mailboxIntelligenceProtectionAction"), Reach(e)));
                break;
            case KnightCapability.DefenderDkim:
                foreach (var e in Each(items))
                    if (Str(e, "domain") is { } domain)
                        yield return KnightTenantConfiguration.Document(domain, domain, new DefenderDkimSigning(domain, Bool(e, "enabled"), Str(e, "status")));
                break;
            case KnightCapability.DefenderAcceptedDomains:
                foreach (var e in Each(items))
                    if (Str(e, "domainName") is { } domain)
                        yield return KnightTenantConfiguration.Document(domain, domain,
                            new DefenderAcceptedDomain(domain, Str(e, "domainType"), Bool(e, "isDefault")));
                break;
            case KnightCapability.DefenderTeamsProtection:
                foreach (var e in Each(items))
                    yield return KnightTenantConfiguration.Document(Str(e, "identity") ?? "TeamsProtectionPolicy", "Proteção do Teams",
                        new DefenderTeamsProtection(Str(e, "identity") ?? "TeamsProtectionPolicy", Bool(e, "zapEnabled")));
                break;
            case KnightCapability.DefenderPriorityAccounts:
                foreach (var e in Each(items).Take(1))
                {
                    var accounts = Items(e, "accounts").Select(a => new DefenderPriorityAccount(
                        Str(a, "externalDirectoryObjectId"), Str(a, "userPrincipalName"), Str(a, "displayName"))).ToList();
                    yield return KnightTenantConfiguration.Document(DefenderPriorityAccounts.ExternalId, "Contas prioritárias",
                        new DefenderPriorityAccounts(Bool(e, "enablePriorityAccountProtection"), accounts, Bool(e, "listComplete") != false));
                }
                break;
            case KnightCapability.DefenderPresetPolicies:
                foreach (var e in Each(items))
                {
                    var kind = Str(e, "kind") ?? DefenderPresetRule.KindEop;
                    yield return KnightTenantConfiguration.Document($"{kind}:{Id(e)}", Str(e, "name"), new DefenderPresetRule(kind, Id(e),
                        Str(e, "name"), Str(e, "state"), Int(e, "priority"), Strings(e, "sentTo"), Strings(e, "sentToMemberOf"),
                        Strings(e, "recipientDomainIs")));
                }
                break;
            case KnightCapability.DefenderReportSubmissionPolicy:
                foreach (var e in Each(items))
                {
                    var identity = Str(e, "identity") ?? "DefaultReportSubmissionPolicy";
                    yield return KnightTenantConfiguration.Document(identity, "Política de envio de mensagens denunciadas",
                        new DefenderReportSubmissionPolicy(identity,
                            Bool(e, "reportJunkToCustomizedAddress"), Bool(e, "reportNotJunkToCustomizedAddress"), Bool(e, "reportPhishToCustomizedAddress"),
                            (int)(Int(e, "reportJunkAddresses") ?? 0), (int)(Int(e, "reportNotJunkAddresses") ?? 0), (int)(Int(e, "reportPhishAddresses") ?? 0),
                            Bool(e, "reportChatMessageEnabled"), Bool(e, "reportChatMessageToCustomizedAddressEnabled"),
                            (int)(Int(e, "reportChatMessageAddresses") ?? 0)));
                }
                break;
        }
    }
}

/// <summary>
/// Microsoft Purview: a ingestão do log de auditoria unificado (sessão do Exchange Online) e as políticas de DLP e de
/// rótulos de confidencialidade (sessão do Security &amp; Compliance, com OUTRO token e OUTRA permissão:
/// <c>Exchange.ManageAsApp</c> na API Microsoft Exchange Online Protection).
/// </summary>
public sealed class PurviewKnightCollector : ProtectionCollectorBase
{
    private readonly IMicrosoftAppTokenClient _tokens;

    public PurviewKnightCollector(IExchangeTokenClient exchangeTokens, IProtectionAdminReader reader, IMicrosoftAppTokenClient tokens,
        ILogger<PurviewKnightCollector>? log = null, TimeProvider? time = null) : base(exchangeTokens, reader, log, time)
        => _tokens = tokens;

    public override KnightSourceType Source => KnightSourceType.MicrosoftPurview;

    protected override string Profile => ProtectionAdminRequest.Purview;

    public override IReadOnlyList<KnightCapability> Capabilities { get; } = new[]
    {
        KnightCapability.PurviewAuditConfig,
        KnightCapability.PurviewDlpPolicies,
        KnightCapability.PurviewLabelPolicies,
    };

    protected override IReadOnlySet<KnightCapability> ComplianceCapabilities { get; } =
        new HashSet<KnightCapability> { KnightCapability.PurviewDlpPolicies, KnightCapability.PurviewLabelPolicies };

    protected override async Task<string?> ComplianceTokenAsync(IMicrosoftGraphCredentials cfg, CancellationToken ct) =>
        await _tokens.AcquireAsync(cfg, ComplianceScope, ct);

    protected override IEnumerable<KnightConfigurationDocument> Translate(KnightCapability capability, JsonElement items)
    {
        switch (capability)
        {
            case KnightCapability.PurviewAuditConfig:
                foreach (var e in Each(items).Take(1))
                    yield return KnightTenantConfiguration.Document(PurviewAuditConfig.ExternalId, "Log de auditoria unificado",
                        new PurviewAuditConfig(Bool(e, "unifiedAuditLogIngestionEnabled")));
                break;
            case KnightCapability.PurviewDlpPolicies:
                foreach (var e in Each(items))
                    yield return KnightTenantConfiguration.Document(Id(e), Str(e, "name"), new PurviewDlpPolicy(Id(e), Str(e, "name"),
                        Str(e, "mode"), Bool(e, "enabled"), Strings(e, "workloads"), Strings(e, "exchangeLocation"),
                        Strings(e, "sharePointLocation"), Strings(e, "oneDriveLocation"), Strings(e, "teamsLocation"),
                        Strings(e, "enforcementPlanes"), Strings(e, "applicationLocations")));
                break;
            case KnightCapability.PurviewLabelPolicies:
                foreach (var e in Each(items))
                    yield return KnightTenantConfiguration.Document(Id(e), Str(e, "name"), new PurviewLabelPolicy(Id(e), Str(e, "name"),
                        Bool(e, "enabled"), Str(e, "mode"), Strings(e, "labels"), Int(e, "locationCount") ?? 0));
                break;
        }
    }
}
