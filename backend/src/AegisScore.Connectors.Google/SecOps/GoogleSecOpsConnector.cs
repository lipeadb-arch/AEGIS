using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Connectors.Google.Auth;
using AegisScore.Connectors.Google.Cloud;
using AegisScore.Domain;

namespace AegisScore.Connectors.Google.SecOps;

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-01] Autenticação da service account do Google SecOps (Chronicle) — porta TESTÁVEL. Reusa
/// a autoridade COMPARTILHADA <see cref="GoogleServiceAccountTokenSource"/> (mesma validação do conector do Google
/// Cloud): SOMENTE service account oficial, <c>token_uri</c> oficial, sem domain-wide delegation. A ÚNICA diferença é
/// o ESCOPO — <see cref="ChronicleReadonlyScope"/> (somente leitura), preferido ao cloud-platform porque a intenção é
/// leitura. Sem e-mail delegado na assinatura: garantia estrutural de que não há domain-wide delegation.
/// </summary>
public interface IGoogleSecOpsAuthenticator
{
    Task<string> AcquireAccessTokenAsync(string serviceAccountJson, CancellationToken ct);
}

/// <inheritdoc cref="IGoogleSecOpsAuthenticator"/>
public sealed class GoogleSecOpsAuthenticator : IGoogleSecOpsAuthenticator
{
    /// <summary>Escopo OAuth SOMENTE LEITURA oficial do Chronicle/Google SecOps — preferido ao cloud-platform (a intenção é leitura).</summary>
    public const string ChronicleReadonlyScope = "https://www.googleapis.com/auth/chronicle.readonly";

    public async Task<string> AcquireAccessTokenAsync(string serviceAccountJson, CancellationToken ct)
    {
        try
        {
            return await GoogleServiceAccountTokenSource.AcquireAsync(
                serviceAccountJson, new[] { ChronicleReadonlyScope }, ct);
        }
        catch (GoogleCloudApiException)
        {
            // Traduz a falha SANITIZADA da autoridade compartilhada (validação do JSON / troca OAuth) ao vocabulário
            // do SecOps, sem vazar segredo/URL/detalhe. Cancelamento solicitado (OCE) propaga (não é GoogleCloudApiException).
            throw new ChronicleApiException(ChronicleApiErrorKind.AuthFailure,
                "falha de autenticação da service account do Google SecOps");
        }
    }
}

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-01] Conector REAL, SOMENTE LEITURA, do Google SecOps (Chronicle) — a PRIMEIRA fonte de
/// postura de SIEM NÃO-Microsoft, provando na prática que a fundação de SIEM do AEGIS é provider-neutral.
///
/// Duas responsabilidades, SEM tocar a autoridade determinística:
///  • <see cref="IEvidenceConnector"/> — NÃO emite sinais de score (<see cref="CollectAsync"/> é vazio): alertas/casos
///    de SIEM NÃO comprovam que um controle NIST está implementado/eficaz. Nenhum EvidenceSignal, nenhum mapping NIST.
///  • <see cref="ISiemPostureCollector"/> — produz a POSTURA OPERACIONAL PROVIDER-NEUTRAL (fato consultivo) em DUAS
///    dimensões independentes: CASOS (inventário atual via <c>cases.list</c>) e ALERTAS (janela de 30 dias via
///    <c>legacySearchEnterpriseWideAlerts</c>). Só AGREGADOS e INSTANTES — nunca título, descrição, usuário, IP,
///    entidade, comentário ou payload bruto.
///
/// A falha de UMA dimensão NUNCA vira zero: a outra preserva seus agregados e o resultado fica degradado com estado
/// classificado (permissão/throttle/timeout distinguíveis). Se NENHUMA dimensão puder ser coletada, o pull FALHA.
/// FAIL-CLOSED: sem configuração legível → não configurado; localidade fora da allowlist → recusada; segredo nunca
/// registrado. O destino é SEMPRE um host regional oficial (<c>*-chronicle.googleapis.com</c>) — nunca a Backstory API.
/// </summary>
public sealed class GoogleSecOpsConnector : IEvidenceConnector, ISiemPostureCollector
{
    /// <summary>Rótulo estável da fonte — exibido na tela e nos diagnósticos.</summary>
    public const string SourceLabel = "Google SecOps";

