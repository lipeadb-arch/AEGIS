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
using AppDevicePosture = AegisScore.Application.Abstractions.DevicePostureSnapshot;

namespace AegisScore.Connectors.Microsoft.Intune;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-02] Coletor REAL do Microsoft Intune (somente leitura), via Microsoft Graph v1.0.
/// É o quinto serviço INDEPENDENTE da conexão Microsoft unificada (Microsoft/ConfigAnalyzer) — mesma credencial,
/// estado/teste/sincronização/lifecycle próprios.
///
/// Endpoints oficiais (todos <c>GET</c>, app-only, Graph v1.0):
///  • <c>/deviceManagement/deviceCompliancePolicies</c> — permissão <c>DeviceManagementConfiguration.Read.All</c>;
///  • <c>/deviceManagement/deviceConfigurations</c>     — permissão <c>DeviceManagementConfiguration.Read.All</c>;
///  • <c>/deviceManagement/managedDevices</c>           — permissão <c>DeviceManagementManagedDevices.Read.All</c>.
///
/// REUTILIZA o transporte ENDURECIDO já validado (<see cref="IEntraGraphClient"/>): client credentials, host
/// oficial FIXO, paginação por <c>@odata.nextLink</c> validado contra o host do Graph (o bearer nunca segue para
/// origem forjada), <c>CancellationToken</c> ponta a ponta, teto de páginas e classificação de 401/403/429/5xx.
/// Não há segunda infra de OAuth/paginação/allowlist. A resiliência (retry/backoff e <c>Retry-After</c> no 429)
/// vem do <c>AddStandardResilienceHandler</c> do typed HttpClient — a mesma dos demais conectores Microsoft.
///
/// DUAS DIMENSÕES INDEPENDENTES, cada uma com estado EXPLÍCITO:
///  (1) postura configurada (políticas + atribuição, quando comprovável);
///  (2) estado efetivo dos dispositivos gerenciados.
/// A ausência de <c>DeviceManagementManagedDevices.Read.All</c> degrada SOMENTE a dimensão (2). Nenhuma falha
/// classificável vira zero, coleção vazia ou exceção: vira estado. Só o cancelamento SOLICITADO propaga.
///
/// FRONTEIRA DE AUTORIDADE: implementa <see cref="IEvidenceConnector"/> apenas para participar do registry e do
/// ciclo de vida (teste/ativação/sincronização) — <see cref="CollectAsync"/> NUNCA emite um único
/// <see cref="EvidenceSignal"/>. Nada aqui mapeia NIST, altera <c>TenantControlState</c> ou gera pontos.
///
/// PRIVACIDADE: os dispositivos são normalizados em GRUPOS agregados. Nenhum identificador de dispositivo,
/// nome, usuário, e-mail, número de série, IMEI, telefone ou MAC é lido, persistido ou registrado em log — o
/// <c>$select</c> já limita o que trafega, e o parser só reconhece os campos da allowlist.
/// </summary>
public sealed class MicrosoftIntuneDevicePostureConnector : IEvidenceConnector, IDevicePostureCollector
{
    /// <summary>Rótulo estável da fonte — exibido na tela; nunca endpoint, permissão ou credencial.</summary>
    public const string SourceLabel = "Microsoft Intune";

    /// <summary>Permissão de aplicativo da dimensão de POSTURA CONFIGURADA (políticas/configurações).</summary>
    public const string ConfigurationPermission = "DeviceManagementConfiguration.Read.All";

    /// <summary>Permissão de aplicativo da dimensão de ESTADO EFETIVO (dispositivos gerenciados).</summary>
    public const string ManagedDevicesPermission = "DeviceManagementManagedDevices.Read.All";

