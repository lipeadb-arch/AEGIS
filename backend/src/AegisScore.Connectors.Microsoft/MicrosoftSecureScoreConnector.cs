using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Domain;

namespace AegisScore.Connectors.Microsoft;

/// <summary>
/// [AEGIS-MVP-POSTURE-02] Coletor REAL do Microsoft Secure Score (somente leitura), via Microsoft Graph v1.0:
/// <c>GET /security/secureScores?$top=1</c> (fotografia mais recente) e <c>GET /security/secureScoreControlProfiles</c>
/// (catálogo de controles, paginado). Permissão de aplicativo mínima: <c>SecurityEvents.Read.All</c>.
///
/// REUTILIZA o transporte VALIDADO do KNIGHT (<see cref="IEntraGraphClient"/>): client credentials, host oficial
/// fixo, paginação defensiva por <c>@odata.nextLink</c> (validado contra o host do Graph — o bearer não vaza para
/// origem forjada) e classificação de 401/403/429/5xx. Não há segunda implementação de OAuth/paginação/validação.
///
/// Produz DUAS saídas do mesmo domínio, sem inventar nada:
///  • <see cref="IEvidenceConnector"/> — sinais determinísticos <c>secureScore.*</c> (Unit=percent) que o
///    executor RE-MAPEIA pela autoridade central (<c>NistSignalMapper</c>) e projeta no ledger;
///  • <see cref="IPostureFindingConnector"/> — exposições de configuração acionáveis (perfil × controlScore).
///
/// FAIL-CLOSED em toda parte: sem credenciais/segredo ilegível → não configurado; azureTenantId divergente do
/// tenant configurado, JSON inválido ou campos essenciais ausentes → falha sanitizada (nunca 0%, conformidade
/// ou sucesso simulado); NUNCA cai para valores demonstrativos. O SampleScores() foi REMOVIDO.
/// </summary>
public sealed class MicrosoftSecureScoreConnector : IEvidenceConnector, IPostureFindingConnector, ICombinedEvidenceCollector
{
    /// <summary>Rótulo estável da fonte — exibido na tela e usado como <c>SourceLabel</c> das exposições.</summary>
    public const string SourceLabel = "Microsoft Secure Score";

