using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight.Exchange;
using AegisScore.Connectors.Microsoft.Knight.Protection;
using AegisScore.Connectors.Microsoft.Knight.Services;
using AegisScore.Connectors.Microsoft.Knight.Teams;
using AegisScore.Domain;

namespace AegisScore.Connectors.Microsoft.Knight;

/// <summary>
/// [AEGIS-KNIGHT-ACCESS-01] "Testar conexão" do conector Microsoft/IdentityPosture — a credencial que alimenta as
/// TRÊS fontes do AEGIS KNIGHT (Entra ID, Teams, Exchange Online).
///
/// <para><b>Por que este arquivo existe.</b> Antes dele, o registro de conectores (<see cref="IConnectorRegistry"/>)
/// não tinha NENHUM <see cref="IEvidenceConnector"/> para (Microsoft, IdentityPosture): clicar em "Testar conexão"
/// nessa linha da tela de Integrações chegava a <c>ConnectorsController.Test</c>, não encontrava adaptador e
/// devolvia 501 — sem nenhuma informação sobre qual das três fontes funciona. Esta classe fecha essa lacuna com uma
/// verificação RÁPIDA e REAL de cada fonte, sem depender de disparar uma sincronização completa (que já existe, é
/// mais lenta e grava uma avaliação).</para>
///
/// <para><b>Por que decifra o segredo aqui, em vez de usar <c>IKnightSourceConfigurationProvider</c>.</b> Essa
/// abstração vive em Application e a implementação real depende do <c>AegisScoreDbContext</c> — uma dependência de
/// Infrastructure que <c>AddMicrosoftConnectors</c> (este projeto) não registra e não deve passar a registrar só
/// para este conector. As três fontes do KNIGHT reusam o MESMO registro de conector (mesmíssimo <c>EncryptedSettings</c>
/// — ver <see cref="KnightSourceConfigurationProvider"/>), então decifrar uma vez aqui, do jeito que
/// <see cref="MicrosoftIntuneDevicePostureConnector"/> e <see cref="MicrosoftSecureScoreConnector"/> já fazem, evita
/// a dependência cruzada de camada sem duplicar autoridade nenhuma sobre o segredo.</para>
///
/// <para><b>O que "rápida" significa aqui, e o que ela NÃO troca.</b> A emissão de um token nunca é, sozinha,
/// tratada como "fonte operacional": ela só prova que a APLICAÇÃO foi autenticada, não que o serviço aceita o
/// método de autenticação ou que a leitura está autorizada. Por isso cada fonte é testada por uma CONEXÃO E LEITURA
/// reais — a mesma que a sincronização usa —, restrita a UM comando representativo por fonte, em vez das seis
/// (Teams) ou doze (Exchange) capacidades completas. Uma recusa AMBÍGUA nunca é traduzida numa causa específica: o
/// texto enumera as verificações pertinentes, na mesma convenção já usada pelos coletores completos
/// (<see cref="TeamsKnightCollector"/>, <see cref="ExchangeKnightCollector"/>).</para>
///
/// <para><b>Para o Exchange Online em particular:</b> a aceitação de um token obtido por segredo de cliente em
/// <c>Connect-ExchangeOnline -AccessToken</c> era, até aqui, uma aposta fundamentada, nunca testada contra um
/// locatário real (ver <see cref="ExchangeTokenClient"/>). Este teste é a PRIMEIRA verificação real disponível
/// dessa aposta, assim que a aplicação for provisionada — sem exigir uma sincronização completa nem gravar
/// avaliação nenhuma.</para>
///
/// <para><b>O que esta classe NÃO faz:</b> não persiste nada (nenhum <c>KnightAssessmentRun</c>, nenhum documento de
/// configuração); não substitui a sincronização completa, que continua sendo a única fonte de verdade do relatório
/// KNIGHT — só ela cobre as demais capacidades de cada fonte.</para>
/// </summary>
public sealed class KnightIdentityPostureConnector : IEvidenceConnector
{
    private const string OrganizationProbeUrl = "organization?$select=verifiedDomains";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IConnectorSecretProtector _protector;
    private readonly IEntraGraphClient _graph;
    private readonly ITeamsTokenClient _teamsTokens;
    private readonly ITeamsAdminReader _teamsReader;
    private readonly IExchangeTokenClient _exchangeTokens;
    private readonly IExchangeAdminReader _exchangeReader;
    private readonly IMicrosoftAppTokenClient? _appTokens;
    private readonly IMicrosoftRestClient? _rest;
    private readonly IProtectionAdminReader? _protection;
    private readonly ILogger<KnightIdentityPostureConnector>? _log;

