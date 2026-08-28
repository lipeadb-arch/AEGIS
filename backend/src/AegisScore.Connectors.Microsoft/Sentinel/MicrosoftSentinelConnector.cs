using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;

namespace AegisScore.Connectors.Microsoft.Sentinel;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-SENTINEL] Coletor REAL, somente leitura, do Microsoft Sentinel via Azure Monitor Log
/// Analytics Query API (<c>POST /v1/workspaces/{workspaceId}/query</c>). Substitui o aviso de "adaptador não
/// implementado" por um adaptador de verdade — reusa o transporte próprio (<see cref="ILogAnalyticsClient"/>),
/// client credentials, host oficial fixo e classificação sanitizada de 401/403/429/timeout.
///
/// Duas responsabilidades, SEM tocar a autoridade determinística:
///  • <see cref="IEvidenceConnector"/> — NÃO emite sinais de score (<see cref="CollectAsync"/> não produz nada):
///    a telemetria agregada não satisfaz as fórmulas determinísticas de DE.*/RS.*/RC.* (que exigem denominadores
///    de cobertura, SLA de triagem, taxa de falso-positivo, etc.), então os controles seguem <c>NotEvaluated</c> —
///    o fallback prescrito. Nenhum mapping NIST é criado; Respond/Recover NUNCA são preenchidos por telemetria.
///  • <see cref="ISiemPostureCollector"/> — produz a POSTURA OPERACIONAL (fato consultivo): agregados e instantes
///    de <c>SecurityIncident</c> (estado mais recente por incidente via <c>arg_max</c>) e <c>SecurityAlert</c>
///    (quando disponível). NÃO inventa contenção/recuperação/reabertura/triagem.
///
/// A KQL é SEMPRE fixa no servidor (composta aqui de constantes) — jamais vem da API/UI. Timespan explícito,
/// projeção mínima e limites defensivos: nunca materializa o histórico completo nem payload bruto em memória.
/// FAIL-CLOSED: sem credenciais/segredo ilegível → não configurado; sem workspaceId → falha SÓ do Sentinel.
/// </summary>
public sealed class MicrosoftSentinelConnector : IEvidenceConnector, ISiemPostureCollector
{
    /// <summary>Rótulo estável da fonte — exibido na tela e nos diagnósticos.</summary>
    public const string SourceLabel = "Microsoft Sentinel";

    /// <summary>Janela de observação (dias). Constante — o usuário não configura KQL nem período arbitrário.</summary>
    private const int WindowDays = 30;

    /// <summary>Timespan EXPLÍCITO enviado à API (ISO-8601 duration) — a API limita TimeGenerated a esta janela.</summary>
    private const string Timespan = "P30D";

    /// <summary>
    /// Consulta MÍNIMA e independente de dados do teste de conexão (item 6): prova token + acesso real ao workspace
    /// + execução de KQL, sem depender da existência de incidentes.
    /// </summary>
    private const string ProbeQuery = "print AegisProbe=1";

    /// <summary>
    /// Estado MAIS RECENTE de cada incidente (<c>arg_max(TimeGenerated, *) by IncidentNumber</c> — dedup do histórico),
    /// agregado no servidor. Timespan limita TimeGenerated; <c>ago(30d)</c> escopa CreatedTime/ClosedTime. Projeção
    /// só do necessário; nunca título, entidade, dono, comentário ou URL.
    /// </summary>
    private const string IncidentQuery = @"
SecurityIncident
| summarize arg_max(TimeGenerated, Status, Severity, CreatedTime, ClosedTime) by IncidentNumber
| summarize
    IncidentsObserved = count(),
    OpenIncidents = countif(Status != 'Closed'),
    OpenHigh = countif(Status != 'Closed' and Severity == 'High'),
    OpenMedium = countif(Status != 'Closed' and Severity == 'Medium'),
    OpenLow = countif(Status != 'Closed' and Severity == 'Low'),
    OpenInformational = countif(Status != 'Closed' and Severity == 'Informational'),
    NewIncidents = countif(CreatedTime >= ago(30d)),
    ClosedIncidents = countif(Status == 'Closed' and ClosedTime >= ago(30d)),
    MeanTimeToCloseMinutes = avgif(todouble(datetime_diff('minute', ClosedTime, CreatedTime)),
        Status == 'Closed' and isnotempty(ClosedTime) and isnotempty(CreatedTime) and ClosedTime >= ago(30d)),
    LastEvidenceAt = max(TimeGenerated)";