    // Consultas com $expand=assignments: a atribuição vem NA MESMA paginação, sem N+1 (uma chamada por política).
    private const string CompliancePoliciesExpandUrl = "deviceManagement/deviceCompliancePolicies?$expand=assignments";
    private const string DeviceConfigurationsExpandUrl = "deviceManagement/deviceConfigurations?$expand=assignments";
    // Fallback SEM $expand: se o contrato não aceitar a expansão, as políticas continuam sendo lidas e a
    // sub-dimensão de atribuição fica explicitamente indisponível — jamais "0 não atribuídas".
    private const string CompliancePoliciesUrl = "deviceManagement/deviceCompliancePolicies";
    private const string DeviceConfigurationsUrl = "deviceManagement/deviceConfigurations";

    /// <summary>$select MINIMIZANTE: só o que a postura precisa. Nada de UPN, nome, serial, IMEI, telefone ou MAC.</summary>
    private const string ManagedDevicesSelectUrl =
        "deviceManagement/managedDevices?$select=id,azureADDeviceId,complianceState,lastSyncDateTime,operatingSystem,isEncrypted";
    private const string ManagedDevicesUrl = "deviceManagement/managedDevices";

    /// <summary>Consulta mínima do teste de conexão (autentica + prova a leitura da dimensão de configuração).</summary>
    private const string ProbeUrl = "deviceManagement/deviceCompliancePolicies?$top=1";

    /// <summary>Teto defensivo de políticas POR FAMÍLIA. Ultrapassar não falha: marca a dimensão como piso (Partial).</summary>
    internal const int MaxPoliciesPerKind = 1_000;

    /// <summary>Teto defensivo de dispositivos lidos. Ultrapassar não falha: marca a dimensão como piso (Partial).</summary>
    internal const int MaxDevices = 100_000;

    /// <summary>Teto de grupos agregados distintos. O excedente é colapsado em "Outros" e a dimensão vira piso.</summary>
    internal const int MaxDeviceGroups = 400;

    /// <summary>Janela padrão de obsolescência por última sincronização (dias).</summary>
    internal const int StaleThresholdDays = 30;

    private const int MaxDisplayNameLength = 200;
    private const int MaxOperatingSystemLength = 60;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IEntraGraphClient _graph;
    private readonly IConnectorSecretProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogger<MicrosoftIntuneDevicePostureConnector>? _log;

    public MicrosoftIntuneDevicePostureConnector(
        IEntraGraphClient graph,
        IConnectorSecretProtector protector,
        TimeProvider? time = null,
        ILogger<MicrosoftIntuneDevicePostureConnector>? log = null)
    {
        _graph = graph;
        _protector = protector;
        _time = time ?? TimeProvider.System;
        _log = log;
    }

    public ConnectorProvider Provider => ConnectorProvider.Microsoft;
    public ConnectorCapability Capability => ConnectorCapability.ConfigAnalyzer;

    // ---- Teste de conexão ---------------------------------------------------------------------------

