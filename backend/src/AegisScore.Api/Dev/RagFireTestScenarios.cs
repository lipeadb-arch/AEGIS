#if DEBUG
using System.Text.Json;

namespace AegisScore.Api.Dev;

/// <summary>
/// ⚠️ DEBUG-ONLY — geradores de telemetria FORJADA para o "Teste de Fogo" do motor RAG.
///
/// Produzem o <c>rawTelemetryPayload</c> (string) que o <c>AegisAiEvaluatorService</c> injeta no User
/// Prompt — o MESMO formato que uma exportação de SIEM/EDR entrega: JSON cru, como a ferramenta emitiu.
/// Não existe tipo <c>TelemetryEvent</c> no domínio (o seam do avaliador é <c>string</c>, de propósito:
/// o motor precisa ver o log CRU, não um DTO já interpretado) — então o record abaixo é local a este
/// andaime, só para montar JSON realista sem concatenar texto na mão.
///
/// Os 4 cenários formam uma MATRIZ DE PROVA deliberada, não amostras soltas — ver <see cref="All"/>.
/// </summary>
public static class RagFireTestScenarios
{
    /// <summary>Evento normalizado de SIEM (forma achatada típica de um export WINEVTLOG).</summary>
    private sealed record TelemetryEvent(
        string Timestamp,
        string Source,
        int? EventId,
        string Host,
        string Severity,
        Dictionary<string, object> Fields);

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static string ToPayload(object o) => JsonSerializer.Serialize(o, Pretty);

    /// <summary>
    /// Matriz de prova do RAG. Cada cenário isola UMA afirmação verificável sobre o motor:
    ///
    ///   A — telemetria de USB contra um controle de CIFRAGEM  → prova o GUARD-RAIL anti-alucinação
    ///       ("ausência de evidência não é evidência de conformidade"): o payload não contém NADA que
    ///       a rubrica do PR.DS-01 saiba medir, então o veredito honesto é NonCompliant por falta de prova.
    ///   B — alerta EDR do Sentinel contra DE.CM-01 → prova a TROCA DE REGRA (outra linha jsonb carregada).
    ///   C — telemetria de cifragem que PASSA a rubrica → prova que o modelo CALCULA a fórmula e credita.
    ///   D — telemetria de cifragem que REPROVA → prova que ele aplica os LIMIARES (fail-closed).
    ///
    /// C e D são o coração do teste: os números foram escolhidos para que a fórmula do PR.DS-01 dê um
    /// resultado ÚNICO e conferível na mão (ver os comentários de cada um) — se o Gemini acertar o
    /// status, ele leu a `calculation_logic` do jsonb e a aplicou.
    /// </summary>
    public static IReadOnlyDictionary<string, FireTestScenario> All { get; } =
        new Dictionary<string, FireTestScenario>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new(
                Key: "A",
                Title: "Quebra de política — dispositivo USB em host restrito (WINEVTLOG)",
                DefaultControl: "PR.DS-01",
                Expectation: "NonCompliant por AUSÊNCIA DE EVIDÊNCIA — o payload não traz cobertura de "
                           + "cifragem, chaves nem algoritmo; creditar (ou reprovar por 'USB') seria alucinação.",
                BuildPayload: UsbPolicyBreach),

            ["B"] = new(
                Key: "B",
                Title: "Alerta EDR crítico — execução suspeita (Microsoft Sentinel)",
                DefaultControl: "DE.CM-01",
                Expectation: "Veredito sob a regra de MONITORAMENTO CONTÍNUO (outra linha jsonb) — prova "
                           + "que o RAG troca de regra por chave, não usa um prompt genérico.",
                BuildPayload: SentinelCriticalAlert),

            ["C"] = new(
                Key: "C",
                Title: "Postura de cifragem em repouso — ACIMA da rubrica",
                DefaultControl: "PR.DS-01",
                Expectation: "Compliant — endpoint 0.988 (≥0.98), algoritmo válido (=1), score ≈0.965 (≥0.85).",
                BuildPayload: EncryptionPosturePassing),