    /// <summary>
    /// Alertas por severidade (secundário, "quando disponível"). Consulta SEPARADA e best-effort: se a tabela
    /// <c>SecurityAlert</c> não existir no workspace, a falha é ABSORVIDA (alertas = 0) sem derrubar a coleta —
    /// o token/workspace já foram provados pela consulta primária de incidentes.
    /// </summary>
    private const string AlertQuery = @"
SecurityAlert
| summarize
    AlertsObserved = count(),
    AlertsHigh = countif(AlertSeverity == 'High'),
    AlertsMedium = countif(AlertSeverity == 'Medium'),
    LastAlertAt = max(TimeGenerated)";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogAnalyticsClient _client;
    private readonly IConnectorSecretProtector _protector;
    private readonly ILogger<MicrosoftSentinelConnector>? _log;

    public MicrosoftSentinelConnector(
        ILogAnalyticsClient client, IConnectorSecretProtector protector,
        ILogger<MicrosoftSentinelConnector>? log = null)
    {
        _client = client;
        _protector = protector;
        _log = log;
    }

    public ConnectorProvider Provider => ConnectorProvider.MicrosoftSentinel;
    public ConnectorCapability Capability => ConnectorCapability.Siem;

    // ---- Teste de conexão (token + acesso real + KQL mínima) ---------------------------------------