    public async Task<ConnectorHealth> TestAsync(ConnectorConfig config, CancellationToken ct)
    {
        var creds = DecryptCredentials(config);
        if (creds is null)
            return new ConnectorHealth(ConnectorStatus.Degraded, "Conector não configurado ou credenciais ilegíveis.");

        try
        {
            var token = await _graph.AcquireTokenAsync(creds, ct);
            var root = await _graph.GetJsonAsync(token, creds, ProbeUrl, ct);
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("value", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return new ConnectorHealth(ConnectorStatus.Failed,
                    "O Microsoft Intune respondeu fora do contrato esperado.");

            // Autenticado e lendo a dimensão de CONFIGURAÇÃO. A dimensão de dispositivos NÃO foi provada aqui:
            // o teste jamais declara o conector "plenamente operacional" com uma só dimensão validada.
            return new ConnectorHealth(ConnectorStatus.Degraded,
                "Leitura de políticas do Intune confirmada. O estado efetivo dos dispositivos só é validado na " +
                $"sincronização e exige a permissão {ManagedDevicesPermission}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EntraGraphException ex)
        {
            return ClassifyDimension(ex) switch
            {
                DevicePostureDimensionState.NotAuthorized => new ConnectorHealth(ConnectorStatus.Failed,
                    $"Permissão insuficiente — conceda {ConfigurationPermission} à aplicação."),
                DevicePostureDimensionState.NotLicensed => new ConnectorHealth(ConnectorStatus.Failed,
                    "O tenant não tem o Microsoft Intune disponível (licença/capacidade ausente)."),
                _ when ex.Kind == EntraGraphErrorKind.AuthFailure => new ConnectorHealth(ConnectorStatus.Failed,
                    "Falha de autenticação junto ao Microsoft Graph."),
                _ when ex.Kind == EntraGraphErrorKind.Throttled => new ConnectorHealth(ConnectorStatus.Degraded,
                    "Throttling do Microsoft Graph; tente novamente em instantes."),
                _ => new ConnectorHealth(ConnectorStatus.Failed,
                    "Microsoft Graph indisponível para a leitura do Microsoft Intune."),
            };
        }
    }

    // ---- IEvidenceConnector: ZERO sinais -------------------------------------------------------------

    /// <summary>
    /// NUNCA emite sinal. O Intune é fonte de EVIDÊNCIA OPERACIONAL consultiva: presença de política ou
    /// dispositivo conforme não é prova determinística de controle NIST e não pode gerar pontos. A implementação
    /// existe apenas para o conector participar do registry/lifecycle (teste, ativação, sincronização).
    /// </summary>
#pragma warning disable CS1998   // sem await: a ausência de sinais é o comportamento correto, não um esquecimento
    public async IAsyncEnumerable<EvidenceSignal> CollectAsync(
        ConnectorConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        yield break;
    }
#pragma warning restore CS1998

    // ---- IDevicePostureCollector: as duas dimensões --------------------------------------------------

    public async Task<AppDevicePosture> CollectDevicePostureAsync(ConnectorConfig config, CancellationToken ct)
    {
        var attemptedAt = _time.GetUtcNow();
        var creds = DecryptCredentials(config);
        if (creds is null)
        {
            const string detail = "Conector do Intune sem credenciais legíveis.";
            return new AppDevicePosture(
                SourceLabel,
                DevicePostureConfigurationDimension.Failed(DevicePostureDimensionState.Unavailable, attemptedAt, detail),
                DevicePostureDeviceDimension.Failed(DevicePostureDimensionState.Unavailable, attemptedAt, detail, StaleThresholdDays));
        }

        // UM token para as DUAS dimensões. Falha de autenticação degrada AMBAS (é dependência dura das duas).
        string token;
        try
        {
            token = await _graph.AcquireTokenAsync(creds, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EntraGraphException ex)
        {
            var state = ClassifyDimension(ex);
            var detail = Describe(ex, "autenticação no Microsoft Graph");
            _log?.LogWarning("Intune: autenticação falhou ({State}).", state);
            return new AppDevicePosture(
                SourceLabel,
                DevicePostureConfigurationDimension.Failed(state, attemptedAt, detail),
                DevicePostureDeviceDimension.Failed(state, attemptedAt, detail, StaleThresholdDays));
        }

        var configuration = await CollectConfigurationAsync(token, creds, attemptedAt, ct);
        var devices = await CollectDevicesAsync(token, creds, attemptedAt, ct);
        return new AppDevicePosture(SourceLabel, configuration, devices);
    }

    // ---- Dimensão 1: postura configurada -------------------------------------------------------------

    private async Task<DevicePostureConfigurationDimension> CollectConfigurationAsync(
        string token, IMicrosoftGraphCredentials creds, DateTimeOffset attemptedAt, CancellationToken ct)
    {
        var compliance = await ReadPoliciesAsync(
            token, creds, DevicePolicyKind.CompliancePolicy, CompliancePoliciesExpandUrl, CompliancePoliciesUrl, ct);
        var configurations = await ReadPoliciesAsync(
            token, creds, DevicePolicyKind.DeviceConfiguration, DeviceConfigurationsExpandUrl, DeviceConfigurationsUrl, ct);

        // AS DUAS famílias falharam sem nenhum item ⇒ a dimensão INTEIRA não foi comprovada. O estado mais
        // específico manda (permissão/licença explicam melhor que "indisponível") — nunca vira zero.
        if (compliance.HardFailure is { } cf && configurations.HardFailure is { } gf)
            return DevicePostureConfigurationDimension.Failed(
                MoreSpecific(cf, gf), attemptedAt, Join(compliance.Detail, configurations.Detail));

        var policies = new List<DevicePolicyFact>(compliance.Policies.Count + configurations.Policies.Count);
        policies.AddRange(compliance.Policies);
        policies.AddRange(configurations.Policies);

        var invalid = compliance.Invalid + configurations.Invalid;
        var truncated = compliance.Truncated || configurations.Truncated;
        // UMA família falhou e a outra não: preserva os fatos válidos já lidos, mas a dimensão é um PISO —
        // o reconciliador, ao ver Partial, NÃO rebaixa um inventário completo anterior.
        var partialFamily = compliance.HardFailure is not null || configurations.HardFailure is not null;

        // A dimensão é PISO quando alguma família falhou, foi truncada pelo teto ou trouxe registros inválidos.
        var state = truncated || invalid > 0 || partialFamily
            ? DevicePostureDimensionState.Partial
            : DevicePostureDimensionState.Available;

        // Sub-dimensão de ATRIBUIÇÃO: derivada do que a fonte realmente devolveu, nunca presumida.
        var known = policies.Count(p => p.AssignmentState != DevicePolicyAssignmentState.Unknown);
        var assignmentState = policies.Count == 0
            ? state                                             // sem políticas não há o que atribuir
            : known == policies.Count ? DevicePostureDimensionState.Available
            : known == 0 ? DevicePostureDimensionState.Unavailable
            : DevicePostureDimensionState.Partial;

        var detail = Join(compliance.Detail, configurations.Detail);
        return new DevicePostureConfigurationDimension(
            state, attemptedAt, policies, assignmentState, invalid, detail);
    }

    /// <summary>
    /// Lê UMA família de políticas com <c>$expand=assignments</c>. Se a expansão for recusada pelo contrato
    /// (ex.: 400), repete SEM a expansão — as políticas continuam sendo lidas e a atribuição fica desconhecida.
    /// Falha CLASSIFICÁVEL sem nenhum item vira <see cref="PolicyRead.HardFailure"/>; falha DEPOIS de itens já
    /// lidos vira piso (<c>Truncated</c>) — nunca uma coleção vazia.
    /// </summary>
    private async Task<PolicyRead> ReadPoliciesAsync(
        string token, IMicrosoftGraphCredentials creds, DevicePolicyKind kind,
        string expandUrl, string plainUrl, CancellationToken ct)
    {
        var first = await ReadPolicyPageAsync(token, creds, kind, expandUrl, ct);

        // A expansão não é um contrato v1.0 GARANTIDO. Se ela falhou SEM nenhum item e o erro não é de
        // permissão/licença/autenticação, tenta de novo sem $expand antes de declarar a dimensão indisponível.
        if (first.HardFailure == DevicePostureDimensionState.Unavailable && first.Policies.Count == 0)
        {
            var plain = await ReadPolicyPageAsync(token, creds, kind, plainUrl, ct);
            if (plain.HardFailure is null)
                return plain with { Detail = Join("Atribuições não retornadas pela fonte.", plain.Detail) };
            return plain;
        }

        return first;
    }

    private async Task<PolicyRead> ReadPolicyPageAsync(
        string token, IMicrosoftGraphCredentials creds, DevicePolicyKind kind, string url, CancellationToken ct)
    {
        var policies = new List<DevicePolicyFact>();
        var invalid = 0;
        var truncated = false;
        string? detail = null;

        try
        {
            await foreach (var item in _graph.GetPagedAsync(token, creds, url, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (policies.Count >= MaxPoliciesPerKind)
                {
                    truncated = true;
                    detail = Join(detail, $"Leitura de políticas truncada no teto de {MaxPoliciesPerKind}.");
                    break;
                }

                var fact = ParsePolicy(item, kind);
                if (fact is null) { invalid++; continue; }
                policies.Add(fact);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EntraGraphException ex)
        {
            var state = ClassifyDimension(ex);
            var reason = Describe(ex, DescribeKind(kind));
            if (policies.Count == 0)
                return new PolicyRead(policies, invalid, truncated, state, reason);

            // Falha numa página INTERMEDIÁRIA: o que já foi lido continua válido — a dimensão vira PISO.
            truncated = true;
            detail = Join(detail, reason);
        }

        return new PolicyRead(policies, invalid, truncated, null, detail);
    }

    /// <summary>
    /// Entre dois estados de falha, o MAIS ESPECÍFICO (permissão/licença) explica melhor a lacuna que o genérico
    /// "indisponível". Nenhum dos dois é dado: a escolha só melhora a ação que a tela recomenda ao operador.
    /// </summary>
    private static DevicePostureDimensionState MoreSpecific(
        DevicePostureDimensionState a, DevicePostureDimensionState b) =>
        a == DevicePostureDimensionState.Unavailable ? b : a;

    /// <summary>Resultado da leitura de UMA família de políticas. <c>HardFailure</c> só existe sem nenhum item lido.</summary>
    private sealed record PolicyRead(
        List<DevicePolicyFact> Policies,
        int Invalid,
        bool Truncated,
        DevicePostureDimensionState? HardFailure,
        string? Detail);

    /// <summary>
    /// Normaliza UMA política. Rejeita (conta como inválida) um item sem <c>id</c> utilizável. Lê SOMENTE
    /// <c>id</c>, <c>displayName</c>, <c>@odata.type</c>, <c>lastModifiedDateTime</c> e a coleção
    /// <c>assignments</c> — nunca <c>description</c>, ajustes ou qualquer valor configurado.
    /// </summary>
    private static DevicePolicyFact? ParsePolicy(JsonElement item, DevicePolicyKind kind)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var id = StrOf(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var name = StrOf(item, "displayName");
        var odataType = StrOf(item, "@odata.type");

        var (assignmentState, assignmentCount) = ReadAssignments(item);

        return new DevicePolicyFact(
            ExternalId: Trim(id!, 200)!,
            Kind: kind,
            DisplayName: Trim(name, MaxDisplayNameLength) ?? "(sem nome)",
            PlatformLabel: PlatformFromODataType(odataType),
            AssignmentState: assignmentState,
            AssignmentCount: assignmentCount,
            SourceLastModifiedAt: DateOf(item, "lastModifiedDateTime"));
    }

    /// <summary>
    /// Atribuição a partir da coleção expandida. AUSÊNCIA da propriedade (ou tipo inesperado) ⇒ desconhecida —
    /// jamais "não atribuída". Coleção presente e VAZIA ⇒ objetivamente não atribuída.
    /// </summary>
    private static (DevicePolicyAssignmentState State, int? Count) ReadAssignments(JsonElement item)
    {
        if (!item.TryGetProperty("assignments", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return (DevicePolicyAssignmentState.Unknown, null);

        var count = arr.GetArrayLength();
        return count > 0
            ? (DevicePolicyAssignmentState.Assigned, count)
            : (DevicePolicyAssignmentState.Unassigned, 0);
    }

    /// <summary>
    /// Plataforma DETERMINÍSTICA a partir do <c>@odata.type</c> declarado pela fonte (ex.:
    /// <c>#microsoft.graph.windows10CompliancePolicy</c> ⇒ "Windows"). Nunca adivinha pelo nome da política:
    /// tipo não reconhecido ⇒ <c>null</c> (indeterminado), nunca um rótulo inventado.
    /// </summary>
    internal static string? PlatformFromODataType(string? odataType)
    {
        if (string.IsNullOrWhiteSpace(odataType)) return null;
        var t = odataType!.ToLowerInvariant();
        if (t.Contains("windowsphone")) return "Windows Phone";
        if (t.Contains("windows")) return "Windows";
        if (t.Contains("macos")) return "macOS";
        if (t.Contains("ios")) return "iOS";
        if (t.Contains("androidwork") || t.Contains("androiddeviceowner") || t.Contains("aospdeviceowner")) return "Android Enterprise";
        if (t.Contains("android")) return "Android";
        return null;
    }

    // ---- Dimensão 2: estado efetivo dos dispositivos -------------------------------------------------

    private async Task<DevicePostureDeviceDimension> CollectDevicesAsync(
        string token, IMicrosoftGraphCredentials creds, DateTimeOffset attemptedAt, CancellationToken ct)
    {
        var read = await ReadDevicesAsync(token, creds, ManagedDevicesSelectUrl, ct);

        // O $select minimizante não é um contrato garantido para toda versão da fonte. Se ele foi recusado SEM
        // nenhum item (e não por permissão/licença/autenticação), repete sem $select — o parser continua lendo
        // SOMENTE os campos da allowlist, então nenhum campo sensível é aproveitado mesmo vindo na resposta.
        if (read.HardFailure == DevicePostureDimensionState.Unavailable && read.Total == 0)
            read = await ReadDevicesAsync(token, creds, ManagedDevicesUrl, ct);

        if (read.HardFailure is { } failure)
            return DevicePostureDeviceDimension.Failed(failure, attemptedAt, read.Detail, StaleThresholdDays);

        var groups = read.Groups
            .Select(kv => new DeviceGroupFact(kv.Key.Os, kv.Key.Compliance, kv.Key.Encryption, kv.Key.Activity, kv.Value))
            .OrderByDescending(g => g.DeviceCount)
            .ThenBy(g => g.OperatingSystem, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => (int)g.Compliance)
            .ThenBy(g => (int)g.Encryption)
            .ThenBy(g => (int)g.Activity)
            .ToList();

        var state = read.Truncated || read.Invalid > 0
            ? DevicePostureDimensionState.Partial
            : DevicePostureDimensionState.Available;

        return new DevicePostureDeviceDimension(
            state, attemptedAt, groups, read.Total, StaleThresholdDays,
            read.WithDirectoryId, read.Invalid, read.Detail);
    }

    private async Task<DeviceRead> ReadDevicesAsync(
        string token, IMicrosoftGraphCredentials creds, string url, CancellationToken ct)
    {
        var groups = new Dictionary<DeviceGroupKey, int>();
        var total = 0;
        var invalid = 0;
        var withDirectoryId = 0;
        var truncated = false;
        string? detail = null;
        var now = _time.GetUtcNow();

        try
        {
            await foreach (var item in _graph.GetPagedAsync(token, creds, url, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (total >= MaxDevices)
                {
                    truncated = true;
                    detail = Join(detail, $"Leitura de dispositivos truncada no teto de {MaxDevices}.");
                    break;
                }

                if (item.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(StrOf(item, "id")))
                {
                    invalid++;
                    continue;
                }

                total++;
                if (!string.IsNullOrWhiteSpace(StrOf(item, "azureADDeviceId"))) withDirectoryId++;

                var key = new DeviceGroupKey(
                    NormalizeOs(StrOf(item, "operatingSystem")),
                    ComplianceOf(StrOf(item, "complianceState")),
                    EncryptionOf(item),
                    ActivityOf(DateOf(item, "lastSyncDateTime"), now));

                // Teto de cardinalidade: o excedente é colapsado num grupo "Outros" do MESMO recorte de estado —
                // a contagem total continua exata e a dimensão vira piso (o detalhamento por SO é que é parcial).
                if (!groups.ContainsKey(key) && groups.Count >= MaxDeviceGroups)
                {
                    key = key with { Os = OtherOsLabel };
                    truncated = true;
                }

                groups[key] = groups.TryGetValue(key, out var n) ? n + 1 : 1;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EntraGraphException ex)
        {
            var state = ClassifyDimension(ex);
            var reason = Describe(ex, "dispositivos gerenciados");
            if (total == 0)
                return new DeviceRead(groups, 0, invalid, 0, truncated, state, reason);

            // Falha numa página INTERMEDIÁRIA: preserva o que já foi contado e marca piso — nunca zera.
            truncated = true;
            detail = Join(detail, reason);
        }

        return new DeviceRead(groups, total, invalid, withDirectoryId, truncated, null, detail);
    }

    private sealed record DeviceRead(
        Dictionary<DeviceGroupKey, int> Groups,
        int Total,
        int Invalid,
        int WithDirectoryId,
        bool Truncated,
        DevicePostureDimensionState? HardFailure,
        string? Detail);

    private readonly record struct DeviceGroupKey(
        string Os, DeviceComplianceBucket Compliance, DeviceEncryptionBucket Encryption, DeviceActivityBucket Activity);

    internal const string UnknownOsLabel = "Não informado";
    internal const string OtherOsLabel = "Outros";

    internal static string NormalizeOs(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return UnknownOsLabel;
        return trimmed.Length > MaxOperatingSystemLength ? trimmed[..MaxOperatingSystemLength] : trimmed;
    }

    /// <summary>
    /// Traduz o <c>complianceState</c> oficial para o vocabulário do AEGIS. Um valor NOVO (ou ausente) que a
    /// Microsoft venha a introduzir cai em <see cref="DeviceComplianceBucket.Unknown"/> — o coletor não quebra e,
    /// principalmente, o dispositivo NUNCA é contado como conforme por desconhecimento.
    /// </summary>
    internal static DeviceComplianceBucket ComplianceOf(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "compliant" => DeviceComplianceBucket.Compliant,
            "noncompliant" => DeviceComplianceBucket.Noncompliant,
            "ingraceperiod" => DeviceComplianceBucket.InGracePeriod,
            "conflict" => DeviceComplianceBucket.Conflict,
            "error" => DeviceComplianceBucket.Error,
            "configmanager" => DeviceComplianceBucket.ManagedExternally,
            _ => DeviceComplianceBucket.Unknown,
        };

    /// <summary>Criptografia SÓ quando a fonte informou o booleano; ausência/tipo inesperado ⇒ desconhecida.</summary>
    internal static DeviceEncryptionBucket EncryptionOf(JsonElement item) =>
        item.TryGetProperty("isEncrypted", out var e)
            ? e.ValueKind switch
            {
                JsonValueKind.True => DeviceEncryptionBucket.Encrypted,
                JsonValueKind.False => DeviceEncryptionBucket.NotEncrypted,
                _ => DeviceEncryptionBucket.Unknown,
            }
            : DeviceEncryptionBucket.Unknown;

    /// <summary>Atividade por última sincronização; sem o instante (ou com instante inválido) ⇒ desconhecida.</summary>
    internal static DeviceActivityBucket ActivityOf(DateTimeOffset? lastSync, DateTimeOffset now)
    {
        if (lastSync is null) return DeviceActivityBucket.Unknown;
        return now - lastSync.Value > TimeSpan.FromDays(StaleThresholdDays)
            ? DeviceActivityBucket.Stale
            : DeviceActivityBucket.Active;
    }

    // ---- Classificação e sanitização ------------------------------------------------------------------

    /// <summary>
    /// Traduz uma falha do transporte no estado da dimensão. 404 (recurso/capacidade ausente) e um código de erro
    /// que mencione licença ⇒ <see cref="DevicePostureDimensionState.NotLicensed"/>; 403 ⇒
    /// <see cref="DevicePostureDimensionState.NotAuthorized"/>; o resto ⇒ <see cref="DevicePostureDimensionState.Unavailable"/>.
    /// NENHUM caminho devolve Available/Partial: uma falha jamais vira dado.
    /// </summary>
    internal static DevicePostureDimensionState ClassifyDimension(EntraGraphException ex)
    {
        if (MentionsLicense(ex.GraphErrorCode)) return DevicePostureDimensionState.NotLicensed;
        if (ex.HttpStatusCode == 404) return DevicePostureDimensionState.NotLicensed;
        if (ex.Kind == EntraGraphErrorKind.InsufficientPermission || ex.HttpStatusCode == 403)
            return DevicePostureDimensionState.NotAuthorized;
        return DevicePostureDimensionState.Unavailable;
    }

    private static bool MentionsLicense(string? graphErrorCode) =>
        !string.IsNullOrWhiteSpace(graphErrorCode)
        && graphErrorCode!.Contains("license", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Descrição SANITIZADA da falha: só o que é seguro mostrar ao operador (dimensão + a permissão que a
    /// destrava, ou o status HTTP). Nunca token, URL completa, corpo, PII ou mensagem bruta da fonte.
    /// </summary>
    private static string Describe(EntraGraphException ex, string what) => ClassifyDimension(ex) switch
    {
        DevicePostureDimensionState.NotAuthorized =>
            $"Sem permissão para ler {what}: conceda {(what == "dispositivos gerenciados" ? ManagedDevicesPermission : ConfigurationPermission)}.",
        DevicePostureDimensionState.NotLicensed =>
            $"O tenant não tem o Microsoft Intune disponível para ler {what} (licença/capacidade ausente).",
        _ when ex.Kind == EntraGraphErrorKind.Throttled =>
            $"Throttling do Microsoft Graph ao ler {what}.",
        _ when ex.Kind == EntraGraphErrorKind.AuthFailure =>
            $"Falha de autenticação ao ler {what}.",
        _ => ex.HttpStatusCode is { } status
            ? $"Microsoft Graph indisponível ao ler {what} (HTTP {status.ToString(CultureInfo.InvariantCulture)})."
            : $"Microsoft Graph indisponível ao ler {what}.",
    };

    private static string DescribeKind(DevicePolicyKind kind) =>
        kind == DevicePolicyKind.CompliancePolicy ? "políticas de conformidade" : "configurações de dispositivo";

    private static string? Join(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) ? b : string.IsNullOrWhiteSpace(b) ? a : $"{a} {b}";

    // ---- Credenciais e leitura de JSON ----------------------------------------------------------------

    private GraphCredentials? DecryptCredentials(ConnectorConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.EncryptedSettings)) return null;
        try
        {
            var json = _protector.Unprotect(config.EncryptedSettings);
            var s = JsonSerializer.Deserialize<IntuneSettings>(json, JsonOpts);
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
            _log?.LogWarning(ex, "Configuração do conector Microsoft Intune ilegível; tratada como não configurada.");
            return null;
        }
    }

    /// <summary>Forma do JSON de configuração (a credencial COMUM da conexão Microsoft). Sem base URL — o destino é constante oficial.</summary>
    private sealed record IntuneSettings(
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

    private static string? StrOf(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Instante ISO-8601 válido, ou <c>null</c>. Um valor inválido NUNCA vira "agora" nem zero.</summary>
    private static DateTimeOffset? DateOf(JsonElement e, string prop)
    {
        var raw = StrOf(e, prop);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static string? Trim(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