    /// <summary>Janela deslizante (dias) da dimensão de ALERTAS. Constante — o usuário não configura período arbitrário.</summary>
    private const int AlertWindowDays = 30;

    // Project id (letras minúsculas/dígitos/hífen) OU project number (dígitos). Segmentos vão escapados na URL de
    // qualquer forma; validar aqui é UX honesta e defesa extra.
    private static readonly Regex ProjectIdRegex = new(@"^[a-z0-9][a-z0-9-]{3,28}[a-z0-9]$|^\d{1,30}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Instance id do SecOps: token seguro (GUID canônico e formatos afins). Escapado na URL de qualquer forma.
    private static readonly Regex InstanceIdRegex = new(@"^[A-Za-z0-9][A-Za-z0-9-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IGoogleSecOpsAuthenticator _auth;
    private readonly IChronicleApiClient _api;
    private readonly IConnectorSecretProtector _protector;
    private readonly ILogger<GoogleSecOpsConnector>? _log;

    public GoogleSecOpsConnector(
        IGoogleSecOpsAuthenticator auth, IChronicleApiClient api,
        IConnectorSecretProtector protector, ILogger<GoogleSecOpsConnector>? log = null)
    {
        _auth = auth;
        _api = api;
        _protector = protector;
        _log = log;
    }

    public ConnectorProvider Provider => ConnectorProvider.Google;
    public ConnectorCapability Capability => ConnectorCapability.Siem;

    // ---- IEvidenceConnector: SEM sinais de score (controles seguem NotEvaluated) -------------------

    public async IAsyncEnumerable<EvidenceSignal> CollectAsync(
        ConnectorConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        // Deliberadamente vazio: alertas/casos de SIEM NÃO satisfazem as fórmulas determinísticas de DE.*/RS.*/RC.*.
        // A postura operacional vem por ISiemPostureCollector (fato consultivo). Nenhum EvidenceSignal, nenhum score.
        await Task.CompletedTask;
        yield break;
    }

    // ---- Teste de conexão: instances.get (não depende de casos nem alertas) ------------------------

    public async Task<ConnectorHealth> TestAsync(ConnectorConfig config, CancellationToken ct)
    {
        try
        {
            var settings = DecryptSettings(config);
            if (settings is null)
                return new ConnectorHealth(ConnectorStatus.Degraded,
                    "Conector não configurado — informe o tipo de autenticação (service account), o project ID, a localidade suportada, o instance ID do SecOps e o JSON da service account.");

            var token = await _auth.AcquireAccessTokenAsync(settings.ServiceAccountJson, ct);

            // instances.get: prova autenticação + permissão de leitura na instância, SEM depender de casos ou alertas.
            await _api.GetInstanceAsync(token, settings.ProjectId, settings.Location, settings.InstanceId, ct);

            return new ConnectorHealth(ConnectorStatus.Healthy,
                "Autenticação e leitura da instância do Google SecOps (Chronicle) confirmadas.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ChronicleApiException ex)
        {
            return ex.Kind switch
            {
                ChronicleApiErrorKind.InsufficientPermission => new ConnectorHealth(ConnectorStatus.Failed,
                    "Permissão insuficiente — conceda à service account acesso somente leitura à instância, aos casos e à pesquisa de alertas do Google SecOps (Chronicle API)."),
                ChronicleApiErrorKind.InstanceNotFound => new ConnectorHealth(ConnectorStatus.Failed,
                    "Instância do Google SecOps não encontrada — verifique o project ID, a localidade e o instance ID."),
                ChronicleApiErrorKind.AuthFailure or ChronicleApiErrorKind.Unauthorized => new ConnectorHealth(ConnectorStatus.Failed,
                    "Falha de autenticação ou configuração inválida — verifique o JSON da service account (oficial, sem domain-wide delegation), o project ID, a localidade e o instance ID."),
                ChronicleApiErrorKind.Throttled => new ConnectorHealth(ConnectorStatus.Degraded,
                    "Throttling da Chronicle API; tente novamente em instantes."),
                ChronicleApiErrorKind.Timeout => new ConnectorHealth(ConnectorStatus.Degraded,
                    "Tempo esgotado ao consultar o Google SecOps."),
                _ => new ConnectorHealth(ConnectorStatus.Failed,
                    "Chronicle API do Google SecOps indisponível para a leitura — verifique a localidade suportada e a disponibilidade da instância."),
            };
        }
    }

    // ---- ISiemPostureCollector: postura operacional (fato consultivo, sem score) -------------------

    public async Task<SiemPostureSnapshot> CollectPostureAsync(ConnectorConfig config, CancellationToken ct)
    {
        var settings = DecryptSettings(config)
            ?? throw new ChronicleApiException(ChronicleApiErrorKind.AuthFailure,
                "conector do Google SecOps sem configuração legível");

        var token = await _auth.AcquireAccessTokenAsync(settings.ServiceAccountJson, ct);
        var now = DateTimeOffset.UtcNow;

        // Duas dimensões INDEPENDENTES: a falha de uma NÃO vira zero na outra. Cada uma captura seu próprio erro
        // classificado (permissão/throttle/timeout permanecem distinguíveis). Cancelamento solicitado propaga.
        var (cases, casesError) = await CollectCasesAsync(token, settings, ct);
        var (alerts, alertsError) = await CollectAlertsAsync(token, settings, now, ct);

        // Se NENHUMA dimensão pôde ser coletada, o pull FALHA (não devolve fotografia vazia). Relança a falha
        // CLASSIFICADA (casos primeiro) para o executor carimbar Failed preservando a natureza do erro.
        var casesUsable = cases.State is SiemCollectionState.Available or SiemCollectionState.Partial;
        var alertsUsable = alerts.State is SiemCollectionState.Available or SiemCollectionState.Partial;
        if (!casesUsable && !alertsUsable)
            throw casesError ?? alertsError
                ?? new ChronicleApiException(ChronicleApiErrorKind.Unavailable, "coleta do Google SecOps indisponível");

        return new SiemPostureSnapshot(SourceLabel, cases, alerts);
    }

    // ---- Dimensão de CASOS (inventário atual) ------------------------------------------------------

    private async Task<(SiemCasePosture Posture, ChronicleApiException? Error)> CollectCasesAsync(
        string token, GoogleSecOpsSettings s, CancellationToken ct)
    {
        try
        {
            var cases = await _api.ListCasesAsync(token, s.ProjectId, s.Location, s.InstanceId, ct);
            return (AggregateCases(cases), null);
        }
        catch (ChronicleApiException ex)
        {
            _log?.LogInformation("Google SecOps: coleta de casos não comprovada ({Kind}).", ex.Kind);
            return (FailedCases(StateFrom(ex)), ex);
        }
    }

    /// <summary>
    /// Agrega os casos como INVENTÁRIO ATUAL (não uma janela temporal — a listagem não garante filtro por
    /// criação/atualização). Só operacional: total, abertos, fechados, distribuição por prioridade (quando fornecida)
    /// e a evidência mais recente por <c>updateTime</c>. Nunca título, descrição, usuário, comentário ou entidade.
    /// </summary>
    private static SiemCasePosture AggregateCases(IReadOnlyList<JsonElement> cases)
    {
        var closed = 0;
        var byPriority = new Dictionary<string, int>(StringComparer.Ordinal);
        DateTimeOffset? last = null;

        foreach (var c in cases)
        {
            if (IsCaseClosed(c))
            {
                closed++;
            }
            else
            {
                var priority = CasePriority(c);
                if (priority is not null)
                    byPriority[priority] = byPriority.GetValueOrDefault(priority) + 1;
            }

            var updated = ReadDate(c, "updateTime");
            if (updated is { } d && (last is null || d > last)) last = d;
        }

        var observed = cases.Count;
        var priorityList = byPriority.Count == 0
            ? null
            : byPriority.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new SiemPriorityCount(kv.Key, kv.Value)).ToList();

        return new SiemCasePosture(
            State: SiemCollectionState.Available,
            Period: SiemPeriodKind.CurrentInventory,
            WindowDays: null,
            IsComplete: true,
            Observed: observed,
            Open: observed - closed,
            New: null,                 // inventário atual não distingue "novo" (sem garantia temporal na listagem)
            Closed: closed,
            OpenHighSeverity: null, OpenMediumSeverity: null, OpenLowSeverity: null, OpenInformationalSeverity: null,
            OpenByPriority: priorityList,
            MeanTimeToCloseHours: null,
            LastEvidenceAt: last);
    }

    private static SiemCasePosture FailedCases(SiemCollectionState state) => new(
        State: state, Period: SiemPeriodKind.CurrentInventory, WindowDays: null, IsComplete: false,
        Observed: null, Open: null, New: null, Closed: null,
        OpenHighSeverity: null, OpenMediumSeverity: null, OpenLowSeverity: null, OpenInformationalSeverity: null,
        OpenByPriority: null, MeanTimeToCloseHours: null, LastEvidenceAt: null);

    // ---- Dimensão de ALERTAS (janela deslizante de 30 dias) ----------------------------------------

    private async Task<(SiemAlertPosture Posture, ChronicleApiException? Error)> CollectAlertsAsync(
        string token, GoogleSecOpsSettings s, DateTimeOffset now, CancellationToken ct)
    {
        // Janela [start, end): 30 dias, início INCLUSIVO e fim EXCLUSIVO, calculada no servidor.
        var start = now - TimeSpan.FromDays(AlertWindowDays);
        try
        {
            var result = await _api.SearchAlertsAsync(token, s.ProjectId, s.Location, s.InstanceId, start, now, ct);
            return (AggregateAlerts(result), null);
        }
        catch (ChronicleApiException ex)
        {
            _log?.LogInformation("Google SecOps: coleta de alertas não comprovada ({Kind}).", ex.Kind);
            return (FailedAlerts(StateFrom(ex)), ex);
        }
    }

    /// <summary>
    /// Agrega os alertas da janela: total (deduplicado por identidade estável quando existente), contagem por
    /// severidade (quando a fonte a fornece) e a evidência mais recente. <c>moreDataAvailable</c> ou um teto interno
    /// ⇒ PARCIAL (agregados preservados como PISO, resultado degradado). Nunca ativo, usuário, IP, título ou payload.
    /// </summary>
    private static SiemAlertPosture AggregateAlerts(ChronicleAlertSearchResult result)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var observed = 0;
        var high = 0;
        var medium = 0;
        var severityKnown = 0;
        DateTimeOffset? last = null;

        foreach (var a in result.Alerts)
        {
            var id = AlertIdentity(a);
            if (id is not null && !seen.Add(id)) continue;   // deduplicação por identidade estável da origem
            observed++;

            var rank = AlertSeverityRank(a);
            if (rank >= 0)
            {
                severityKnown++;
                if (rank == 0) high++;
                else if (rank == 1) medium++;
            }

            var ts = AlertTimestamp(a);
            if (ts is { } d && (last is null || d > last)) last = d;
        }

        var state = result.IsPartial ? SiemCollectionState.Partial : SiemCollectionState.Available;
        return new SiemAlertPosture(
            State: state,
            Period: SiemPeriodKind.RollingWindow,
            WindowDays: AlertWindowDays,
            IsComplete: state == SiemCollectionState.Available,
            Observed: observed,
            // Severidade só quando a fonte a forneceu em ao menos um alerta — nunca "0 alto" sintético sem sinal.
            HighSeverity: severityKnown == 0 ? (int?)null : high,
            MediumSeverity: severityKnown == 0 ? (int?)null : medium,
            LastEvidenceAt: last);
    }

    private static SiemAlertPosture FailedAlerts(SiemCollectionState state) => new(
        State: state, Period: SiemPeriodKind.RollingWindow, WindowDays: AlertWindowDays, IsComplete: false,
        Observed: null, HighSeverity: null, MediumSeverity: null, LastEvidenceAt: null);

    /// <summary>Mapeia a falha SANITIZADA de transporte ao estado explícito PROVIDER-NEUTRAL da dimensão (permissão/throttle/timeout distinguíveis).</summary>
    private static SiemCollectionState StateFrom(ChronicleApiException ex) => ex.Kind switch
    {
        ChronicleApiErrorKind.InsufficientPermission => SiemCollectionState.PermissionDenied,
        ChronicleApiErrorKind.Throttled => SiemCollectionState.Throttled,
        ChronicleApiErrorKind.Timeout => SiemCollectionState.Timeout,
        ChronicleApiErrorKind.InstanceNotFound => SiemCollectionState.Unsupported,
        // AuthFailure/Unauthorized/Unavailable/InvalidResponse/IncompleteCollection: dimensão não comprovada.
        _ => SiemCollectionState.Unavailable,
    };

    // ---- Parsing operacional (defensivo; nomes de campo conforme o contrato documentado — ver SECOPS-02) ----

    /// <summary>Caso FECHADO? Lê o primeiro campo de estado presente (<c>status</c>/<c>stage</c>/<c>state</c>); fechado quando o valor indica encerramento.</summary>
    private static bool IsCaseClosed(JsonElement c)
    {
        var raw = FieldStr(c, "status") ?? FieldStr(c, "stage") ?? FieldStr(c, "state");
        if (string.IsNullOrWhiteSpace(raw)) return false;   // sem marcador de fechamento → tratado como ABERTO (inventário atual)
        var v = raw.Trim().ToUpperInvariant();
        return v.Contains("CLOS") || v.Contains("RESOLV") || v.Contains("DONE");
    }

    /// <summary>Prioridade declarada pela fonte (valor bruto do provedor, ex.: <c>PRIORITY_HIGH</c>) — nunca reinterpretada. Null quando ausente.</summary>
    private static string? CasePriority(JsonElement c)
    {
        var raw = FieldStr(c, "priority");
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>Identidade estável de um alerta (para deduplicar), quando a origem a fornece: <c>id</c>/<c>alertId</c>/<c>name</c>.</summary>
    private static string? AlertIdentity(JsonElement a)
    {
        var id = FieldStr(a, "id") ?? FieldStr(a, "alertId") ?? FieldStr(a, "name");
        return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    }

    /// <summary>Rank de severidade do alerta: 0=alto (HIGH/CRITICAL), 1=médio (MEDIUM/MODERATE), 2=outro conhecido, -1=ausente.</summary>
    private static int AlertSeverityRank(JsonElement a)
    {
        var raw = FieldStr(a, "severity") ?? FieldStr(a, "alertSeverity");
        if (string.IsNullOrWhiteSpace(raw)) return -1;
        return raw.Trim().ToUpperInvariant() switch
        {
            "CRITICAL" or "HIGH" => 0,
            "MEDIUM" or "MODERATE" => 1,
            _ => 2,
        };
    }

    /// <summary>Instante do alerta (evidência mais recente), quando presente: <c>detectionTimestamp</c>/<c>createTime</c>/<c>createdTime</c>/<c>timestamp</c>.</summary>
    private static DateTimeOffset? AlertTimestamp(JsonElement a) =>
        ReadDate(a, "detectionTimestamp") ?? ReadDate(a, "createTime") ?? ReadDate(a, "createdTime") ?? ReadDate(a, "timestamp");

    private static string? FieldStr(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static DateTimeOffset? ReadDate(JsonElement e, string prop)
    {
        var s = FieldStr(e, prop);
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d)
            ? d : (DateTimeOffset?)null;
    }

    // ---- Configuração / segredo -------------------------------------------------------------------

    /// <summary>
    /// Resolve a configuração do conector. Devolve <c>null</c> SOMENTE quando não há segredo salvo (conector NÃO
    /// configurado). Configuração INCOMPATÍVEL (Provider/Capability/AuthType) ou PRESENTE-porém-inválida (segredo
    /// ilegível, JSON malformado, project ID / localidade / instance ID / JSON da service account inválidos) lança
    /// <see cref="ChronicleApiException"/> com <see cref="ChronicleApiErrorKind.AuthFailure"/> — fail-closed, antes de
    /// qualquer autenticação/coleta. Mensagens CONSTANTES: nunca ecoam o segredo. <c>customerId</c> é aceito SÓ como
    /// compatibilidade de entrada para <c>instanceId</c> (autoridade canônica ÚNICA na leitura interna).
    /// </summary>
    private GoogleSecOpsSettings? DecryptSettings(ConnectorConfig config)
    {
        if (config.Provider != ConnectorProvider.Google
            || config.Capability != ConnectorCapability.Siem
            || config.AuthType != ConnectorAuthType.ServiceAccount)
            throw new ChronicleApiException(ChronicleApiErrorKind.AuthFailure,
                "configuração incompatível com o conector Google SecOps (Provider/Capability/AuthType).");

        if (string.IsNullOrWhiteSpace(config.EncryptedSettings)) return null;

        string json;
        try
        {
            json = _protector.Unprotect(config.EncryptedSettings);
        }
        catch (Exception)
        {
            _log?.LogWarning("Configuração do conector Google SecOps ilegível; tratada como falha de autenticação.");
            throw new ChronicleApiException(ChronicleApiErrorKind.AuthFailure,
                "configuração do conector Google SecOps ilegível.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new ChronicleApiException(ChronicleApiErrorKind.AuthFailure,
                "configuração do conector Google SecOps inválida.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ChronicleApiException(ChronicleApiErrorKind.AuthFailure,
                    "configuração do conector Google SecOps inválida.");

            var projectId = ReadPublicString(root, "projectId")?.Trim();
            var location = ReadPublicString(root, "location")?.Trim();
            var serviceAccountJson = ReadPublicString(root, "serviceAccountJson");
            // instanceId é a AUTORIDADE CANÔNICA; customerId (resquício de config antiga) é só compatibilidade de entrada.
            var instanceId = (ReadPublicString(root, "instanceId") ?? ReadPublicString(root, "customerId"))?.Trim();

            if (projectId is null || !ProjectIdRegex.IsMatch(projectId)
                || !ChronicleRegions.IsSupported(location)
                || instanceId is null || !InstanceIdRegex.IsMatch(instanceId)
                || string.IsNullOrWhiteSpace(serviceAccountJson))
                throw new ChronicleApiException(ChronicleApiErrorKind.AuthFailure,
                    "configuração do conector Google SecOps inválida (project ID, localidade suportada, instance ID e JSON da service account são obrigatórios).");

            return new GoogleSecOpsSettings(projectId, location!, instanceId, serviceAccountJson!);
        }
    }

    private static string? ReadPublicString(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Configuração resolvida do Google SecOps. Deliberadamente SEM e-mail de administrador delegado: não há
    /// domain-wide delegation. <c>ToString</c> oculta o JSON da service account (nunca aparece em dump/log).
    /// </summary>
    private sealed record GoogleSecOpsSettings(string ProjectId, string Location, string InstanceId, string ServiceAccountJson)
    {
        public override string ToString() =>
            $"GoogleSecOpsSettings {{ ProjectId = {ProjectId}, Location = {Location}, InstanceId = {InstanceId}, ServiceAccountJson = *** }}";
    }
}