    public async Task<ConnectorHealth> TestAsync(ConnectorConfig config, CancellationToken ct)
    {
        var cfg = DecryptConfig(config);
        if (cfg is null)
            return new ConnectorHealth(ConnectorStatus.Degraded,
                "Conector não configurado ou credenciais ilegíveis.");
        if (string.IsNullOrWhiteSpace(cfg.WorkspaceId))
            return new ConnectorHealth(ConnectorStatus.Degraded,
                "Informe o Log Analytics Workspace ID do Sentinel.");

        try
        {
            var token = await _client.AcquireTokenAsync(cfg.Credentials, ct);
            var result = await _client.QueryAsync(token, cfg.WorkspaceId!, ProbeQuery, Timespan, ct);

            var table = result.Primary;
            if (table is null || table.Rows.Count == 0)
                return new ConnectorHealth(ConnectorStatus.Degraded,
                    "Autenticado, mas o workspace não respondeu à consulta de verificação.");

            // A sonda projeta AegisProbe=1. Confirma que a KQL executou e retornou o valor esperado.
            var probe = ReadLong(table, table.Rows[0], "AegisProbe");
            if (probe != 1)
                return new ConnectorHealth(ConnectorStatus.Degraded,
                    "Autenticado, mas a consulta de verificação não retornou o valor esperado.");

            return new ConnectorHealth(ConnectorStatus.Healthy,
                "Autenticação e execução de KQL no workspace do Sentinel confirmadas.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LogAnalyticsException ex)
        {
            return new ConnectorHealth(HealthStatusFor(ex.Kind), MessageFor(ex.Kind));
        }
    }

    // ---- IEvidenceConnector: SEM sinais de score (controles seguem NotEvaluated) -------------------

    public async IAsyncEnumerable<EvidenceSignal> CollectAsync(
        ConnectorConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        // Deliberadamente vazio: a telemetria agregada do Sentinel não satisfaz as fórmulas determinísticas de
        // Detect/Respond/Recover; mapear contagens como "controle comprovado" inflaria o score sobre evidência
        // que a própria regra rejeita. A postura operacional vem por ISiemPostureCollector (fato consultivo).
        await Task.CompletedTask;
        yield break;
    }

    // ---- ISiemPostureCollector: postura operacional (fato consultivo, sem score) -------------------

    public async Task<SiemPostureSnapshot> CollectPostureAsync(ConnectorConfig config, CancellationToken ct)
    {
        var cfg = DecryptConfig(config)
            ?? throw new LogAnalyticsException(LogAnalyticsErrorKind.AuthFailure,
                "conector do Sentinel sem credenciais legíveis");
        if (string.IsNullOrWhiteSpace(cfg.WorkspaceId))
            throw new LogAnalyticsException(LogAnalyticsErrorKind.Unavailable,
                "conector do Sentinel sem workspaceId");

        var token = await _client.AcquireTokenAsync(cfg.Credentials, ct);

        // PRIMÁRIO: incidentes (falha propaga — carimba Failed no executor).
        var incidents = await _client.QueryAsync(token, cfg.WorkspaceId!, IncidentQuery, Timespan, ct);
        var incRow = SingleRowOrThrow(incidents, "consulta de incidentes sem linha de agregação");

        var isComplete = !incidents.IsPartial;

        var meanMinutes = ReadDouble(incidents.Primary!, incRow, "MeanTimeToCloseMinutes");
        var meanHours = meanMinutes is { } m && m >= 0 ? Math.Round(m / 60.0, 2) : (double?)null;

        // SECUNDÁRIO: alertas "quando disponível". Best-effort — se SecurityAlert não existir (ou falhar), alertas
        // = 0 e a coleta primária permanece válida (o token/workspace já foram provados acima).
        var alertsObserved = 0;
        var alertsHigh = 0;
        var alertsMedium = 0;
        DateTimeOffset? lastAlertAt = null;
        try
        {
            var alerts = await _client.QueryAsync(token, cfg.WorkspaceId!, AlertQuery, Timespan, ct);
            if (alerts.Primary is { Rows.Count: > 0 } at)
            {
                var aRow = at.Rows[0];
                alertsObserved = (int)ReadLongOrZero(at, aRow, "AlertsObserved");
                alertsHigh = (int)ReadLongOrZero(at, aRow, "AlertsHigh");
                alertsMedium = (int)ReadLongOrZero(at, aRow, "AlertsMedium");
                lastAlertAt = ReadDate(at, aRow, "LastAlertAt");
                if (alerts.IsPartial) isComplete = false;
            }
        }
        catch (LogAnalyticsException ex)
        {
            // SecurityAlert ausente/indisponível NÃO derruba a coleta — é a metade "quando disponível".
            _log?.LogInformation(
                "Sentinel: SecurityAlert indisponível no workspace ({Kind}); prosseguindo só com incidentes.", ex.Kind);
        }

        var primary = incidents.Primary!;
        var lastIncidentAt = ReadDate(primary, incRow, "LastEvidenceAt");
        var lastEvidenceAt = MaxDate(lastIncidentAt, lastAlertAt);

        return new SiemPostureSnapshot(
            WindowDays: WindowDays,
            IncidentsObserved: (int)ReadLongOrZero(primary, incRow, "IncidentsObserved"),
            OpenIncidents: (int)ReadLongOrZero(primary, incRow, "OpenIncidents"),
            NewIncidents: (int)ReadLongOrZero(primary, incRow, "NewIncidents"),
            ClosedIncidents: (int)ReadLongOrZero(primary, incRow, "ClosedIncidents"),
            OpenHighSeverity: (int)ReadLongOrZero(primary, incRow, "OpenHigh"),
            OpenMediumSeverity: (int)ReadLongOrZero(primary, incRow, "OpenMedium"),
            OpenLowSeverity: (int)ReadLongOrZero(primary, incRow, "OpenLow"),
            OpenInformationalSeverity: (int)ReadLongOrZero(primary, incRow, "OpenInformational"),
            MeanTimeToCloseHours: meanHours,
            AlertsObserved: alertsObserved,
            AlertsHighSeverity: alertsHigh,
            AlertsMediumSeverity: alertsMedium,
            LastEvidenceAt: lastEvidenceAt,
            IsComplete: isComplete);
    }

    // ---- Credenciais + settings --------------------------------------------------------------------

    private SentinelConfig? DecryptConfig(ConnectorConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.EncryptedSettings)) return null;
        try
        {
            var json = _protector.Unprotect(config.EncryptedSettings);
            var s = JsonSerializer.Deserialize<SentinelSettings>(json, JsonOpts);
            if (s is null
                || string.IsNullOrWhiteSpace(s.TenantIdValue)
                || string.IsNullOrWhiteSpace(s.ClientId)
                || string.IsNullOrWhiteSpace(s.ClientSecret))
                return null;
            var creds = new SentinelCredentials(s.TenantIdValue!, s.ClientId!, s.ClientSecret!);
            return new SentinelConfig(creds, string.IsNullOrWhiteSpace(s.WorkspaceId) ? null : s.WorkspaceId!.Trim());
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Configuração do conector Microsoft Sentinel ilegível; tratada como não configurada.");
            return null;
        }
    }

    // ---- Diagnóstico sanitizado --------------------------------------------------------------------

