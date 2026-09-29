using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight.Exchange;
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
    private readonly ILogger<KnightIdentityPostureConnector>? _log;

    public KnightIdentityPostureConnector(
        IConnectorSecretProtector protector,
        IEntraGraphClient graph,
        ITeamsTokenClient teamsTokens,
        ITeamsAdminReader teamsReader,
        IExchangeTokenClient exchangeTokens,
        IExchangeAdminReader exchangeReader,
        ILogger<KnightIdentityPostureConnector>? log = null)
    {
        _protector = protector;
        _graph = graph;
        _teamsTokens = teamsTokens;
        _teamsReader = teamsReader;
        _exchangeTokens = exchangeTokens;
        _exchangeReader = exchangeReader;
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
        var entra = await ProbeEntraAsync(credentials, ct);
        var teams = await ProbeTeamsAsync(credentials, ct);
        var exchange = await ProbeExchangeAsync(credentials, ct);

        var probes = new[] { entra, teams, exchange };
        var status = probes.All(p => p.Ok) ? ConnectorStatus.Healthy
            : probes.Any(p => p.Ok) ? ConnectorStatus.Degraded
            : ConnectorStatus.Failed;

        return new ConnectorHealth(status, string.Join("\n", probes.Select(p => p.Message)));
    }

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
                || string.IsNullOrWhiteSpace(s.ClientSecret))
                return null;
            return new GraphCredentials(s.TenantIdValue!, s.ClientId!, s.ClientSecret!);
        }
        catch (Exception ex)
        {
            // Segredo ilegível/adulterado = nada testável (fail-closed). Nada sensível vai ao log.
            _log?.LogWarning(ex, "Configuração do conector Microsoft ilegível; teste de conexão não pôde decifrar as credenciais.");
            return null;
        }
    }

    private sealed record MicrosoftSettings(
        string? TenantId = null, string? AzureTenantId = null, string? ClientId = null, string? ClientSecret = null)
    {
        public string? TenantIdValue => !string.IsNullOrWhiteSpace(TenantId) ? TenantId : AzureTenantId;
    }

    /// <summary>Record imprime todas as propriedades no ToString(): aqui isso seria vazar o segredo de cliente.</summary>
    private sealed record GraphCredentials(string AzureTenantId, string ClientId, string ClientSecret) : IMicrosoftGraphCredentials
    {
        public override string ToString() =>
            $"GraphCredentials {{ AzureTenantId = {AzureTenantId}, ClientId = {ClientId}, ClientSecret = *** }}";
    }

    // ---- Sondas por fonte -------------------------------------------------------------------------------

    private readonly record struct Probe(bool Ok, string Message);

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

        try
        {
            var output = await _teamsReader.TestConnectionAsync(credentials, ct);
            if (!output.Connected)
                return new Probe(false,
                    $"{label}: {ConnectionFailureReason(output.ConnectionErrorCategory)} Confira o papel Leitor "
                    + "do Teams (ou Leitor Global) atribuído à aplicação.");

            var probe = output.Reads.FirstOrDefault();
            if (probe is null || !probe.Ok)
                return new Probe(false,
                    $"{label}: conexão estabelecida, mas a leitura de verificação "
                    + $"({probe?.Command ?? "Get-CsTeamsClientConfiguration"}) foi recusada. Confira o papel "
                    + "Leitor do Teams (ou Leitor Global) atribuído à aplicação.");

            var suffix = output.Runtime.Module is { Length: > 0 } m ? $" (módulo Teams PowerShell {m})" : "";
            return new Probe(true, $"{label}: credencial aceita, conexão estabelecida e leitura confirmada{suffix}.");
        }
        catch (TeamsAdminTransportException)
        {
            return new Probe(false,
                $"{label}: o adaptador de coleta não pôde ser executado neste ambiente. Nenhuma conclusão sobre "
                + "a credencial é possível.");
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

        try
        {
            var output = await _exchangeReader.TestConnectionAsync(credentials, ct);
            if (!output.Connected)
                return new Probe(false,
                    $"{label}: {ConnectionFailureReason(output.ConnectionErrorCategory)} Confira "
                    + "Exchange.ManageAsApp, o papel de diretório Leitor Global e se o serviço aceita o método de "
                    + "autenticação usado (token obtido por segredo de cliente — sem confirmação documental de que "
                    + "o serviço o aceite).");

            var probe = output.Reads.FirstOrDefault();
            if (probe is null || !probe.Ok)
                return new Probe(false,
                    $"{label}: conexão estabelecida, mas a leitura de verificação "
                    + $"({probe?.Command ?? "Get-OrganizationConfig"}) foi recusada. Confira o papel de diretório "
                    + "Leitor Global atribuído à aplicação.");

            var suffix = output.Runtime.Module is { Length: > 0 } m ? $" (módulo Exchange Online PowerShell {m})" : "";
            return new Probe(true,
                $"{label}: credencial aceita, conexão estabelecida e leitura confirmada{suffix}. Esta é a PRIMEIRA "
                + "verificação real de que o método por segredo de cliente funciona neste locatário.");
        }
        catch (ExchangeAdminTransportException)
        {
            return new Probe(false,
                $"{label}: o adaptador de coleta não pôde ser executado neste ambiente. Nenhuma conclusão sobre "
                + "a credencial é possível.");
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
    /// Mensagem da recusa de CONEXÃO montada pela CATEGORIA — nunca atribui uma causa específica a uma recusa
    /// ambígua, mesma regra dos coletores completos. <paramref name="category"/> vem do adaptador, no mesmo
    /// vocabulário de <see cref="KnightCapabilityOutcome"/>.
    /// </summary>
    private static string ConnectionFailureReason(string? category) => category switch
    {
        nameof(KnightCapabilityOutcome.AuthenticationFailure) => "A conexão falhou na autenticação.",
        nameof(KnightCapabilityOutcome.InsufficientPermission) =>
            "A conexão foi recusada por autorização, e a recusa não identifica a causa.",
        nameof(KnightCapabilityOutcome.Throttled) => "O serviço aplicou limite de taxa ao estabelecer a conexão.",
        nameof(KnightCapabilityOutcome.LimitedByLicense) => "A conexão foi recusada por licença do locatário.",
        _ => "A conexão não foi estabelecida, e a recusa não identifica a causa.",
    };
}