            ["D"] = new(
                Key: "D",
                Title: "Postura de cifragem em repouso — ABAIXO da rubrica",
                DefaultControl: "PR.DS-01",
                Expectation: "NonCompliant — endpoint 0.715 (<0.90) e score ≈0.48 (<0.50); 3DES/RC4 zera o algoritmo.",
                BuildPayload: EncryptionPostureFailing),
        };

    // ---- Cenário A: USB em host restrito -------------------------------------------------------
    // IDs 133/134 conforme especificado no enunciado do teste (normalização do coletor). O ponto do
    // cenário não é o número do evento e sim que NADA aqui é medível pela rubrica de cifragem.
    private static string UsbPolicyBreach() => ToPayload(new
    {
        export = "WINEVTLOG",
        collector = "Microsoft Sentinel · CommonSecurityLog",
        query_window = "2026-07-17T13:00:00Z/2026-07-17T13:30:00Z",
        events = new[]
        {
            new TelemetryEvent(
                "2026-07-17T13:04:12Z", "WINEVTLOG", 133, "FIN-WKS-0447", "Warning",
                new()
                {
                    ["Channel"] = "Microsoft-Windows-Kernel-PnP/Device Configuration",
                    ["Message"] = "Removable storage device connected",
                    ["DeviceClass"] = "USBSTOR",
                    ["DeviceInstanceId"] = @"USBSTOR\DISK&VEN_SANDISK&PROD_ULTRA&REV_1.00\4C530001120523104192&0",
                    ["VolumeLabel"] = "NO NAME",
                    ["CapacityBytes"] = 64_424_509_440L,
                    ["AccountName"] = "CORP\\r.almeida",
                    ["HostPolicyGroup"] = "RESTRICTED-FINANCE-WORKSTATIONS",
                }),
            new TelemetryEvent(
                "2026-07-17T13:19:58Z", "WINEVTLOG", 134, "FIN-WKS-0447", "Warning",
                new()
                {
                    ["Channel"] = "Microsoft-Windows-Kernel-PnP/Device Configuration",
                    ["Message"] = "Removable storage device disconnected",
                    ["DeviceClass"] = "USBSTOR",
                    ["DeviceInstanceId"] = @"USBSTOR\DISK&VEN_SANDISK&PROD_ULTRA&REV_1.00\4C530001120523104192&0",
                    ["SessionDurationSeconds"] = 946,
                    ["AccountName"] = "CORP\\r.almeida",
                    ["HostPolicyGroup"] = "RESTRICTED-FINANCE-WORKSTATIONS",
                }),
        },
    });

    // ---- Cenário B: alerta crítico do Sentinel -------------------------------------------------
    private static string SentinelCriticalAlert() => ToPayload(new
    {
        export = "Microsoft Sentinel · SecurityAlert",
        query_window = "2026-07-17T02:00:00Z/2026-07-17T03:00:00Z",
        alerts = new[]
        {
            new
            {
                AlertName = "Suspicious process execution from temporary directory",
                AlertSeverity = "High",
                ProviderName = "Microsoft Defender for Endpoint",
                CompromisedEntity = "SRV-DB-PROD-02",
                TimeGenerated = "2026-07-17T02:41:07Z",
                Status = "New",
                Tactics = new[] { "Execution", "Defense Evasion" },
                Techniques = new[] { "T1059.001", "T1036" },
                ProcessName = "powershell.exe",
                CommandLine = "powershell -nop -w hidden -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQA",
                ParentProcess = "w3wp.exe",
                InitiatedByAccount = "NT AUTHORITY\\NETWORK SERVICE",
                RemediationAction = "None — alert not triaged",
                MinutesSinceGenerated = 271,
            },
        },
        sensor_coverage = new
        {
            managed_edge_interfaces_inventoried = 42,
            interfaces_with_sensor = 27,
            egress_volume_with_tls_inspection_pct = 38.4,
            unapproved_active_network_services = 6,
            rogue_wireless_aps_detected = 2,
            detection_baseline_age_days = 214,
            rule_false_positive_rate_30d_pct = 61.2,
        },
    });

    // ---- Cenário C: cifragem ACIMA da rubrica --------------------------------------------------
    // Conferência manual da calculation_logic do PR.DS-01:
    //   cifragem_endpoint = 988/1000            = 0.988   (≥0.98 ✔)
    //   cifragem_repositorio = 38/40            = 0.950
    //   chaves = 112/120                        = 0.9333
    //   algoritmo (todos AES-256/FIPS validado) = 1       (✔)
    //   score = .30(.988)+.30(.950)+.25(.9333)+.15(1) ≈ 0.9647  (≥0.85 ✔)  → Compliant
    private static string EncryptionPosturePassing() => ToPayload(new
    {
        export = "Microsoft Defender for Endpoint · DeviceInfo + Key Vault inventory",
        query_window = "2026-07-17T00:00:00Z/2026-07-17T23:59:59Z",
        endpoint_encryption = new
        {
            managed_endpoints = 1000,
            endpoints_with_disk_encryption_and_key_escrow = 988,
            escrow_verified = true,
            escrow_recovery_tested_at = "2026-07-10T09:00:00Z",
        },
        critical_repositories = new
        {
            inventoried = 40,
            with_proven_at_rest_encryption = 38,
            worm_protected_where_encryption_not_applicable = 2,
        },
        key_management = new
        {
            keys_inventoried = 120,
            keys_under_formal_management_with_rotation = 112,
            custody = "tenant-managed (CMK) in Azure Key Vault HSM",
            max_key_age_days = 83,
        },
        cryptographic_algorithms = new
        {
            algorithms_in_use = new[] { "AES-256-XTS", "AES-256-GCM", "RSA-4096" },
            all_within_validated_standard = true,
            standard = "FIPS 140-3 validated",
            deprecated_algorithms_detected = Array.Empty<string>(),
        },
        removable_media = new { policy_enforced_encryption_pct = 100.0 },
    });

    // ---- Cenário D: cifragem ABAIXO da rubrica -------------------------------------------------
    // Conferência manual:
    //   cifragem_endpoint = 715/1000 = 0.715  (<0.90 → NonCompliant por si só)
    //   cifragem_repositorio = 22/40 = 0.550
    //   chaves = 48/120              = 0.400
    //   algoritmo (3DES/RC4 em uso)  = 0
    //   score = .30(.715)+.30(.550)+.25(.400)+.15(0) ≈ 0.4795  (<0.50 ✔) → NonCompliant
    private static string EncryptionPostureFailing() => ToPayload(new
    {
        export = "Microsoft Defender for Endpoint · DeviceInfo + Key Vault inventory",
        query_window = "2026-07-17T00:00:00Z/2026-07-17T23:59:59Z",
        endpoint_encryption = new
        {
            managed_endpoints = 1000,
            endpoints_with_disk_encryption_and_key_escrow = 715,
            escrow_verified = false,
            escrow_note = "112 endpoints report BitLocker ON but recovery key is NOT present in escrow",
        },
        critical_repositories = new
        {
            inventoried = 40,
            with_proven_at_rest_encryption = 22,
            worm_protected_where_encryption_not_applicable = 0,
        },
        key_management = new
        {
            keys_inventoried = 120,
            keys_under_formal_management_with_rotation = 48,
            custody = "mixed — 31 keys held in application config files",
            max_key_age_days = 1_284,
        },
        cryptographic_algorithms = new
        {
            algorithms_in_use = new[] { "AES-256-XTS", "3DES-CBC", "RC4" },
            all_within_validated_standard = false,
            standard = "FIPS 140-3 validated",
            deprecated_algorithms_detected = new[] { "3DES-CBC (legacy backup agent)", "RC4 (internal SMB share)" },
        },
        removable_media = new { policy_enforced_encryption_pct = 12.5 },
    });
}

/// <summary>Um cenário do teste de fogo: o payload forjado + o controle-alvo + a expectativa auditável.</summary>
/// <param name="Key">Identificador curto ("A".."D").</param>
/// <param name="Title">Descrição legível no relatório de console.</param>
/// <param name="DefaultControl">Código NIST cuja regra jsonb será carregada pelo RAG.</param>
/// <param name="Expectation">O que um veredito CORRETO deve dizer — o gabarito do SDET, não uma asserção automática.</param>
/// <param name="BuildPayload">Gera a telemetria crua (JSON) no momento do disparo.</param>
public sealed record FireTestScenario(
    string Key,
    string Title,
    string DefaultControl,
    string Expectation,
    Func<string> BuildPayload);
#endif