    public KnightIdentityPostureConnector(
        IConnectorSecretProtector protector,
        IEntraGraphClient graph,
        ITeamsTokenClient teamsTokens,
        ITeamsAdminReader teamsReader,
        IExchangeTokenClient exchangeTokens,
        IExchangeAdminReader exchangeReader,
        ILogger<KnightIdentityPostureConnector>? log = null,
        IMicrosoftAppTokenClient? appTokens = null,
        IMicrosoftRestClient? rest = null,
        IProtectionAdminReader? protection = null)
    {
        _protector = protector;
        _graph = graph;
        _teamsTokens = teamsTokens;
        _teamsReader = teamsReader;
        _exchangeTokens = exchangeTokens;
        _exchangeReader = exchangeReader;
        _appTokens = appTokens;
        _rest = rest;
        _protection = protection;
        _log = log;
    }

    public ConnectorProvider Provider => ConnectorProvider.Microsoft;
    public ConnectorCapability Capability => ConnectorCapability.IdentityPosture;

    public async Task<ConnectorHealth> TestAsync(ConnectorConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        var credentials = DecryptCredentials(config);
        if (credentials is null)
            return new ConnectorHealth(ConnectorStatus.Failed,
                "Conector sem credenciais legíveis: nenhuma das três fontes pôde ser testada.");

        // Sequencial, não paralelo: Teams e Exchange sobem o PRÓPRIO processo PowerShell descartável cada um.
        // Testá-los ao mesmo tempo dobraria a carga de processo por clique sem reduzir o tempo total de forma que
        // valha a complexidade — um clique em "Testar conexão" já é uma ação deliberada do operador.
        var probes = new List<Probe>
        {
            await ProbeEntraAsync(credentials, ct),
            await ProbeTeamsAsync(credentials, ct),
            await ProbeExchangeAsync(credentials, ct),
        };
        // [AEGIS-KNIGHT-COVERAGE-04] As fontes seguintes usam a MESMA credencial; cada uma com UMA leitura real.
        if (_protection is not null)
        {
            probes.Add(await ProbeProtectionAsync(credentials, ProtectionAdminRequest.Defender, ct));
            probes.Add(await ProbeProtectionAsync(credentials, ProtectionAdminRequest.Purview, ct));
        }
        probes.Add(await ProbeGraphReadAsync(credentials, "SharePoint e OneDrive (Microsoft Graph)",
            "admin/sharepoint/settings", "SharePointTenantSettings.Read.All", ct));
        probes.Add(await ProbeSharePointAdminAsync(credentials, ct));
        probes.Add(await ProbeGraphReadAsync(credentials, "Microsoft Intune",
            "deviceManagement?$select=settings", "DeviceManagementConfiguration.Read.All", ct));
        if (_appTokens is not null && _rest is not null)
            probes.Add(await ProbeRestAsync(credentials, "Microsoft Fabric", FabricKnightCollector.Scope, FabricKnightCollector.TenantSettingsUrl,
                "a configuração “entidades de serviço podem usar as APIs de administração somente leitura” e o grupo de segurança da aplicação", ct));
        if (_appTokens is not null && _rest is not null)
            probes.Add(await ProbeAzureAsync(credentials, ct));
        // Uma leitura NÃO TENTADA (ex.: a API administrativa do SharePoint sem certificado) não conta como falha nem
        // como sucesso: o estado é o das leituras tentadas, e a linha dela diz o que falta.
        var attempted = probes.Where(p => !p.Skipped).ToList();
        var status = attempted.All(p => p.Ok) ? ConnectorStatus.Healthy
            : attempted.Any(p => p.Ok) ? ConnectorStatus.Degraded
            : ConnectorStatus.Failed;

        var lines = probes.Select(p => p.Message).ToList();
        lines.Add(credentials.ClientCertificate is null
            ? "Autenticação usada: segredo de cliente."
            : "Autenticação usada: certificado da aplicação (asserção assinada).");
        if (probes.Any(p => p.Ok))
            lines.Add(ScopeOfVerificationNote);
        return new ConnectorHealth(status, string.Join("\n", lines));
    }