    private const string LatestScoreUrl = "security/secureScores?$top=1";
    private const string ControlProfilesUrl = "security/secureScoreControlProfiles";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Categorias de postura que possuem <c>SignalMapping</c> aprovado (Infrastructure NÃO tem — não se inventa).</summary>
    private static readonly IReadOnlyDictionary<string, string> CategorySignalKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Identity"] = "secureScore.identity",
            ["Data"] = "secureScore.data",
            ["Device"] = "secureScore.device",
            ["Apps"] = "secureScore.apps",
        };

    private readonly IEntraGraphClient _graph;
    private readonly IConnectorSecretProtector _protector;
    private readonly ILogger<MicrosoftSecureScoreConnector>? _log;

    public MicrosoftSecureScoreConnector(
        IEntraGraphClient graph, IConnectorSecretProtector protector,
        ILogger<MicrosoftSecureScoreConnector>? log = null)
    {
        _graph = graph;
        _protector = protector;
        _log = log;
    }

    public ConnectorProvider Provider => ConnectorProvider.Microsoft;
    public ConnectorCapability Capability => ConnectorCapability.SecureScore;

    // ---- Teste de conexão (autenticação + leitura real de $top=1) -----------------------------------

    public async Task<ConnectorHealth> TestAsync(ConnectorConfig config, CancellationToken ct)
    {
        var creds = DecryptCredentials(config);
        if (creds is null)
            return new ConnectorHealth(ConnectorStatus.Degraded,
                "Conector não configurado ou credenciais ilegíveis.");

        try
        {
            var token = await _graph.AcquireTokenAsync(creds, ct);
            var root = await _graph.GetJsonAsync(token, creds, LatestScoreUrl, ct);
            var latest = FirstValue(root);
            if (latest is null)
                return new ConnectorHealth(ConnectorStatus.Degraded,
                    "Autenticado, mas o tenant ainda não possui Secure Score disponível.");

            // O azureTenantId retornado precisa corresponder ao tenant Microsoft configurado (fail-closed).
            if (!TenantMatches(latest.Value, creds.AzureTenantId))
                return new ConnectorHealth(ConnectorStatus.Failed,
                    "O tenant do Secure Score retornado não corresponde ao tenant configurado.");

            return new ConnectorHealth(ConnectorStatus.Healthy,
                "Autenticação e leitura do Microsoft Secure Score confirmadas.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EntraGraphException ex)
        {
            return ex.Kind switch
            {
                EntraGraphErrorKind.Throttled => new ConnectorHealth(ConnectorStatus.Degraded,
                    "Throttling do Microsoft Graph; tente novamente em instantes."),
                EntraGraphErrorKind.InsufficientPermission => new ConnectorHealth(ConnectorStatus.Failed,
                    "Permissão insuficiente — conceda SecurityEvents.Read.All à aplicação."),
                EntraGraphErrorKind.AuthFailure => new ConnectorHealth(ConnectorStatus.Failed,
                    "Falha de autenticação junto ao Microsoft Graph."),
                _ => new ConnectorHealth(ConnectorStatus.Failed,
                    "Microsoft Graph indisponível para a leitura do Secure Score."),
            };
        }
    }

    // ---- Coleta: UMA fotografia por sincronização --------------------------------------------------
    // CollectAsync (sinais) e CollectFindingsAsync (exposições) mantêm o contrato existente e cada um faz UMA
    // aquisição própria quando chamados isoladamente. O executor, porém, usa a coleta COMBINADA
    // (ICombinedEvidenceCollector.CollectAllAsync) — token + secureScores + secureScoreControlProfiles UMA vez —
    // para que sinais e exposições de uma sincronização venham SEMPRE da mesma fotografia. Sem cache global/estático.

    public async IAsyncEnumerable<EvidenceSignal> CollectAsync(
        ConnectorConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        // Fetch + validação ANTES de qualquer yield: uma falha do Graph propaga como EntraGraphException para o
        // executor (que carimba LastStatus=Failed e sobe), nunca vira coleta vazia mascarada.
        var snapshot = await FetchAndValidateSnapshotAsync(config, ct);
        if (snapshot is null) yield break;   // sem fotografia: nenhum sinal (controles seguem NotEvaluated)
        foreach (var s in DeriveSignals(config, snapshot))
        {
            ct.ThrowIfCancellationRequested();
            yield return s;
        }
    }

    public async Task<PostureFindingCollection> CollectFindingsAsync(ConnectorConfig config, CancellationToken ct)
    {
        var snapshot = await FetchAndValidateSnapshotAsync(config, ct);
        if (snapshot is null)
            // Sem fotografia: nada a reconciliar. Coleta NÃO completa → o reconciliador não resolve por omissão.
            return new PostureFindingCollection(Array.Empty<PostureFinding>(), IsComplete: false, SourceLabel);
        return new PostureFindingCollection(DeriveFindings(snapshot), IsComplete: true, SourceLabel);
    }

    /// <summary>
    /// [AEGIS-MVP-POSTURE-02] Coleta COMBINADA: UMA aquisição da fonte (token + secureScores + perfis paginados)
    /// produz sinais E exposições da MESMA fotografia. É o caminho usado pelo executor no fluxo pull.
    /// </summary>
    public async Task<CombinedCollection> CollectAllAsync(ConnectorConfig config, CancellationToken ct)
    {
        var snapshot = await FetchAndValidateSnapshotAsync(config, ct);
        if (snapshot is null)
            return new CombinedCollection(
                Array.Empty<EvidenceSignal>(),
                new PostureFindingCollection(Array.Empty<PostureFinding>(), IsComplete: false, SourceLabel));
        return new CombinedCollection(
            DeriveSignals(config, snapshot),
            new PostureFindingCollection(DeriveFindings(snapshot), IsComplete: true, SourceLabel));
    }

    // ---- Fotografia normalizada e VALIDADA (fail-closed) -------------------------------------------

    /// <summary>
    /// Adquire UMA fotografia (token + secureScores $top=1 + perfis paginados) e a VALIDA integralmente ANTES de
    /// devolver — fail-closed. Devolve <c>null</c> SOMENTE quando o tenant não tem Secure Score (array
    /// <c>value</c> vazio): situação legítima, não é falha. Qualquer inconsistência estrutural ou de dados lança
    /// <see cref="EntraGraphException"/> SANITIZADA (o executor carimba Failed e não persiste/resolve nada):
    /// resposta sem array <c>value</c>; azureTenantId divergente; <c>controlScores</c> ausente/malformado;
    /// controlName vazio, score não-finito/negativo ou controlName DUPLICADO; perfil com id DUPLICADO; e, para
    /// cada par controlScore×perfil UTILIZADO (perfil não-deprecated), maxScore não-finito/não-positivo ou
    /// <c>score &gt; maxScore</c>. Nenhuma inconsistência vira 0%/100%/conformidade/resolução.
    /// </summary>
    private async Task<SecureScoreSnapshot?> FetchAndValidateSnapshotAsync(ConnectorConfig config, CancellationToken ct)
    {
        var creds = DecryptCredentials(config)
            ?? throw new EntraGraphException(EntraGraphErrorKind.AuthFailure,
                "conector do Secure Score sem credenciais legíveis");

        var token = await _graph.AcquireTokenAsync(creds, ct);

        // secureScores?$top=1 — o transporte já exige 200 OK e JSON válido (206 e JSON inválido → falha sanitizada).
        var scoreRoot = await _graph.GetJsonAsync(token, creds, LatestScoreUrl, ct);
        if (scoreRoot.ValueKind != JsonValueKind.Object
            || !scoreRoot.TryGetProperty("value", out var valueArr)
            || valueArr.ValueKind != JsonValueKind.Array)
            throw new EntraGraphException(EntraGraphErrorKind.Unavailable,
                "resposta do Secure Score sem o array value esperado");

        JsonElement? first = null;
        foreach (var it in valueArr.EnumerateArray()) { first = it; break; }   // $top=1
        if (first is null) return null;   // tenant sem Secure Score ainda — legítimo (não é falha)

        var snapshot = first.Value;
        if (!TenantMatches(snapshot, creds.AzureTenantId))
            throw new EntraGraphException(EntraGraphErrorKind.AuthFailure,
                "azureTenantId do Secure Score diverge do tenant configurado");

        // controlScores: presença + formato; controlName não vazio/não duplicado; score finito e não negativo.
        if (!TryGetArray(snapshot, "controlScores", out var csArr))
            throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "controlScores ausente ou malformado");

        var controlScores = new Dictionary<string, ControlScoreFact>(StringComparer.OrdinalIgnoreCase);
        foreach (var cs in csArr.EnumerateArray())
        {
            var name = StrOf(cs, "controlName");
            if (string.IsNullOrWhiteSpace(name))
                throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "controlScore com controlName vazio");
            var score = NumOf(cs, "score");
            if (score is not { } sv || !IsFinite(sv))
                throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "controlScore com score invalido");
            if (sv < 0)
                throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "controlScore com score negativo");
            if (!controlScores.TryAdd(name!, new ControlScoreFact(name!, StrOf(cs, "controlCategory"), sv)))
                throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "controlName duplicado na fotografia");
        }

        // Perfis (paginado): id não vazio (id vazio é ignorado, perfil inutilizável) e NÃO duplicado.
        var profiles = new Dictionary<string, ProfileFacts>(StringComparer.OrdinalIgnoreCase);
        await foreach (var p in _graph.GetPagedAsync(token, creds, ControlProfilesUrl, ct))
        {
            var id = StrOf(p, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!profiles.TryAdd(id!, ProfileFacts.From(p)))
                throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "perfil de controle duplicado (id repetido)");
        }

        // Pares UTILIZADOS (controlScore com perfil não-deprecated): maxScore finito+positivo e 0 <= score <= maxScore.
        foreach (var fact in controlScores.Values)
        {
            if (!profiles.TryGetValue(fact.Name, out var prof) || prof.Deprecated) continue;   // não é par utilizado
            if (!IsFinite(prof.MaxScore) || prof.MaxScore <= 0)
                throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "maxScore invalido para controle utilizado");
            if (fact.Score > prof.MaxScore)
                throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "score acima do maximo do controle");
        }

        var collectedAt = DateOf(snapshot, "createdDateTime") ?? DateTimeOffset.UtcNow;
        return new SecureScoreSnapshot(
            NumOf(snapshot, "currentScore"), NumOf(snapshot, "maxScore"), collectedAt, controlScores, profiles);
    }

    // ---- Derivações puras (a partir da fotografia já validada) -------------------------------------

    private IReadOnlyList<EvidenceSignal> DeriveSignals(ConnectorConfig config, SecureScoreSnapshot snap)
    {
        var signals = new List<EvidenceSignal>();

        // Overall: currentScore / maxScore × 100 — só com valores válidos e maxScore > 0.
        if (snap.OverallCurrent is { } cv && snap.OverallMax is { } mv && mv > 0 && IsFinite(cv))
        {
            var overall = Clamp01Pct(100.0 * cv / mv);
            if (overall is { } pct)
                signals.Add(MakeSignal(config, "secureScore.overall", pct, snap.CollectedAt));
        }

        // Categorias: denominador = Σ(maxScore) dos controles EFETIVAMENTE correspondentes (já validados);
        // categoria com controle sem perfil correspondente é incompleta → sinal não emitido.
        foreach (var (category, signalKey) in CategorySignalKeys)
        {
            var pct = ComputeCategoryPercent(snap, category);
            if (pct is { } value)
                signals.Add(MakeSignal(config, signalKey, value, snap.CollectedAt));
        }

        return signals;
    }

    /// <summary>
    /// Percentual de uma categoria = 100 × Σ(score) / Σ(maxScore) dos controles correspondentes (perfil
    /// não-deprecated). Null (não emite) quando não há controle na categoria, quando ALGUM controle da categoria
    /// não tem perfil correspondente (incompleta), ou quando o denominador é inválido.
    /// </summary>
    private static double? ComputeCategoryPercent(SecureScoreSnapshot snap, string category)
    {
        double numerator = 0, denominator = 0;
        var any = false;
        foreach (var fact in snap.ControlScores.Values)
        {
            if (!string.Equals(fact.Category, category, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!snap.Profiles.TryGetValue(fact.Name, out var prof))
                return null;   // controle da categoria sem perfil correspondente → incompleta, fail-closed
            if (prof.Deprecated) continue;   // controle depreciado não conta na categoria

            any = true;
            numerator += fact.Score;         // maxScore>0 garantido pela validação dos pares utilizados
            denominator += prof.MaxScore;
        }

        if (!any || denominator <= 0) return null;
        return Clamp01Pct(100.0 * numerator / denominator);
    }

    private static List<PostureFinding> DeriveFindings(SecureScoreSnapshot snap)
    {
        var findings = new List<PostureFinding>();
        foreach (var (id, prof) in snap.Profiles)
        {
            if (prof.Deprecated) continue;                                   // depreciado não cria exposição
            if (!snap.ControlScores.TryGetValue(id, out var fact)) continue; // sem controlScore correspondente

            var gap = prof.MaxScore - fact.Score;   // maxScore>0 e 0<=score<=max já validados para pares utilizados
            if (gap <= 0) continue;                  // sem gap positivo → não é exposição aberta

            findings.Add(new PostureFinding(
                ExternalId: id,
                Title: prof.Title ?? id,
                Category: prof.ControlCategory,
                Service: prof.Service,
                ActionType: prof.ActionType,
                CurrentScore: Math.Round(fact.Score, 2),
                MaxScore: Math.Round(prof.MaxScore, 2),
                Gap: Math.Round(gap, 2),
                SourceRank: prof.Rank,
                Tier: prof.Tier,
                ImplementationCost: prof.ImplementationCost,
                UserImpact: prof.UserImpact,
                Remediation: prof.Remediation,
                RemediationImpact: prof.RemediationImpact,
                Threats: prof.Threats,
                SourceState: prof.SourceState));
        }
        return findings;
    }

    private EvidenceSignal MakeSignal(ConnectorConfig config, string key, double percent, DateTimeOffset collectedAt) => new()
    {
        TenantId = config.TenantId,
        ConnectorConfigId = config.Id,
        SignalKey = key,
        NumericValue = Math.Round(percent, 2),
        Unit = "percent",
        Severity = SeverityFromPercent(percent),
        // MappedSubcategoryCodes deixado VAZIO de propósito: o adaptador não é autoridade de mapping — o
        // executor re-mapeia por NistSignalMapper (o campo trazido aqui seria ignorado de qualquer forma).
        CollectedAt = collectedAt,
    };

    // ---- Credenciais + validação de tenant ---------------------------------------------------------

    private GraphCredentials? DecryptCredentials(ConnectorConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.EncryptedSettings)) return null;
        try
        {
            var json = _protector.Unprotect(config.EncryptedSettings);
            var s = JsonSerializer.Deserialize<SecureScoreSettings>(json, JsonOpts);
            if (s is null
                || string.IsNullOrWhiteSpace(s.TenantIdValue)
                || string.IsNullOrWhiteSpace(s.ClientId)
                || string.IsNullOrWhiteSpace(s.ClientSecret))
                return null;
            return new GraphCredentials(s.TenantIdValue!, s.ClientId!, s.ClientSecret!);
        }
        catch (Exception ex)
        {
            // Segredo ilegível/adulterado = não configurado (fail-closed). Nada sensível vai ao log.
            _log?.LogWarning(ex, "Configuração do conector Microsoft Secure Score ilegível; tratada como não configurada.");
            return null;
        }
    }

    /// <summary>Confere se o <c>azureTenantId</c> da fotografia bate com o tenant configurado (case-insensitive).</summary>
    private static bool TenantMatches(JsonElement snapshot, string configuredTenantId)
    {
        var returned = StrOf(snapshot, "azureTenantId");
        return !string.IsNullOrWhiteSpace(returned)
            && string.Equals(returned!.Trim(), configuredTenantId?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static int SeverityFromPercent(double pct) => pct switch
    {
        >= 80 => 0,
        >= 60 => 1,
        >= 40 => 2,
        >= 20 => 3,
        _ => 4,
    };

    // ---- Parsing seguro do JSON do Graph -----------------------------------------------------------

    /// <summary>Primeiro item do array <c>value</c> da resposta do Graph, ou null (resposta vazia/malformada).</summary>
    private static JsonElement? FirstValue(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("value", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
                return item;   // $top=1 → no máximo um
        }
        return null;
    }

    private static string? StrOf(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static double? NumOf(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : (double?)null;
    }

    private static bool BoolOf(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? DateOf(JsonElement e, string prop)
    {
        var s = StrOf(e, prop);
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d)
            ? d : (DateTimeOffset?)null;
    }

    private static bool TryGetArray(JsonElement e, string prop, out JsonElement arr)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            arr = v;
            return true;
        }
        arr = default;
        return false;
    }

    private static List<string> StringArray(JsonElement e, string prop)
    {
        var list = new List<string>();
        if (TryGetArray(e, prop, out var arr))
            foreach (var it in arr.EnumerateArray())
                if (it.ValueKind == JsonValueKind.String && it.GetString() is { Length: > 0 } s)
                    list.Add(s);
        return list;
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>Percentual válido no domínio 0–100 (senão null — dado incompatível não vira 0% nem 100%).</summary>
    private static double? Clamp01Pct(double v) => IsFinite(v) && v is >= 0 and <= 100 ? v : (double?)null;

    // ---- Tipos internos ----------------------------------------------------------------------------

    /// <summary>Forma do JSON de configuração (em claro no <c>settings</c>; cifrado em repouso). Aceita <c>tenantId</c>
    /// (o que a interface envia) e <c>azureTenantId</c> por compatibilidade. Sem base URL — o destino é constante oficial.</summary>
    private sealed record SecureScoreSettings(
        string? TenantId = null, string? AzureTenantId = null, string? ClientId = null, string? ClientSecret = null)
    {
        public string? TenantIdValue => !string.IsNullOrWhiteSpace(TenantId) ? TenantId : AzureTenantId;
    }

    /// <summary>Credenciais resolvidas para o transporte do Graph. ToString oculta o segredo (nunca aparece em dump/log).</summary>
    private sealed record GraphCredentials(string AzureTenantId, string ClientId, string ClientSecret) : IMicrosoftGraphCredentials
    {
        public override string ToString() =>
            $"GraphCredentials {{ AzureTenantId = {AzureTenantId}, ClientId = {ClientId}, ClientSecret = *** }}";
    }

    /// <summary>Fotografia NORMALIZADA e VALIDADA do Secure Score: score geral + controlScores (por nome) + perfis (por id).</summary>
    private sealed record SecureScoreSnapshot(
        double? OverallCurrent, double? OverallMax, DateTimeOffset CollectedAt,
        IReadOnlyDictionary<string, ControlScoreFact> ControlScores,
        IReadOnlyDictionary<string, ProfileFacts> Profiles);

    /// <summary>Fato NORMALIZADO de um controlScore da fotografia (nome, categoria informada, score atual).</summary>
    private sealed record ControlScoreFact(string Name, string? Category, double Score);

    /// <summary>Fatos NORMALIZADOS de um secureScoreControlProfile (só o necessário; sem actionUrl/updatedBy/PII).</summary>
    private sealed record ProfileFacts(
        string? Title, string? ControlCategory, string? Service, string? ActionType, double MaxScore,
        int? Rank, string? Tier, string? ImplementationCost, string? UserImpact,
        string? Remediation, string? RemediationImpact, List<string> Threats, bool Deprecated, string? SourceState)
    {
        public static ProfileFacts From(JsonElement p) => new(
            Title: StrOf(p, "title"),
            ControlCategory: StrOf(p, "controlCategory"),
            Service: StrOf(p, "service"),
            ActionType: StrOf(p, "actionType"),
            MaxScore: NumOf(p, "maxScore") ?? 0,
            Rank: NumOf(p, "rank") is { } r ? (int)Math.Round(r) : (int?)null,
            Tier: StrOf(p, "tier"),
            ImplementationCost: StrOf(p, "implementationCost"),
            UserImpact: StrOf(p, "userImpact"),
            Remediation: StrOf(p, "remediation"),
            RemediationImpact: StrOf(p, "remediationImpact"),
            Threats: StringArray(p, "threats"),
            Deprecated: BoolOf(p, "deprecated"),
            SourceState: LatestState(p));

        /// <summary>Estado mais recente informado pela fonte (<c>controlStateUpdates[].state</c>) — SÓ o rótulo do
        /// estado; nunca updatedBy/comment/assignedTo/e-mail. Metadado, não prova.</summary>
        private static string? LatestState(JsonElement p)
        {
            if (!TryGetArray(p, "controlStateUpdates", out var arr)) return null;
            string? state = null;
            foreach (var u in arr.EnumerateArray())
            {
                var s = StrOf(u, "state");
                if (!string.IsNullOrWhiteSpace(s)) state = s;   // última entrada vence (a mais recente)
            }
            return state;
        }
    }
}
