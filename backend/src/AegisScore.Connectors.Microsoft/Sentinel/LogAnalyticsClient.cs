using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Connectors.Microsoft.Sentinel;

/// <summary>Natureza de uma falha ao falar com a Azure Monitor Log Analytics Query API (traduzida pelo conector).</summary>
public enum LogAnalyticsErrorKind
{
    AuthFailure,
    InsufficientPermission,
    Throttled,
    Timeout,
    Unavailable,
}

/// <summary>
/// Falha SANITIZADA de acesso ao Log Analytics. Expõe só metadados operacionais seguros para diagnóstico: status
/// HTTP, código de erro da API (sanitizado) e <see cref="RetryAfterSeconds"/> quando a API o informa. NUNCA carrega
/// token, segredo, KQL, workspaceId, URL completa, mensagem bruta, PII ou payload.
/// </summary>
public sealed class LogAnalyticsException : Exception
{
    public LogAnalyticsErrorKind Kind { get; }
    public int? HttpStatusCode { get; }
    public string? ApiErrorCode { get; }
    public int? RetryAfterSeconds { get; }

    public LogAnalyticsException(
        LogAnalyticsErrorKind kind,
        string? detail = null,
        int? httpStatusCode = null,
        string? apiErrorCode = null,
        int? retryAfterSeconds = null) : base(detail)
    {
        Kind = kind;
        HttpStatusCode = httpStatusCode;
        ApiErrorCode = SanitizeCode(apiErrorCode);
        RetryAfterSeconds = retryAfterSeconds is > 0 and <= 86_400 ? retryAfterSeconds : null;
    }

    private static string? SanitizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > 96) trimmed = trimmed[..96];
        foreach (var ch in trimmed)
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')) return null;
        return trimmed;
    }
}

/// <summary>Credenciais app-only (client credentials) para o Log Analytics — mesma forma do Entra/Defender, recurso DIFERENTE.</summary>
public interface ILogAnalyticsCredentials
{
    string AzureTenantId { get; }
    string ClientId { get; }
    string ClientSecret { get; }
}

/// <summary>Uma tabela de resultado do Log Analytics: nome, colunas (por nome) e linhas (células como <see cref="JsonElement"/>).</summary>
public sealed record LogAnalyticsTable(
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<JsonElement>> Rows)
{
    /// <summary>Índice da coluna pelo nome (case-insensitive), ou -1.</summary>
    public int IndexOf(string column)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i], column, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}

/// <summary>
/// Resultado normalizado de UMA consulta KQL. <see cref="IsPartial"/> reflete um resultado truncado/parcial
/// SINALIZADO pela própria API (não uma inferência nossa). <see cref="Primary"/> é a tabela <c>PrimaryResult</c>.
/// </summary>
public sealed record LogAnalyticsQueryResult(IReadOnlyList<LogAnalyticsTable> Tables, bool IsPartial)
{
    public LogAnalyticsTable? Primary =>
        Tables.FirstOrDefault(t => string.Equals(t.Name, "PrimaryResult", StringComparison.OrdinalIgnoreCase))
        ?? (Tables.Count > 0 ? Tables[0] : null);
}

/// <summary>
/// [AEGIS-MVP-MICROSOFT-SENTINEL] Transporte de baixo nível da Azure Monitor Log Analytics Query API (client
/// credentials + POST de consulta KQL). É o seam testável por HTTP simulado: mockando o <see cref="HttpClient"/>
/// (via HttpMessageHandler) o protocolo real é exercido — forma do token, header Bearer, corpo <c>{query,timespan}</c>,
/// classificação de 401/403/429(+Retry-After)/timeout/5xx e parsing fail-closed de <c>tables/columns/rows</c>.
///
/// A KQL é SEMPRE fixa no servidor (o conector a compõe de constantes) — este transporte nunca recebe consulta do
/// usuário/API. O único parâmetro de caminho é o <c>workspaceId</c>, exigido no formato GUID (impede injeção de path
/// e destino forjado). O destino é a origem OFICIAL e CONSTANTE — o tenant nunca fornece base URL.
/// </summary>
public interface ILogAnalyticsClient
{
    Task<string> AcquireTokenAsync(ILogAnalyticsCredentials creds, CancellationToken ct);

    /// <summary>Executa UMA consulta KQL FIXA no workspace, com timespan explícito. Falha sanitizada sobe.</summary>
    Task<LogAnalyticsQueryResult> QueryAsync(
        string token, string workspaceId, string kql, string timespan, CancellationToken ct);
}