    private const string ScopeOfVerificationNote =
        "Este teste confirma apenas a conexão e a leitura de verificação de cada fonte; as demais leituras "
        + "(e portanto a cobertura do assessment) só são verificadas pela sincronização.";

    // ---- IEvidenceConnector: ZERO sinais -------------------------------------------------------------

    /// <summary>
    /// NUNCA emite sinal e NUNCA é chamado pela sincronização: o AEGIS KNIGHT tem seu PRÓPRIO caminho de coleta
    /// (<see cref="IKnightSyncRequests"/> → <see cref="IKnightCollector"/>), resolvido ANTES deste método em
    /// <c>ConnectorsController.Sync</c> sempre que o conector alimenta alguma fonte do KNIGHT. A implementação
    /// existe só para o conector participar do registro/ciclo de vida (teste).
    /// </summary>
#pragma warning disable CS1998   // sem await: a ausência de sinais é o comportamento correto, não um esquecimento
    public async IAsyncEnumerable<EvidenceSignal> CollectAsync(
        ConnectorConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        yield break;
    }
#pragma warning restore CS1998

    // ---- Credenciais (mesmo JSON de configuração dos três coletores KNIGHT) ---------------------------

    private GraphCredentials? DecryptCredentials(ConnectorConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.EncryptedSettings)) return null;
        try
        {
            var json = _protector.Unprotect(config.EncryptedSettings);
            var s = JsonSerializer.Deserialize<MicrosoftSettings>(json, JsonOpts);
            if (s is null
                || string.IsNullOrWhiteSpace(s.TenantIdValue)
                || string.IsNullOrWhiteSpace(s.ClientId)
                || (string.IsNullOrWhiteSpace(s.ClientSecret) && string.IsNullOrWhiteSpace(s.CertificatePfxBase64)))
                return null;
            var certificate = string.IsNullOrWhiteSpace(s.CertificatePfxBase64)
                ? null
                : new MicrosoftClientCertificate(s.CertificatePfxBase64!.Trim(), s.CertificatePassword);
            return new GraphCredentials(s.TenantIdValue!, s.ClientId!, s.ClientSecret ?? "", certificate);
        }
        catch (Exception ex)
        {
            // Segredo ilegível/adulterado = nada testável (fail-closed). Nada sensível vai ao log.
            _log?.LogWarning(ex, "Configuração do conector Microsoft ilegível; teste de conexão não pôde decifrar as credenciais.");
            return null;
        }
    }

    private sealed record MicrosoftSettings(
        string? TenantId = null, string? AzureTenantId = null, string? ClientId = null, string? ClientSecret = null,
        string? CertificatePfxBase64 = null, string? CertificatePassword = null)
    {
        public string? TenantIdValue => !string.IsNullOrWhiteSpace(TenantId) ? TenantId : AzureTenantId;

        public override string ToString() => "MicrosoftSettings { *** }";
    }

    /// <summary>Record imprime todas as propriedades no ToString(): aqui isso seria vazar o segredo de cliente.</summary>
    private sealed record GraphCredentials(string AzureTenantId, string ClientId, string ClientSecret, MicrosoftClientCertificate? ClientCertificate)
        : IMicrosoftGraphCredentials
    {
        public override string ToString() =>
            $"GraphCredentials {{ AzureTenantId = {AzureTenantId}, ClientId = {ClientId}, ClientSecret = ***, ClientCertificate = *** }}";
    }

    // ---- Sondas por fonte -------------------------------------------------------------------------------

    private readonly record struct Probe(bool Ok, string Message, bool Skipped = false);

    private async Task<Probe> ProbeEntraAsync(IMicrosoftGraphCredentials cfg, CancellationToken ct)
    {
        const string label = "Microsoft Entra ID";
        try
        {
            var token = await _graph.AcquireTokenAsync(cfg, ct);
            await _graph.GetJsonAsync(token, cfg, OrganizationProbeUrl, ct);
            return new Probe(true, $"{label}: credencial aceita e leitura de organização confirmada.");
        }
        catch (EntraGraphException ex)
        {
            return new Probe(false, $"{label}: {GraphFailureReason(ex)}");
        }
        catch (Exception ex) when (!IsRequestedCancellation(ex, ct))
        {
            return FailedProbe(label, ex);
        }
    }

    private async Task<Probe> ProbeTeamsAsync(IMicrosoftGraphCredentials cfg, CancellationToken ct)
    {
        const string label = "Microsoft Teams";

        TeamsAdminCredentials credentials;
        try
        {
            credentials = await _teamsTokens.AcquireAsync(cfg, ct);
        }
        catch (EntraGraphException ex)
        {
            return new Probe(false, $"{label}: {GraphFailureReason(ex)}");
        }
        catch (Exception ex) when (!IsRequestedCancellation(ex, ct))
        {
            return FailedProbe(label, ex);
        }

        try
        {
            var output = await _teamsReader.TestConnectionAsync(credentials, ct);
            if (!output.Connected)
                return new Probe(false, $"{label}: {TeamsConnectionFailureReason(output.ConnectionErrorCategory)}");

            var probe = output.Reads.FirstOrDefault();
            if (probe is null || !probe.Ok)
                return new Probe(false,
                    $"{label}: conexão estabelecida, mas a leitura de verificação "
                    + $"({probe?.Command ?? "Get-CsTeamsClientConfiguration"}) foi "
                    + TeamsReadFailureReason(probe?.ErrorCategory));

            var suffix = output.Runtime.Module is { Length: > 0 } m ? $" (módulo Teams PowerShell {m})" : "";
            return new Probe(true, $"{label}: credencial aceita, conexão estabelecida e leitura confirmada{suffix}.");
        }
        catch (TeamsAdminTransportException)
        {
            return new Probe(false,
                $"{label}: o adaptador de coleta não pôde ser executado neste ambiente. Nenhuma conclusão sobre "
                + "a credencial é possível.");
        }
        catch (Exception ex) when (!IsRequestedCancellation(ex, ct))
        {
            return FailedProbe(label, ex);
        }
    }

    private async Task<Probe> ProbeExchangeAsync(IMicrosoftGraphCredentials cfg, CancellationToken ct)
    {
        const string label = "Exchange Online";

        ExchangeAdminCredentials credentials;
        try
        {
            credentials = await _exchangeTokens.AcquireAsync(cfg, ct);
        }
        catch (EntraGraphException ex)
        {
            return new Probe(false, $"{label}: {GraphFailureReason(ex)}");
        }
        catch (Exception ex) when (!IsRequestedCancellation(ex, ct))
        {
            return FailedProbe(label, ex);
        }

        try
        {
            var output = await _exchangeReader.TestConnectionAsync(credentials, ct);
            if (!output.Connected)
                return new Probe(false, $"{label}: {ExchangeConnectionFailureReason(output.ConnectionErrorCategory, cfg.ClientCertificate is not null)}");

            var probe = output.Reads.FirstOrDefault();
            if (probe is null || !probe.Ok)
                return new Probe(false,
                    $"{label}: conexão estabelecida, mas a leitura de verificação "
                    + $"({probe?.Command ?? "Get-OrganizationConfig"}) foi "
                    + ExchangeReadFailureReason(probe?.ErrorCategory));

            var suffix = output.Runtime.Module is { Length: > 0 } m ? $" (módulo Exchange Online PowerShell {m})" : "";
            return new Probe(true, cfg.ClientCertificate is null
                ? $"{label}: credencial aceita, conexão estabelecida e leitura confirmada{suffix}. Esta é a PRIMEIRA "
                  + "verificação real de que o método por segredo de cliente funciona neste locatário."
                : $"{label}: credencial aceita por certificado, conexão estabelecida e leitura confirmada{suffix}.");
        }
        catch (ExchangeAdminTransportException)
        {
            return new Probe(false,
                $"{label}: o adaptador de coleta não pôde ser executado neste ambiente. Nenhuma conclusão sobre "
                + "a credencial é possível.");
        }
        catch (Exception ex) when (!IsRequestedCancellation(ex, ct))
        {
            return FailedProbe(label, ex);
        }
    }

    /// <summary>[AEGIS-KNIGHT-COVERAGE-04] Uma leitura do Microsoft Graph que destrava uma fonte nova.</summary>
    private async Task<Probe> ProbeGraphReadAsync(IMicrosoftGraphCredentials cfg, string label, string url, string permission, CancellationToken ct)
    {
        try
        {
            var token = await _graph.AcquireTokenAsync(cfg, ct);
            await _graph.GetJsonAsync(token, cfg, url, ct);
            return new Probe(true, $"{label}: leitura de verificação confirmada.");
        }
        catch (EntraGraphException ex) when (ex.Kind == EntraGraphErrorKind.InsufficientPermission)
        {
            return new Probe(false, $"{label}: autorização recusada — confira {permission} (aplicativo) e o consentimento do administrador.");
        }
        catch (EntraGraphException ex)
        {
            return new Probe(false, $"{label}: {GraphFailureReason(ex)}");
        }
        catch (Exception ex) when (!IsRequestedCancellation(ex, ct))
        {
            return FailedProbe(label, ex);
        }
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] A API administrativa do SharePoint: só com certificado. Sem ele, o teste diz que a
    /// leitura não foi tentada — não é uma falha da credencial.
    /// </summary>
    private async Task<Probe> ProbeSharePointAdminAsync(IMicrosoftGraphCredentials cfg, CancellationToken ct)
    {
        const string label = "SharePoint e OneDrive (API administrativa)";
        if (cfg.ClientCertificate is null)
            return new Probe(false, $"{label}: não tentada — exige certificado da aplicação; sem ele, 8 controles do SharePoint e OneDrive ficam não avaliados.", Skipped: true);
        if (_appTokens is null || _rest is null)
            return new Probe(false, $"{label}: não verificável nesta instalação.", Skipped: true);
        try
        {
            var token = await _graph.AcquireTokenAsync(cfg, ct);
            var org = await _graph.GetJsonAsync(token, cfg, OrganizationProbeUrl, ct);
            string? initial = null;
            if (org.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array)
                foreach (var o in v.EnumerateArray())
                    if (o.TryGetProperty("verifiedDomains", out var ds) && ds.ValueKind == JsonValueKind.Array)
                        foreach (var d in ds.EnumerateArray())
                            if (d.TryGetProperty("isInitial", out var i) && i.ValueKind == JsonValueKind.True && d.TryGetProperty("name", out var n))
                                initial = n.GetString();
            var host = SharePointKnightCollector.AdminHost(initial);
            if (host is null)
                return new Probe(false, $"{label}: o domínio inicial do locatário não permite derivar o host de administração.");
            return await ProbeRestAsync(cfg, label, $"https://{host}/.default", $"https://{host}/_api/SPO.Tenant",
                "Sites.FullControl.All na API do SharePoint (aplicativo) e o certificado registrado na aplicação", ct);
        }
        catch (EntraGraphException ex)
        {
            return new Probe(false, $"{label}: {GraphFailureReason(ex)}");
        }
        catch (Exception ex) when (!IsRequestedCancellation(ex, ct))
        {
            return FailedProbe(label, ex);
        }
    }

    /// <summary>[AEGIS-KNIGHT-COVERAGE-04] Token para o recurso e UMA leitura REST (Fabric, administração do SharePoint).</summary>
    private async Task<Probe> ProbeRestAsync(IMicrosoftGraphCredentials cfg, string label, string scope, string url, string requirement, CancellationToken ct)
    {
        try
        {
            var token = await _appTokens!.AcquireAsync(cfg, scope, ct);
            await _rest!.GetAsync(token, url, ct);
            return new Probe(true, $"{label}: leitura de verificação confirmada.");
        }
        catch (EntraGraphException ex) when (ex.Kind == EntraGraphErrorKind.InsufficientPermission)
        {
            return new Probe(false, $"{label}: autorização recusada, e a recusa não identifica a causa — confira {requirement}.");
        }
        catch (EntraGraphException ex)
        {
            return new Probe(false, $"{label}: {GraphFailureReason(ex)}");
        }
        catch (MicrosoftCertificateException ex)
        {
            return new Probe(false, $"{label}: {ex.Message}");
        }
        catch (Exception ex) when (!IsRequestedCancellation(ex, ct))
        {
            return FailedProbe(label, ex);
        }
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] Azure: token do Resource Manager e a listagem das assinaturas visíveis. Conectar sem
    /// enxergar nenhuma assinatura não é sucesso — é a falta do papel Leitor, e a linha diz isso.
    /// </summary>
    private async Task<Probe> ProbeAzureAsync(IMicrosoftGraphCredentials cfg, CancellationToken ct)
    {
        const string label = "Microsoft Azure";
        try
        {
            var token = await _appTokens!.AcquireAsync(cfg, Azure.AzureArmPlan.ArmScope, ct);
            var n = 0;
            await foreach (var _ in _rest!.GetPagedAsync(token,
                               $"{Azure.AzureArmPlan.ArmHost}/subscriptions?api-version={Azure.AzureArmPlan.SubscriptionsApi}", ct))
                n++;
            return n == 0
                ? new Probe(false, $"{label}: conexão aceita, mas nenhuma assinatura visível — atribua o papel Leitor do Azure RBAC à aplicação nas assinaturas a avaliar.")
                : new Probe(true, $"{label}: leitura de verificação confirmada — {n} assinatura(s) visível(is) à aplicação.");
        }
        catch (EntraGraphException ex)
        {
            return new Probe(false, $"{label}: {GraphFailureReason(ex)}");
        }
        catch (MicrosoftCertificateException ex)
        {
            return new Probe(false, $"{label}: {ex.Message}");
        }
        catch (Exception ex) when (!IsRequestedCancellation(ex, ct))
        {
            return FailedProbe(label, ex);
        }
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] Defender para Office 365 (sessão do Exchange Online) ou Purview (sessões do Exchange
    /// Online e do Security &amp; Compliance), pelo modo de teste do adaptador: conexão e UMA leitura.
    /// </summary>
    private async Task<Probe> ProbeProtectionAsync(IMicrosoftGraphCredentials cfg, string profile, CancellationToken ct)
    {
        var purview = profile == ProtectionAdminRequest.Purview;
        var label = purview ? "Microsoft Purview" : "Microsoft Defender para Office 365";
        try
        {
            var exo = await _exchangeTokens.AcquireAsync(cfg, ct);
            string? complianceToken = null;
            if (purview && _appTokens is not null)
                complianceToken = await _appTokens.AcquireAsync(cfg, ProtectionCollectorBase.ComplianceScope, ct);
            var output = await _protection!.TestConnectionAsync(new ProtectionAdminRequest(profile, exo.Organization, exo.AccessToken, complianceToken), ct);
            if (!output.Exchange.Connected)
                return new Probe(false, $"{label}: {ExchangeConnectionFailureReason(output.Exchange.ConnectionErrorCategory, cfg.ClientCertificate is not null)}");
            if (purview && !output.ComplianceConnected)
                return new Probe(false, $"{label}: {ProtectionCollectorBase.ConnectionReason(
                    ProtectionCollectorBase.ParseOutcome(output.ComplianceErrorCategory) ?? KnightCapabilityOutcome.AuthenticationFailure,
                    compliance: true, certificate: cfg.ClientCertificate is not null)}");
            var failed = output.Exchange.Reads.FirstOrDefault(r => !r.Ok);
            if (failed is not null)
                return new Probe(false, $"{label}: conexão estabelecida, mas a leitura de verificação ({failed.Command}) foi {ExchangeReadFailureReason(failed.ErrorCategory)}");
            return new Probe(true, $"{label}: conexão estabelecida e leitura de verificação confirmada.");
        }
        catch (EntraGraphException ex)
        {
            return new Probe(false, $"{label}: {GraphFailureReason(ex)}");
        }
        catch (ExchangeAdminTransportException)
        {
            return new Probe(false, $"{label}: o adaptador de coleta não pôde ser executado neste ambiente. Nenhuma conclusão sobre a credencial é possível.");
        }
        catch (MicrosoftCertificateException ex)
        {
            return new Probe(false, $"{label}: {ex.Message}");
        }
        catch (Exception ex) when (!IsRequestedCancellation(ex, ct))
        {
            return FailedProbe(label, ex);
        }
    }

    private static string GraphFailureReason(EntraGraphException ex) => ex.Kind switch
    {
        EntraGraphErrorKind.AuthFailure =>
            "falha de autenticação junto ao Microsoft Graph — confira o segredo da aplicação e o tenant informado.",
        EntraGraphErrorKind.InsufficientPermission =>
            "autorização recusada pelo Microsoft Graph — confira Organization.Read.All e o consentimento do administrador.",
        EntraGraphErrorKind.Throttled =>
            "limite de taxa do Microsoft Graph ao verificar a credencial; tente novamente em instantes.",
        _ => "o Microsoft Graph não respondeu à verificação.",
    };

    /// <summary>
    /// Mensagem da recusa de CONEXÃO do Teams pela CATEGORIA. Só orienta conferir o papel de diretório quando a
    /// categoria É autorização — apontar isso para uma recusa por licença ou limite de taxa seria inventar uma
    /// causa que a resposta do serviço não confirma.
    /// </summary>
    private static string TeamsConnectionFailureReason(string? category) => category switch
    {
        nameof(KnightCapabilityOutcome.AuthenticationFailure) =>
            "A conexão falhou na autenticação — confira o segredo da aplicação e os dois tokens emitidos.",
        nameof(KnightCapabilityOutcome.InsufficientPermission) =>
            "A conexão foi recusada por autorização — confira o papel Leitor do Teams (ou Leitor Global) atribuído à aplicação.",
        nameof(KnightCapabilityOutcome.Throttled) =>
            "O serviço aplicou limite de taxa ao estabelecer a conexão; não é uma questão de permissão ou papel.",
        nameof(KnightCapabilityOutcome.LimitedByLicense) =>
            "A conexão foi recusada por licença do locatário; não é uma questão de permissão ou papel.",
        _ => "A conexão não foi estabelecida, e a recusa não identifica a causa — confira permissão, papel, domínio e método de autenticação.",
    };

    /// <summary>Mesma regra de <see cref="TeamsConnectionFailureReason"/> para uma LEITURA (não a conexão) recusada.</summary>
    private static string TeamsReadFailureReason(string? category) => category switch
    {
        nameof(KnightCapabilityOutcome.InsufficientPermission) =>
            "recusada por autorização. Confira o papel Leitor do Teams (ou Leitor Global) atribuído à aplicação.",
        nameof(KnightCapabilityOutcome.LimitedByLicense) =>
            "recusada por licença do locatário; não é resolvida concedendo mais papel.",
        nameof(KnightCapabilityOutcome.Throttled) =>
            "recusada por limite de taxa do serviço; tente novamente em instantes.",
        nameof(KnightCapabilityOutcome.Unavailable) =>
            "recusada porque o serviço reportou esta capacidade como indisponível neste locatário.",
        _ => "recusada, e a causa não pôde ser determinada a partir da resposta do serviço.",
    };

    /// <summary>
    /// Recusa de CONEXÃO do Exchange pela CATEGORIA. Diferente do Teams: para <c>AuthenticationFailure</c>,
    /// <c>InsufficientPermission</c> e categoria desconhecida, o Exchange PERMANECE no enquadramento ambíguo
    /// ("confira tudo"), porque o método por segredo de cliente ainda não foi confirmado contra um locatário real
    /// — uma recusa categorizada pelo script como "autorização" pode, na verdade, ser o serviço rejeitando o
    /// MÉTODO, e afirmar que é só permissão inventaria a causa a partir do sintoma (ver
    /// <see cref="ExchangeTokenClient"/>). Só <c>Throttled</c> e <c>LimitedByLicense</c> são mecanicamente
    /// inequívocos o bastante para não precisar dessa cautela extra.
    /// </summary>
    private static string ExchangeConnectionFailureReason(string? category, bool certificate = false) => category switch
    {
        nameof(KnightCapabilityOutcome.Throttled) =>
            "O serviço aplicou limite de taxa ao estabelecer a conexão; não é uma questão de permissão, papel ou método de autenticação.",
        nameof(KnightCapabilityOutcome.LimitedByLicense) =>
            "A conexão foi recusada por licença do locatário; não é uma questão de permissão, papel ou método de autenticação.",
        _ when certificate =>
            "A conexão foi recusada, e a recusa não identifica a causa com segurança — confira Exchange.ManageAsApp, o "
            + "papel Leitor Global, o domínio de organização e se o certificado enviado é o mesmo registrado na aplicação.",
        _ => "A conexão foi recusada, e a recusa não identifica a causa com segurança — confira Exchange.ManageAsApp, o "
            + "papel Leitor Global e se o serviço aceita o método de autenticação usado (token obtido por segredo "
            + "de cliente — sem confirmação documental de que o serviço o aceite).",
    };

    /// <summary>Mesma regra de <see cref="TeamsReadFailureReason"/>, com o vocabulário do Exchange Online.</summary>
    private static string ExchangeReadFailureReason(string? category) => category switch
    {
        nameof(KnightCapabilityOutcome.InsufficientPermission) =>
            "recusada por autorização. Confira o papel de diretório Leitor Global atribuído à aplicação.",
        nameof(KnightCapabilityOutcome.LimitedByLicense) =>
            "recusada por licença do locatário; não é resolvida concedendo mais papel.",
        nameof(KnightCapabilityOutcome.Throttled) =>
            "recusada por limite de taxa do serviço; tente novamente em instantes.",
        nameof(KnightCapabilityOutcome.Unavailable) =>
            "recusada porque o serviço reportou esta capacidade como indisponível neste locatário.",
        _ => "recusada, e a causa não pôde ser determinada a partir da resposta do serviço.",
    };

    private const string TransportFailureMessage =
        "a verificação não pôde ser concluída por falha de rede ou tempo limite ao contatar o serviço; causa não determinada.";

    private const string UnexpectedFailureMessage =
        "a verificação terminou com uma falha inesperada do AEGIS; nenhuma conclusão sobre a credencial, a permissão "
        + "ou o serviço é possível a partir dela.";

    /// <summary>Cancelamento pedido pelo chamador: sempre se propaga, nunca vira um resultado "Failed".</summary>
    private static bool IsRequestedCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;

    /// <summary>
    /// Falhas CONHECIDAS de transporte: erro de rede/DNS/TLS do HttpClient, tempo limite (o do próprio HttpClient
    /// chega como <see cref="OperationCanceledException"/> sem cancelamento do chamador — este método só é
    /// consultado depois de <see cref="IsRequestedCancellation"/> ter excluído o cancelamento real) e E/S.
    /// </summary>
    private static bool IsKnownTransportFailure(Exception ex) =>
        ex is HttpRequestException or TimeoutException or OperationCanceledException or IOException;

    /// <summary>
    /// Resultado seguro de UMA fonte para uma exceção não tipada: transporte conhecido vira "rede ou tempo
    /// limite"; qualquer outra coisa é uma falha interna e recebe mensagem neutra, sem atribuir causa ao
    /// serviço. Só o TIPO da exceção vai ao log — a mensagem de uma exceção inesperada não é conteúdo confiável
    /// para registrar ao lado de uma credencial.
    /// </summary>
    private Probe FailedProbe(string label, Exception ex)
    {
        if (IsKnownTransportFailure(ex))
        {
            _log?.LogWarning("Falha de transporte ao verificar {Label}: {ExceptionType}.", label, ex.GetType().Name);
            return new Probe(false, $"{label}: {TransportFailureMessage}");
        }

        _log?.LogError("Falha inesperada ao verificar {Label}: {ExceptionType}.", label, ex.GetType().Name);
        return new Probe(false, $"{label}: {UnexpectedFailureMessage}");
    }
}