    private static ConnectorStatus HealthStatusFor(LogAnalyticsErrorKind kind) => kind switch
    {
        LogAnalyticsErrorKind.Throttled => ConnectorStatus.Degraded,
        LogAnalyticsErrorKind.Timeout => ConnectorStatus.Degraded,
        _ => ConnectorStatus.Failed,
    };

    private static string MessageFor(LogAnalyticsErrorKind kind) => kind switch
    {
        LogAnalyticsErrorKind.Throttled =>
            "Throttling do Log Analytics; tente novamente em instantes.",
        LogAnalyticsErrorKind.Timeout =>
            "Tempo esgotado ao consultar o workspace do Sentinel.",
        LogAnalyticsErrorKind.InsufficientPermission =>
            "Permissão insuficiente — conceda ao service principal o Azure RBAC de leitura no workspace " +
            "(Log Analytics Reader ou permissão mínima equivalente).",
        LogAnalyticsErrorKind.AuthFailure =>
            "Falha de autenticação junto ao Azure AD / Log Analytics.",
        _ => "Log Analytics indisponível para a consulta ao workspace do Sentinel.",
    };

    // ---- Leitura segura de células -----------------------------------------------------------------

    private static long? ReadLong(LogAnalyticsTable table, IReadOnlyList<JsonElement> row, string column)
    {
        var i = table.IndexOf(column);
        if (i < 0 || i >= row.Count) return null;
        var cell = row[i];
        return cell.ValueKind == JsonValueKind.Number && cell.TryGetInt64(out var v) ? v : (long?)null;
    }

    private static long ReadLongOrZero(LogAnalyticsTable table, IReadOnlyList<JsonElement> row, string column) =>
        ReadLong(table, row, column) ?? 0;

    private static double? ReadDouble(LogAnalyticsTable table, IReadOnlyList<JsonElement> row, string column)
    {
        var i = table.IndexOf(column);
        if (i < 0 || i >= row.Count) return null;
        var cell = row[i];
        return cell.ValueKind == JsonValueKind.Number && cell.TryGetDouble(out var v)
            && !double.IsNaN(v) && !double.IsInfinity(v) ? v : (double?)null;
    }

    private static DateTimeOffset? ReadDate(LogAnalyticsTable table, IReadOnlyList<JsonElement> row, string column)
    {
        var i = table.IndexOf(column);
        if (i < 0 || i >= row.Count) return null;
        var cell = row[i];
        if (cell.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(cell.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out var d) ? d : (DateTimeOffset?)null;
    }

    private static DateTimeOffset? MaxDate(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a.Value >= b.Value ? a : b);

    private static IReadOnlyList<JsonElement> SingleRowOrThrow(LogAnalyticsQueryResult result, string detail)
    {
        var table = result.Primary;
        // summarize sem 'by' devolve SEMPRE exatamente uma linha (mesmo sobre entrada vazia). Ausência é anomalia.
        if (table is null || table.Rows.Count == 0)
            throw new LogAnalyticsException(LogAnalyticsErrorKind.Unavailable, detail);
        return table.Rows[0];
    }

    // ---- Tipos internos ----------------------------------------------------------------------------

    /// <summary>Configuração resolvida do Sentinel: credenciais app-only + workspaceId (só o Sentinel usa).</summary>
    private sealed record SentinelConfig(SentinelCredentials Credentials, string? WorkspaceId);

    /// <summary>Forma do JSON de configuração. Aceita <c>tenantId</c> (o que a interface envia) e <c>azureTenantId</c>
    /// por compatibilidade. <c>workspaceId</c> é EXCLUSIVO do Sentinel; sem base URL (destino é constante oficial).</summary>
    private sealed record SentinelSettings(
        string? TenantId = null, string? AzureTenantId = null, string? ClientId = null,
        string? ClientSecret = null, string? WorkspaceId = null)
    {
        public string? TenantIdValue => !string.IsNullOrWhiteSpace(TenantId) ? TenantId : AzureTenantId;
    }

    /// <summary>Credenciais resolvidas para o transporte. ToString oculta o segredo (nunca aparece em dump/log).</summary>
    private sealed record SentinelCredentials(string AzureTenantId, string ClientId, string ClientSecret)
        : ILogAnalyticsCredentials
    {
        public override string ToString() =>
            $"SentinelCredentials {{ AzureTenantId = {AzureTenantId}, ClientId = {ClientId}, ClientSecret = *** }}";
    }
}
