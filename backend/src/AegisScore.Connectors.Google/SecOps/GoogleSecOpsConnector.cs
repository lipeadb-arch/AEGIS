using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Connectors.Google.Auth;
using AegisScore.Connectors.Google.Cloud;
using AegisScore.Domain;
// Cobertura de detecção: o record PROVIDER-NEUTRAL da camada Application (a entidade persistida homônima vive no
// Domain, mas o conector produz o record consultivo — nunca escreve no banco).
using AppDetectionCoverage = AegisScore.Application.Abstractions.DetectionCoverageSnapshot;

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
    /// <summary>
    /// Escopo OAuth OFICIAL do Chronicle/Google SecOps — o ÚNICO aceito por TODAS as três operações usadas
    /// (instances.get, cases.list e legacySearchEnterpriseWideAlerts). ⚠️ Deliberadamente NÃO é o
    /// <c>chronicle.readonly</c>: a documentação de <c>cases.list</c> NÃO o lista entre os escopos aceitos (só
    /// <c>chronicle</c> e <c>cloud-platform</c>). Escolhido <c>chronicle</c> em vez de <c>cloud-platform</c> (menor
    /// superfície). Este escopo NÃO é "readonly": o AEGIS permanece operacionalmente somente leitura porque só executa
    /// métodos HTTP GET; o MENOR PRIVILÉGIO efetivo depende das permissões IAM concedidas à service account
    /// (ex.: <c>chronicle.instances.get</c>, <c>chronicle.cases.get</c>,
    /// <c>chronicle.legacies.legacySearchEnterpriseWideAlerts</c>).
    /// </summary>
    public const string ChronicleScope = "https://www.googleapis.com/auth/chronicle";

    public async Task<string> AcquireAccessTokenAsync(string serviceAccountJson, CancellationToken ct)
    {
        try
        {
            return await GoogleServiceAccountTokenSource.AcquireAsync(
                serviceAccountJson, new[] { ChronicleScope }, ct);
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
public sealed class GoogleSecOpsConnector : IEvidenceConnector, ISiemPostureCollector, IDetectionCoverageCollector
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
    private readonly IMitreAttackCatalog _mitre;
    private readonly ILogger<GoogleSecOpsConnector>? _log;

    // Memoização do access token por INSTÂNCIA (scoped = UMA sincronização): casos/alertas e cobertura de detecção
    // reusam a MESMA troca OAuth, sem uma segunda desnecessária. A chave é o HASH do segredo+destino (nunca o
    // segredo em claro): uma config diferente jamais reaproveita o token. Nunca registrado/logado.
    private string? _cachedTokenKey;
    private string? _cachedToken;

    public GoogleSecOpsConnector(
        IGoogleSecOpsAuthenticator auth, IChronicleApiClient api,
        IConnectorSecretProtector protector, IMitreAttackCatalog mitre,
        ILogger<GoogleSecOpsConnector>? log = null)
    {
        _auth = auth;
        _api = api;
        _protector = protector;
        _mitre = mitre;
        _log = log;
    }

    public ConnectorProvider Provider => ConnectorProvider.Google;
    public ConnectorCapability Capability => ConnectorCapability.Siem;

    /// <summary>
    /// Adquire o access token REUSANDO a memoização por instância (scoped): duas dimensões da mesma sincronização
    /// (casos/alertas e cobertura de detecção) não disparam duas trocas OAuth. Fora de uma sincronização (instância
    /// nova por request), nada é compartilhado.
    /// </summary>
    private async Task<string> AcquireTokenAsync(GoogleSecOpsSettings s, CancellationToken ct)
    {
        var key = TokenCacheKey(s);
        if (_cachedToken is not null && string.Equals(_cachedTokenKey, key, StringComparison.Ordinal))
            return _cachedToken;
        var token = await _auth.AcquireAccessTokenAsync(s.ServiceAccountJson, ct);
        _cachedToken = token;
        _cachedTokenKey = key;
        return token;
    }

    /// <summary>Chave de memoização = SHA-256 (hex) do destino + segredo. Nunca guarda o segredo em claro num campo.</summary>
    private static string TokenCacheKey(GoogleSecOpsSettings s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            s.ProjectId + "" + s.Location + "" + s.InstanceId + "" + s.ServiceAccountJson)));

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

            var token = await AcquireTokenAsync(settings, ct);

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
                // 400: a REQUISIÇÃO foi rejeitada (contrato/argumento). NÃO instruir troca de credenciais.
                ChronicleApiErrorKind.InvalidRequest => new ConnectorHealth(ConnectorStatus.Failed,
                    "Requisição rejeitada pela Chronicle API do Google SecOps (contrato/argumento inválido). Não é falha de credencial — reporte a ocorrência."),
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

        var token = await AcquireTokenAsync(settings, ct);
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

    // ---- IDetectionCoverageCollector: cobertura de detecção (regras × MITRE), fato consultivo, sem score -------
    // Provider/Capability já são expostos acima (Google/Siem) — a MESMA integração/credencial serve as 3 dimensões.

    /// <summary>
    /// Coleta a COBERTURA DE DETECÇÃO (regras × MITRE ATT&CK v17.1) via rules.list (CONFIG_ONLY). Dimensão
    /// INDEPENDENTE de casos/alertas: NÃO lança em falha da fonte — devolve SEMPRE uma fotografia com o estado
    /// classificado (Available/Partial/Unavailable), para nunca derrubar a sincronização de casos/alertas nem
    /// apagar cobertura anterior (a preservação vive no reconciliador). Só o cancelamento SOLICITADO propaga.
    /// NÃO emite EvidenceSignal, NÃO mapeia NIST, NÃO toca o score.
    /// </summary>
    public async Task<AppDetectionCoverage> CollectCoverageAsync(ConnectorConfig config, CancellationToken ct)
    {
        var attemptedAt = DateTimeOffset.UtcNow;

        GoogleSecOpsSettings settings;
        try
        {
            settings = DecryptSettings(config)
                ?? throw new ChronicleApiException(ChronicleApiErrorKind.AuthFailure,
                    "conector do Google SecOps sem configuração legível");
        }
        catch (ChronicleApiException)
        {
            // Config ausente/ilegível: dimensão INDISPONÍVEL — nunca derruba casos/alertas nem inventa cobertura vazia.
            return UnavailableCoverage(attemptedAt);
        }

        string token;
        try
        {
            token = await AcquireTokenAsync(settings, ct);
        }
        catch (ChronicleApiException ex)
        {
            _log?.LogInformation("Google SecOps: cobertura de detecção não comprovada (auth {Kind}).", ex.Kind);
            return UnavailableCoverage(attemptedAt);
        }

        var acc = new RuleCoverageAccumulator(_mitre);
        ChronicleRulesResult result;
        try
        {
            // O transporte ENTREGA cada regra ao acumulador (piso mínimo) e devolve o desfecho estrutural. Falha na
            // PRIMEIRA página lança → dimensão indisponível; falha APÓS ≥1 página marca parcial preservando o piso.
            result = await _api.CollectRulesAsync(token, settings.ProjectId, settings.Location, settings.InstanceId, acc.Add, ct);
        }
        catch (ChronicleApiException ex)
        {
            _log?.LogInformation("Google SecOps: coleta de regras não comprovada ({Kind}).", ex.Kind);
            return UnavailableCoverage(attemptedAt);
        }

        var state = result.IsPartial
            ? DetectionCoverageCollectionState.Partial
            : DetectionCoverageCollectionState.Available;
        return acc.ToSnapshot(SourceLabel, _mitre.AttackVersion, state, attemptedAt);
    }

    private AppDetectionCoverage UnavailableCoverage(DateTimeOffset attemptedAt) => new(
        SourceLabel, _mitre.AttackVersion, DetectionCoverageCollectionState.Unavailable, attemptedAt,
        TotalActiveRules: 0, RulesWithMitre: 0, RulesWithoutMitre: 0, RulesInLiveMode: 0,
        RulesInNormalExecution: 0, RulesInLimitedExecution: 0, RulesInPausedExecution: 0, RulesInUnknownExecution: 0,
        RulesWithAlerting: 0, Techniques: Array.Empty<DetectionTechniqueCoverage>());

    // ---- Agregação de regras (CONFIG_ONLY) — só configuração, nunca texto/nome/autor/conteúdo -------------------

    /// <summary>
    /// Formato de um ID de técnica MITRE: <c>T</c>+4 dígitos, opcionalmente <c>.ddd</c> (subtécnica). CASE-INSENSITIVE
    /// para reconhecer o formato OFICIAL em minúsculas do Google SecOps (ex.: <c>t1136.003</c>); a normalização para o
    /// ID canônico MAIÚSCULO ocorre SÓ depois da validação. Usado APENAS nos campos de metadados DEDICADOS
    /// (<c>technique</c>/<c>mitre_ttp</c>), onde qualquer <c>T####</c> É uma referência de técnica.
    /// </summary>
    private static readonly Regex TechniqueTokenRegex =
        new(@"\bT\d{4}(?:\.\d{3})?\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Tag MITRE no namespace OFICIAL <c>google.mitre.technique.&lt;ID&gt;</c> — aceita tanto a tag curta
    /// (<c>google.mitre.technique.t1136.003</c>) quanto o resource name completo terminando nela
    /// (<c>projects/.../google.mitre.technique.T1595</c>). O ID fica ancorado ao FIM da string, de modo que
    /// <c>google.mitre.tactic.*</c> NUNCA casa e um <c>T####</c> solto numa tag qualquer NÃO é extraído.
    /// </summary>
    private static readonly Regex TechniqueTagRegex =
        new(@"google\.mitre\.technique\.(T\d{4}(?:\.\d{3})?)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Chaves de metadados (meta compilado da YARA-L) OFICIALMENTE reconhecidas para técnicas MITRE, conforme a doc de
    /// unified rules. SOMENTE <c>technique</c> e <c>mitre_ttp</c> — chaves de fonte comunitária não são autoridade.
    /// </summary>
    private static readonly string[] TechniqueMetaKeys = { "technique", "mitre_ttp" };

    /// <summary>
    /// Acumulador de cobertura de regras alimentado PÁGINA A PÁGINA — totais + agregação por técnica MITRE VÁLIDA —
    /// sem reter os objetos completos. Regras ARQUIVADAS não entram nos totais ativos; regras sem técnica MITRE
    /// válida (ausente, formato inválido ou inexistente no catálogo fixado) contam em "sem mapeamento". Só lê os
    /// campos de CONFIGURAÇÃO necessários — nunca texto, nome, autor, descrição ou contagem de detecções.
    /// </summary>
    private sealed class RuleCoverageAccumulator
    {
        private readonly IMitreAttackCatalog _mitre;
        private int _activeRules;
        private int _withMitre;
        private int _withoutMitre;
        private int _liveRules;
        // Condição de execução das regras em live mode (partição de _liveRules).
        private int _liveNormal;
        private int _liveLimited;
        private int _livePaused;
        private int _liveUnknown;
        private int _alertingRules;
        private readonly HashSet<string> _seenRuleIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TechniqueAgg> _byTechnique = new(StringComparer.Ordinal);

        public RuleCoverageAccumulator(IMitreAttackCatalog mitre) => _mitre = mitre;

        public void Add(JsonElement rule)
        {
            if (RuleArchived(rule)) return;   // regra ARQUIVADA não entra nos totais ativos

            // Identidade técnica (name = resource path) usada SÓ para deduplicar — nunca persistida/exposta.
            var id = RuleIdentity(rule);
            if (id is not null && !_seenRuleIds.Add(id)) return;

            _activeRules++;
            var live = RuleLiveMode(rule);
            var alerting = RuleAlerting(rule);
            // A condição de execução SÓ é operacionalmente relevante para regras em live mode (uma regra fora de live
            // mode não roda contra dados ao vivo). Assim os quatro buckets particionam EXATAMENTE _liveRules.
            var exec = live ? RuleExecutionStateOf(rule) : (RuleExecution?)null;
            if (live)
            {
                _liveRules++;
                switch (exec)
                {
                    case RuleExecution.Normal: _liveNormal++; break;
                    case RuleExecution.Limited: _liveLimited++; break;
                    case RuleExecution.Paused: _livePaused++; break;
                    default: _liveUnknown++; break;
                }
            }
            if (alerting) _alertingRules++;

            var techniques = ExtractValidTechniques(rule, _mitre);   // deduplicadas por regra, validadas no catálogo
            if (techniques.Count == 0)
            {
                _withoutMitre++;   // sem técnica MITRE VÁLIDA — diagnóstico de mapeamento ausente/inválido
                return;
            }
            _withMitre++;
            foreach (var tid in techniques)
            {
                if (!_byTechnique.TryGetValue(tid, out var agg)) { agg = new TechniqueAgg(); _byTechnique[tid] = agg; }
                agg.RuleCount++;
                if (live)
                {
                    agg.LiveRuleCount++;
                    switch (exec)
                    {
                        case RuleExecution.Normal: agg.NormalExec++; break;
                        case RuleExecution.Limited: agg.LimitedExec++; break;
                        case RuleExecution.Paused: agg.PausedExec++; break;
                        default: agg.UnknownExec++; break;
                    }
                }
                if (alerting) agg.AlertingRuleCount++;
            }
        }

        public AppDetectionCoverage ToSnapshot(
            string source, string attackVersion, DetectionCoverageCollectionState state, DateTimeOffset attemptedAt)
        {
            var techniques = _byTechnique
                .Select(kv =>
                {
                    var t = _mitre.GetTechnique(kv.Key)!;   // só entram IDs já validados no catálogo (nunca null aqui)
                    return new DetectionTechniqueCoverage(
                        t.Id, t.Name, t.IsSubtechnique, t.ParentId, t.TacticIds,
                        kv.Value.RuleCount, kv.Value.LiveRuleCount,
                        kv.Value.NormalExec, kv.Value.LimitedExec, kv.Value.PausedExec, kv.Value.UnknownExec,
                        kv.Value.AlertingRuleCount);
                })
                .OrderBy(t => t.TechniqueId, StringComparer.Ordinal)
                .ToList();

            return new AppDetectionCoverage(
                source, attackVersion, state, attemptedAt,
                _activeRules, _withMitre, _withoutMitre, _liveRules,
                _liveNormal, _liveLimited, _livePaused, _liveUnknown,
                _alertingRules, techniques);
        }

        private sealed class TechniqueAgg
        {
            public int RuleCount;
            public int LiveRuleCount;
            public int NormalExec;
            public int LimitedExec;
            public int PausedExec;
            public int UnknownExec;
            public int AlertingRuleCount;
        }
    }

    private static IReadOnlyCollection<string> ExtractValidTechniques(JsonElement rule, IMitreAttackCatalog mitre)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);   // dedup POR REGRA
        // O meta compilado da YARA-L chega como um mapa (`metadata`/`meta`) string→string. As chaves MITRE oficiais
        // (`technique`/`mitre_ttp`) carregam o(s) ID(s), possivelmente separados por vírgula e no formato "T#### - Nome".
        CollectTechniquesFromMap(rule, "metadata", found, mitre);
        CollectTechniquesFromMap(rule, "meta", found, mitre);
        // `tags` (quando presentes): SÓ o namespace documentado `google.mitre.technique.<ID>` (tag curta ou resource
        // name completo). `google.mitre.tactic.*` é tática — ignorada aqui; um `T####` avulso numa tag qualquer NÃO é
        // interpretado como MITRE (evita falso mapeamento a partir de texto arbitrário).
        if (rule.ValueKind == JsonValueKind.Object && rule.TryGetProperty("tags", out var tags)
            && tags.ValueKind == JsonValueKind.Array)
            foreach (var t in tags.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String) AddTechniqueFromTag(t.GetString(), found, mitre);
        return found;
    }

    private static void CollectTechniquesFromMap(
        JsonElement rule, string mapProp, HashSet<string> into, IMitreAttackCatalog mitre)
    {
        if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty(mapProp, out var map)
            || map.ValueKind != JsonValueKind.Object)
            return;
        foreach (var kv in map.EnumerateObject())
        {
            var key = kv.Name.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            if (Array.IndexOf(TechniqueMetaKeys, key) < 0) continue;
            if (kv.Value.ValueKind == JsonValueKind.String)
                AddTechniqueTokens(kv.Value.GetString(), into, mitre);
            else if (kv.Value.ValueKind == JsonValueKind.Array)
                foreach (var v in kv.Value.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.String) AddTechniqueTokens(v.GetString(), into, mitre);
        }
    }

    /// <summary>
    /// Extrai IDs de técnica dos campos de metadados DEDICADOS (<c>technique</c>/<c>mitre_ttp</c>), onde qualquer
    /// <c>T####</c> é uma referência de técnica. Valida o FORMATO (case-insensitive), normaliza para o ID canônico
    /// MAIÚSCULO e SÓ então aceita os que EXISTEM no catálogo fixado (v17.1) — nunca inventa nem aproxima.
    /// </summary>
    private static void AddTechniqueTokens(string? raw, HashSet<string> into, IMitreAttackCatalog mitre)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (Match m in TechniqueTokenRegex.Matches(raw))
        {
            var id = m.Value.ToUpperInvariant();   // normaliza SÓ após a validação de formato pela regex
            if (mitre.GetTechnique(id) is not null) into.Add(id);   // validação ESTRITA no catálogo v17.1
        }
    }

    /// <summary>
    /// Extrai a técnica de UMA tag SOMENTE quando ela pertence ao namespace oficial <c>google.mitre.technique</c>
    /// (tag curta ou resource name completo). Nunca interpreta <c>google.mitre.tactic.*</c> como técnica, nem extrai
    /// um <c>T####</c> avulso de uma tag fora do namespace. Formato validado (case-insensitive), normalizado para o
    /// ID canônico MAIÚSCULO e então validado no catálogo fixado (v17.1).
    /// </summary>
    private static void AddTechniqueFromTag(string? raw, HashSet<string> into, IMitreAttackCatalog mitre)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var m = TechniqueTagRegex.Match(raw.Trim());
        if (!m.Success) return;
        var id = m.Groups[1].Value.ToUpperInvariant();   // normaliza SÓ após validar o namespace + formato
        if (mitre.GetTechnique(id) is not null) into.Add(id);   // validação ESTRITA no catálogo v17.1
    }

    private static bool RuleArchived(JsonElement r) =>
        ReadBool(r, "archived") == true
        || (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("deployment", out var d)
            && d.ValueKind == JsonValueKind.Object && ReadBool(d, "archived") == true);

    private static bool RuleLiveMode(JsonElement r)
    {
        if (ReadBool(r, "liveModeEnabled") is { } v) return v;
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("deployment", out var d)
            && d.ValueKind == JsonValueKind.Object && ReadBool(d, "enabled") is { } e) return e;
        return false;
    }

    private static bool RuleAlerting(JsonElement r)
    {
        if (ReadBool(r, "alertingEnabled") is { } v) return v;
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("deployment", out var d)
            && d.ValueKind == JsonValueKind.Object && ReadBool(d, "alerting") is { } a) return a;
        return false;
    }

    /// <summary>
    /// Condição operacional da execução de uma regra (enum oficial <c>executionState</c>), DISTINTA de live mode e de
    /// alerting. <c>DEFAULT</c>=execução normal; <c>LIMITED</c>=execução não garantida; <c>PAUSED</c>=não executa;
    /// <c>EXECUTION_STATE_UNSPECIFIED</c>, ausente ou não reconhecido ⇒ <see cref="RuleExecution.Unknown"/> (estado não
    /// comprovado — nunca assumido como saudável). Lê o campo no topo da regra e, como defesa, no <c>deployment</c>.
    /// </summary>
    private static RuleExecution RuleExecutionStateOf(JsonElement r)
    {
        var raw = FieldStr(r, "executionState");
        if (string.IsNullOrWhiteSpace(raw) && r.ValueKind == JsonValueKind.Object
            && r.TryGetProperty("deployment", out var d) && d.ValueKind == JsonValueKind.Object)
            raw = FieldStr(d, "executionState");
        return (raw?.Trim().ToUpperInvariant()) switch
        {
            "DEFAULT" => RuleExecution.Normal,
            "LIMITED" => RuleExecution.Limited,
            "PAUSED" => RuleExecution.Paused,
            // EXECUTION_STATE_UNSPECIFIED, ausente ou valor futuro/desconhecido: NÃO comprovado.
            _ => RuleExecution.Unknown,
        };
    }

    /// <summary>Condição de execução PROVIDER-NEUTRAL derivada do <c>executionState</c> oficial.</summary>
    private enum RuleExecution { Normal, Limited, Paused, Unknown }

    /// <summary>Identidade ESTÁVEL da regra (name = resource path; ou ruleId/revisionId) — SÓ para deduplicar.</summary>
    private static string? RuleIdentity(JsonElement r)
    {
        var name = FieldStr(r, "name");
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        var alt = FieldStr(r, "ruleId") ?? FieldStr(r, "revisionId");
        return string.IsNullOrWhiteSpace(alt) ? null : alt.Trim();
    }

    private static bool? ReadBool(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    // ---- Dimensão de CASOS (inventário atual) — agregação INCREMENTAL, sem materializar a coleção ---

    private async Task<(SiemCasePosture Posture, ChronicleApiException? Error)> CollectCasesAsync(
        string token, GoogleSecOpsSettings s, CancellationToken ct)
    {
        var acc = new CaseAccumulator();
        try
        {
            // O transporte ENTREGA cada caso ao acumulador (piso mínimo) e devolve se a coleta foi PARCIAL. Falha na
            // PRIMEIRA página lança (sem piso) → dimensão falha classificada; falha APÓS ≥1 página preserva o piso e
            // marca Partial. Nenhum objeto de caso é retido após a agregação por página.
            var isPartial = await _api.CollectCasesAsync(token, s.ProjectId, s.Location, s.InstanceId, acc.Add, ct);
            return (acc.ToPosture(isPartial), null);
        }
        catch (ChronicleApiException ex)
        {
            _log?.LogInformation("Google SecOps: coleta de casos não comprovada ({Kind}).", ex.Kind);
            return (FailedCases(StateFrom(ex)), ex);
        }
    }

    private static SiemCasePosture FailedCases(SiemCollectionState state) => new(
        State: state, Period: SiemPeriodKind.CurrentInventory, WindowDays: null, IsComplete: false,
        Observed: null, Open: null, New: null, Closed: null,
        OpenHighSeverity: null, OpenMediumSeverity: null, OpenLowSeverity: null, OpenInformationalSeverity: null,
        OpenByPriority: null, MeanTimeToCloseHours: null, LastEvidenceAt: null);

    /// <summary>Situação oficial de um caso (<c>Case.status</c>) reduzida ao que a postura precisa distinguir.</summary>
    private enum CaseStatusKind { Opened, Closed, Other, Unknown }

    /// <summary>
    /// Acumulador MÍNIMO de casos, alimentado PÁGINA A PÁGINA — total observado, abertos, fechados, distribuição por
    /// prioridade dos ABERTOS e a última evidência — para NÃO reter os objetos completos (evita materializar centenas de
    /// milhares de JsonElement). Interpreta EXCLUSIVAMENTE os campos oficiais <c>status</c>, <c>priority</c> e
    /// <c>updateTime</c>. É INVENTÁRIO ATUAL (a listagem não garante filtro temporal), então não há "novo".
    /// </summary>
    private sealed class CaseAccumulator
    {
        private int _observed;
        private int _open;
        private int _closed;
        private readonly Dictionary<string, int> _openByPriority = new(StringComparer.Ordinal);
        private DateTimeOffset? _last;

        public void Add(JsonElement c)
        {
            _observed++;
            switch (CaseStatus(c))
            {
                case CaseStatusKind.Opened:
                    _open++;
                    var priority = CasePriority(c);   // distribuição por prioridade SÓ dos abertos
                    if (priority is not null)
                        _openByPriority[priority] = _openByPriority.GetValueOrDefault(priority) + 1;
                    break;
                case CaseStatusKind.Closed:
                    _closed++;
                    break;
                // MERGED / CREATION_PENDING / CASE_DATA_STATE_UNSPECIFIED / desconhecido / ausente: contam SÓ no total
                // observado — NUNCA presumidos como abertos ou fechados.
            }

            var updated = CaseUpdateTime(c);   // epoch-millis (int64); ausente/inválido → ignorado p/ LastEvidenceAt
            if (updated is { } d && (_last is null || d > _last)) _last = d;
        }

        public SiemCasePosture ToPosture(bool isPartial)
        {
            var priorityList = _openByPriority.Count == 0
                ? null
                : _openByPriority.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new SiemPriorityCount(kv.Key, kv.Value)).ToList();
            var state = isPartial ? SiemCollectionState.Partial : SiemCollectionState.Available;
            return new SiemCasePosture(
                State: state,
                Period: SiemPeriodKind.CurrentInventory,
                WindowDays: null,
                IsComplete: !isPartial,
                Observed: _observed,
                Open: _open,
                New: null,
                Closed: _closed,
                OpenHighSeverity: null, OpenMediumSeverity: null, OpenLowSeverity: null, OpenInformationalSeverity: null,
                OpenByPriority: priorityList,
                MeanTimeToCloseHours: null,
                LastEvidenceAt: _last);
        }
    }

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
    /// Agrega os itens de alerta ACHATADOS dos dois agrupamentos oficiais (ativo e usuário). Deduplica por identidade
    /// ESTÁVEL da origem: preferindo <c>alertNumber</c> (reconhece o MESMO alerta associado a ativo E a usuário), com
    /// <c>uid</c>/<c>eventLogToken</c> apenas como fallback. Um item SEM identidade confiável NÃO é contado como total
    /// confiável nem vira hash com PII — apenas marca a dimensão PARCIAL (os identificáveis ficam como piso). Severidade
    /// só quando a fonte a fornece; instante por <c>alertTime</c>. <c>moreDataAvailable</c>, um teto interno OU um item
    /// sem identidade ⇒ PARCIAL. Nunca projeta ativo, usuário, evento UDM, displayName, filterProperties, título ou payload.
    /// </summary>
    private static SiemAlertPosture AggregateAlerts(ChronicleAlertSearchResult result)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var observed = 0;
        var high = 0;
        var medium = 0;
        var severityKnown = 0;
        var hasUnidentified = false;
        DateTimeOffset? last = null;

        foreach (var a in result.AlertInfos)
        {
            var id = AlertIdentity(a);
            if (id is null)
            {
                hasUnidentified = true;   // sem identidade confiável → não conta e degrada (nunca inventa hash com PII)
                continue;
            }
            if (!seen.Add(id)) continue;   // dedup ativo+usuário pelo MESMO alertNumber (ou fallback estável)
            observed++;

            var rank = AlertSeverityRank(a);
            if (rank >= 0)
            {
                severityKnown++;
                if (rank == 0) high++;
                else if (rank == 1) medium++;
            }

            var ts = ReadDate(a, "alertTime");
            if (ts is { } d && (last is null || d > last)) last = d;
        }

        var partial = result.IsPartial || hasUnidentified;
        var state = partial ? SiemCollectionState.Partial : SiemCollectionState.Available;
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
        // AuthFailure/Unauthorized/InvalidRequest/Unavailable/InvalidResponse: dimensão não comprovada.
        _ => SiemCollectionState.Unavailable,
    };

    // ---- Parsing operacional dos campos OFICIAIS (defensivo) ---------------------------------------

    /// <summary>
    /// Situação de um caso a partir do campo OFICIAL <c>status</c>. SEM fallback para <c>stage</c> (que é fase de
    /// triagem — "Triage"/"Investigation"/"Incident" —, não indicador de fechamento). Ausente/desconhecido → Unknown
    /// (nem aberto nem fechado). MERGED/CREATION_PENDING/CASE_DATA_STATE_UNSPECIFIED → Other.
    /// </summary>
    private static CaseStatusKind CaseStatus(JsonElement c) => (FieldStr(c, "status")?.Trim().ToUpperInvariant()) switch
    {
        "OPENED" => CaseStatusKind.Opened,
        "CLOSED" => CaseStatusKind.Closed,
        "MERGED" or "CREATION_PENDING" or "CASE_DATA_STATE_UNSPECIFIED" => CaseStatusKind.Other,
        _ => CaseStatusKind.Unknown,
    };

    /// <summary>
    /// <c>Case.updateTime</c> é uma string no formato <c>int64</c> com epoch em MILISSEGUNDOS. Converte com
    /// <see cref="DateTimeOffset.FromUnixTimeMilliseconds"/>; ausente, tipo inesperado, não-numérico, negativo ou fora
    /// do intervalo → <c>null</c> (ignorado para LastEvidenceAt, SEM invalidar as demais contagens).
    /// </summary>
    private static DateTimeOffset? CaseUpdateTime(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("updateTime", out var v)) return null;
        long millis;
        switch (v.ValueKind)
        {
            case JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m):
                millis = m; break;
            case JsonValueKind.Number when v.TryGetInt64(out var m):
                millis = m; break;
            default:
                return null;
        }
        if (millis < 0) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(millis); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Prioridade OFICIAL do caso (<c>PRIORITY_*</c>) — valor bruto preservado, nunca reinterpretado. Null quando ausente.</summary>
    private static string? CasePriority(JsonElement c)
    {
        var raw = FieldStr(c, "priority");
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>
    /// Identidade ESTÁVEL de um item de alerta para deduplicar. Prefere <c>alertNumber</c> (reconhece o MESMO alerta
    /// associado a ativo E a usuário); <c>uid</c> e <c>eventLogToken</c> só como fallback estável. Prefixa por tipo
    /// para não colidir entre eixos. <c>null</c> quando não há identidade confiável — nunca deriva hash com PII.
    /// </summary>
    private static string? AlertIdentity(JsonElement a)
    {
        var num = FieldStrOrNumber(a, "alertNumber");
        if (!string.IsNullOrWhiteSpace(num)) return "n:" + num.Trim();
        var uid = FieldStr(a, "uid");
        if (!string.IsNullOrWhiteSpace(uid)) return "u:" + uid.Trim();
        var tok = FieldStr(a, "eventLogToken");
        if (!string.IsNullOrWhiteSpace(tok)) return "t:" + tok.Trim();
        return null;
    }

    /// <summary>Rank de severidade do alerta (campo oficial <c>severity</c>): 0=alto (HIGH/CRITICAL), 1=médio (MEDIUM/MODERATE), 2=outro conhecido, -1=ausente.</summary>
    private static int AlertSeverityRank(JsonElement a)
    {
        var raw = FieldStr(a, "severity");
        if (string.IsNullOrWhiteSpace(raw)) return -1;
        return raw.Trim().ToUpperInvariant() switch
        {
            "CRITICAL" or "HIGH" => 0,
            "MEDIUM" or "MODERATE" => 1,
            _ => 2,
        };
    }

    private static string? FieldStr(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>Lê um campo como string OU número (ex.: <c>alertNumber</c>, que a fonte pode enviar como int64-string ou número). Null se ausente/outro tipo.</summary>
    private static string? FieldStrOrNumber(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    /// <summary>Lê um instante RFC 3339 (ex.: <c>AssetAlertInfo.alertTime</c>). Ausente/inválido → null.</summary>
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