/// <inheritdoc cref="ILogAnalyticsClient"/>
public sealed class LogAnalyticsClient : ILogAnalyticsClient
{
    // Origens OFICIAIS e CONSTANTES. ⚠️ O HOST da consulta e o RECURSO (audience) do token são domínios DISTINTOS:
    // a consulta vai para api.loganalytics.azure.com, mas o token client-credentials deve ser pedido com o scope
    // api.loganalytics.io/.default (doc oficial: learn.microsoft.com/azure/azure-monitor/logs/api/access-api). Usar
    // o mesmo domínio nos dois faz o AAD devolver 401/403 (audiência divergente). São constantes distintas de
    // propósito. O tenant NUNCA fornece base URL nem destino: impede exfiltrar o bearer para uma origem arbitrária.
    private const string LoginBaseUrl = "https://login.microsoftonline.com";
    private const string TokenResource = "https://api.loganalytics.io/.default";
    private const string ApiBaseUrl = "https://api.loganalytics.azure.com";
    private const string ApiHost = "api.loganalytics.azure.com";

    /// <summary>Teto defensivo de linhas por tabela — as consultas agregam no servidor (poucas linhas), mas o
    /// parser nunca materializa um resultado inesperadamente grande.</summary>
    private const int DefaultMaxRows = 10_000;

    private readonly HttpClient _http;
    private readonly int _maxRows;

    public LogAnalyticsClient(HttpClient http) : this(http, DefaultMaxRows) { }

    /// <summary>Construtor com teto de linhas injetável — SOMENTE para teste. <c>internal</c>: nada disso vem do tenant.</summary>
    internal LogAnalyticsClient(HttpClient http, int maxRows)
    {
        _http = http;
        _maxRows = maxRows > 0 ? maxRows : DefaultMaxRows;
    }

    public async Task<string> AcquireTokenAsync(ILogAnalyticsCredentials creds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(creds.AzureTenantId)
            || string.IsNullOrWhiteSpace(creds.ClientId)
            || string.IsNullOrWhiteSpace(creds.ClientSecret))
            throw new LogAnalyticsException(LogAnalyticsErrorKind.AuthFailure, "credenciais app-only incompletas");

        var url = $"{LoginBaseUrl}/{Uri.EscapeDataString(creds.AzureTenantId)}/oauth2/v2.0/token";
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = creds.ClientId,
            ["client_secret"] = creds.ClientSecret,
            ["scope"] = TokenResource,
            ["grant_type"] = "client_credentials",
        });

        HttpResponseMessage resp;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            resp = await _http.SendAsync(req, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LogAnalyticsException(LogAnalyticsErrorKind.Timeout, "tempo esgotado ao obter token");
        }
        catch (HttpRequestException)
        {
            throw new LogAnalyticsException(LogAnalyticsErrorKind.Unavailable, "endpoint de token inacessível");
        }

        using (resp)
        {
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                // O endpoint OAuth (AAD) devolve credencial inválida como HTTP 400 com um campo string `error`
                // (invalid_client/unauthorized_client/invalid_grant/…). Parsing DEFENSIVO e LIMITADO: só o código
                // `error` (sanitizado) — nunca `error_description`, segredo ou corpo bruto — para não classificar
                // uma falha de autenticação como Unavailable. Throttling/timeout/indisponibilidade são preservados.
                var errBody = await SafeReadAsync(resp, ct);
                var oauthCode = TryReadOAuthErrorCode(errBody);
                throw new LogAnalyticsException(
                    ClassifyTokenError(resp.StatusCode, oauthCode), $"token endpoint retornou {(int)resp.StatusCode}",
                    (int)resp.StatusCode, apiErrorCode: oauthCode, retryAfterSeconds: RetryAfter(resp));
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new LogAnalyticsException(LogAnalyticsErrorKind.AuthFailure, "resposta do token não é JSON válido");
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("access_token", out var t)
                    && t.ValueKind == JsonValueKind.String
                    && t.GetString() is { Length: > 0 } token)
                    return token;
            }
            throw new LogAnalyticsException(LogAnalyticsErrorKind.AuthFailure, "resposta do token sem access_token válido");
        }
    }

    public async Task<LogAnalyticsQueryResult> QueryAsync(
        string token, string workspaceId, string kql, string timespan, CancellationToken ct)
    {
        // workspaceId DEVE ser um GUID — é o único trecho variável do caminho. Impede injeção de path e destino
        // forjado, e é exatamente o formato do Log Analytics Workspace ID.
        if (!Guid.TryParse((workspaceId ?? "").Trim(), out var wsGuid))
            throw new LogAnalyticsException(LogAnalyticsErrorKind.Unavailable, "workspaceId ausente ou fora do formato GUID");

        var url = $"{ApiBaseUrl}/v1/workspaces/{wsGuid:D}/query";
        ValidateUrl(url);

        var payload = JsonSerializer.Serialize(new QueryBody(kql, timespan));

        HttpResponseMessage resp;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            resp = await _http.SendAsync(req, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LogAnalyticsException(LogAnalyticsErrorKind.Timeout, "tempo esgotado na consulta ao workspace");
        }
        catch (HttpRequestException)
        {
            throw new LogAnalyticsException(LogAnalyticsErrorKind.Unavailable, "Log Analytics inacessível");
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode != HttpStatusCode.OK)
                throw new LogAnalyticsException(
                    Classify(resp.StatusCode), $"log analytics retornou {(int)resp.StatusCode}",
                    (int)resp.StatusCode, TryReadApiErrorCode(body), RetryAfter(resp));

            return ParseResult(body);
        }
    }

    // ---- Parsing fail-closed ----------------------------------------------------------------------

    /// <summary>
    /// Parse ESTRUTURAL fail-closed. 200 OK com corpo não-objeto, sem <c>tables</c> array, ou tabela malformada é
    /// falha sanitizada — NUNCA coleção vazia em silêncio nem <see cref="JsonException"/> escapando. Um objeto
    /// <c>error</c> na raiz cujo código indica resultado PARCIAL não é falha: marca <see cref="LogAnalyticsQueryResult.IsPartial"/>
    /// e devolve as tabelas presentes; qualquer outro <c>error</c> vira falha.
    /// </summary>
    private LogAnalyticsQueryResult ParseResult(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new LogAnalyticsException(LogAnalyticsErrorKind.Unavailable, "resposta do Log Analytics não é JSON válido");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new LogAnalyticsException(LogAnalyticsErrorKind.Unavailable, "resposta do Log Analytics sem objeto raiz");

            // Resultado parcial sinalizado pela API (200 OK + error.code "PartialError"). Degrada, não falha.
            var isPartial = false;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                var code = err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (!string.IsNullOrEmpty(code) && code!.Contains("Partial", StringComparison.OrdinalIgnoreCase))
                    isPartial = true;
                else
                    throw new LogAnalyticsException(
                        LogAnalyticsErrorKind.Unavailable, "consulta sinalizou erro", apiErrorCode: code);
            }

            if (!root.TryGetProperty("tables", out var tablesEl) || tablesEl.ValueKind != JsonValueKind.Array)
                throw new LogAnalyticsException(LogAnalyticsErrorKind.Unavailable, "resposta do Log Analytics sem o array tables");

            var tables = new List<LogAnalyticsTable>();
            foreach (var tableEl in tablesEl.EnumerateArray())
            {
                if (tableEl.ValueKind != JsonValueKind.Object)
                    throw new LogAnalyticsException(LogAnalyticsErrorKind.Unavailable, "tabela do Log Analytics malformada");

                var name = tableEl.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? "" : "";

                if (!tableEl.TryGetProperty("columns", out var colsEl) || colsEl.ValueKind != JsonValueKind.Array)
                    throw new LogAnalyticsException(LogAnalyticsErrorKind.Unavailable, "tabela sem o array columns");
                var columns = new List<string>();
                foreach (var col in colsEl.EnumerateArray())
                    columns.Add(col.ValueKind == JsonValueKind.Object && col.TryGetProperty("name", out var cn)
                        && cn.ValueKind == JsonValueKind.String ? cn.GetString() ?? "" : "");

                if (!tableEl.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                    throw new LogAnalyticsException(LogAnalyticsErrorKind.Unavailable, "tabela sem o array rows");

                var rows = new List<IReadOnlyList<JsonElement>>();
                foreach (var rowEl in rowsEl.EnumerateArray())
                {
                    if (rowEl.ValueKind != JsonValueKind.Array)
                        throw new LogAnalyticsException(LogAnalyticsErrorKind.Unavailable, "linha do Log Analytics malformada");
                    if (rows.Count >= _maxRows)
                    {
                        isPartial = true;   // teto atingido: honestamente parcial (não silencia o excesso)
                        break;
                    }
                    var cells = new List<JsonElement>();
                    foreach (var cell in rowEl.EnumerateArray())
                        cells.Add(cell.Clone());   // Clone: sobrevive ao dispose do JsonDocument
                    rows.Add(cells);
                }

                tables.Add(new LogAnalyticsTable(name, columns, rows));
            }

            return new LogAnalyticsQueryResult(tables, isPartial);
        }
    }

    // ---- Destino / classificação ------------------------------------------------------------------

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new LogAnalyticsException(LogAnalyticsErrorKind.Unavailable, "destino de requisicao reprovado pela allowlist do Log Analytics");
        }
    }

    private static int? RetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } delta) return (int)Math.Ceiling(delta.TotalSeconds);
        if (ra.Date is { } date)
        {
            var secs = (date - DateTimeOffset.UtcNow).TotalSeconds;
            return secs > 0 ? (int)Math.Ceiling(secs) : null;
        }
        return null;
    }

    /// <summary>
    /// Código de erro SANITIZADO da Query API. Prefere o código MAIS ESPECÍFICO: o Log Analytics envolve a falha
    /// real num <c>error.code</c> genérico (ex.: <c>BadArgumentError</c>) e detalha a causa em
    /// <c>error.details[].code</c> (ex.: <c>SemanticError</c> para tabela/coluna não resolvida). Sem isso, tabela
    /// ausente ficaria indistinguível de um 400 qualquer. Leitura defensiva e limitada — só o campo <c>code</c>,
    /// nunca <c>message</c>/corpo bruto.
    /// </summary>
    private static string? TryReadApiErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 64_000) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
                return null;

            // Código específico do primeiro detalhe (a causa real), quando presente.
            if (error.TryGetProperty("details", out var details)
                && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    if (detail.ValueKind == JsonValueKind.Object
                        && detail.TryGetProperty("code", out var dcode)
                        && dcode.ValueKind == JsonValueKind.String
                        && dcode.GetString() is { Length: > 0 } specific)
                        return specific;
                    break;   // só o primeiro detalhe — não varre a lista inteira
                }
            }

            return error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LogAnalyticsErrorKind Classify(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized => LogAnalyticsErrorKind.AuthFailure,
        HttpStatusCode.Forbidden => LogAnalyticsErrorKind.InsufficientPermission,
        HttpStatusCode.TooManyRequests => LogAnalyticsErrorKind.Throttled,
        HttpStatusCode.RequestTimeout => LogAnalyticsErrorKind.Timeout,
        HttpStatusCode.GatewayTimeout => LogAnalyticsErrorKind.Timeout,
        _ => LogAnalyticsErrorKind.Unavailable,
    };

    /// <summary>
    /// Classificação de uma resposta de ERRO do endpoint OAuth (AAD). Um código de erro OAuth de autenticação
    /// conhecido, ou qualquer HTTP 400 do endpoint de token (rejeição de client_credentials), é
    /// <see cref="LogAnalyticsErrorKind.AuthFailure"/>. Demais status caem na classificação por HTTP — preservando
    /// 429 (Throttled), 408/504 (Timeout) e 5xx (Unavailable). Nunca vira Unavailable uma credencial inválida.
    /// </summary>
    private static LogAnalyticsErrorKind ClassifyTokenError(HttpStatusCode status, string? oauthErrorCode)
    {
        if (oauthErrorCode is not null && IsAuthErrorCode(oauthErrorCode)) return LogAnalyticsErrorKind.AuthFailure;
        if (status == HttpStatusCode.BadRequest) return LogAnalyticsErrorKind.AuthFailure;
        return Classify(status);
    }

    /// <summary>Códigos OAuth (RFC 6749 / AAD) que indicam falha de autenticação/autorização da credencial.</summary>
    private static bool IsAuthErrorCode(string code) => code switch
    {
        "invalid_client" or "unauthorized_client" or "invalid_grant"
            or "invalid_request" or "unsupported_grant_type" or "invalid_scope" => true,
        _ => false,
    };

    /// <summary>
    /// Lê APENAS o campo string <c>error</c> de uma resposta OAuth (ex.: <c>{"error":"invalid_client",…}</c>).
    /// Distinto do erro da Query API (objeto <c>error.code</c>). NUNCA lê <c>error_description</c>/corpo bruto.
    /// </summary>
    private static string? TryReadOAuthErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body!.Length > 64_000) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.String)
                return null;
            return error.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Lê o corpo com tolerância a falha (o classificador de erro não pode lançar por causa da leitura).</summary>
    private static async Task<string?> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Corpo da consulta: KQL FIXA + timespan explícito. camelCase por contrato da API.</summary>
    private sealed record QueryBody(string query, string timespan);
}
