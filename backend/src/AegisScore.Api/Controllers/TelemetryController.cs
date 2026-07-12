using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Services;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Telemetry — superfície de ingestão PASSIVA de sinais de segurança (webhook para EDR/SIEM: Microsoft
/// Defender, Sentinel, CrowdStrike…). É o chamador do motor de avaliação por IA
/// (<see cref="ITelemetryIngestionService"/> → <c>EvaluateAsync</c> → <c>ControlStateWriter</c>): cada
/// alerta ingerido vira um veredito NIST com fonte <c>Telemetry</c> — a evidência AUTORITATIVA, a única
/// que pode levar um controle a 100% (a análise documental tem teto de 50%).
///
/// Superfícies: <c>/ingest</c> (genérico), <c>/asset</c> (Identify / ID.AM) e as rotas por categoria dos
/// pilares <c>/protect/*</c>, <c>/detect/*</c>, <c>/respond/*</c>, <c>/recover/*</c> — todas achatam seu
/// contrato específico num <see cref="CategoryTelemetrySignal"/> comum (DRY). Tenant IMPLÍCITO: resolvido
/// do claim <c>tenant_id</c> do JWT e aplicado pelo Global Query Filter — grava no tenant do chamador.
/// </summary>
[ApiController]
[Authorize]   // superfície de escrita no ledger: exige usuário autenticado (o FallbackPolicy já cobre; explícito declara a intenção).
[Route("api/v1/telemetry")]
public class TelemetryController : ControllerBase
{
    private readonly ITelemetryIngestionService _ingestion;

    public TelemetryController(ITelemetryIngestionService ingestion) => _ingestion = ingestion;

    /// <summary>
    /// Ingere um alerta de segurança GENÉRICO e avalia o controle NIST indicado. O motor decide o status
    /// e o <c>ControlStateWriter</c> faz o upsert do ledger com fonte <c>Telemetry</c>.
    /// </summary>
    /// <response code="200">Veredito aplicado ao ledger.</response>
    /// <response code="400">Payload incompleto ou <c>SubcategoryCode</c> fora do catálogo NIST.</response>
    /// <response code="503">Motor de IA indisponível (transitório — repetir).</response>
    [HttpPost("ingest")]
    public async Task<ActionResult<TelemetryVerdictDto>> Ingest(TelemetryIngestionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.SubcategoryCode))
            return BadRequest("SubcategoryCode é obrigatório: indica qual controle NIST a evidência endereça.");
        if (string.IsNullOrWhiteSpace(req.RawData))
            return BadRequest("RawData é obrigatório: é a evidência técnica que o motor avalia.");

        var signal = new TelemetrySignal(
            req.Source ?? "", req.EventName ?? "", req.Severity ?? "", req.SubcategoryCode, req.RawData);

        return await RunAsync(req.SubcategoryCode, () => _ingestion.IngestAsync(signal, ct));
    }

    /// <summary>
    /// Ingere a telemetria de um ATIVO (Identify / ID.AM) e avalia o controle de gestão de ativos pelos
    /// metadados táticos: cobertura de EDR, ciclo de vida do SO, nº de CVEs críticas e criticidade do ativo.
    /// </summary>
    [HttpPost("asset")]
    public async Task<ActionResult<TelemetryVerdictDto>> IngestAsset(AssetTelemetryRequest req, CancellationToken ct)
    {
        // Default para o controle de inventário de ativos quando o emissor não especifica o alvo NIST.
        var code = string.IsNullOrWhiteSpace(req.SubcategoryCode) ? "ID.AM-01" : req.SubcategoryCode;

        var signal = new AssetTelemetrySignal(
            req.AssetName, req.EdrCoverage, req.OsLifecycle,
            req.CriticalVulnerabilitiesCount, req.IsCriticalAsset, code);

        return await RunAsync(code, () => _ingestion.IngestAssetAsync(signal, ct));
    }

    // ---- Protect (PR): rotas especializadas por categoria do SOC multicloud --------

    /// <summary>PR.AA — Identity &amp; Access. Privilégio sem MFA integral ou sem Conditional Access reprova.</summary>
    [HttpPost("protect/identity")]
    public Task<ActionResult<TelemetryVerdictDto>> IngestIdentityProtect(IdentityProtectTelemetryDto req, CancellationToken ct)
        => IngestCategory(req.SubcategoryCode, "Protect", "Identity", new[]
        {
            $"Privileged MFA Coverage: {req.PrivilegedMfaCoverage}%",
            $"Standard MFA Coverage: {req.StandardMfaCoverage}%",
            $"Stale Accounts Active: {req.StaleAccountsActive}",
            $"Conditional Access Enforced: {Flag(req.ConditionalAccessEnforced)}",
        }, ct);

    /// <summary>PR.DS — Data Security. Criptografia de endpoint insuficiente ou tráfego em claro reprova.</summary>
    [HttpPost("protect/data")]
    public Task<ActionResult<TelemetryVerdictDto>> IngestDataProtect(DataProtectTelemetryDto req, CancellationToken ct)
        => IngestCategory(req.SubcategoryCode, "Protect", "Data", new[]
        {
            $"Endpoint Encryption Coverage: {req.EndpointEncryptionCoverage}%",
            $"DLP Active Policies: {req.DlpActivePoliciesCount}",
            $"Unencrypted Traffic Detected: {Flag(req.UnencryptedTrafficDetected)}",
        }, ct);

    /// <summary>PR.PS — Platform Security. Hardening CIS abaixo do mínimo ou patch crítico pendente reprova.</summary>
    [HttpPost("protect/platform")]
    public Task<ActionResult<TelemetryVerdictDto>> IngestPlatformProtect(PlatformProtectTelemetryDto req, CancellationToken ct)
        => IngestCategory(req.SubcategoryCode, "Protect", "Platform", new[]
        {
            $"CIS Benchmark Compliance Rate: {req.CisBenchmarkComplianceRate}%",
            $"AppLocker Enforced: {Flag(req.AppLockerEnforced)}",
            $"Missing Critical Patches: {req.MissingCriticalPatchesCount}",
        }, ct);

    /// <summary>PR.IR — Technology Infrastructure Resilience. Firewall sem política default-deny reprova.</summary>
    [HttpPost("protect/network")]
    public Task<ActionResult<TelemetryVerdictDto>> IngestNetworkProtect(NetworkProtectTelemetryDto req, CancellationToken ct)
        => IngestCategory(req.SubcategoryCode, "Protect", "Network", new[]
        {
            $"Microsegmentation Active: {Flag(req.MicrosegmentationActive)}",
            $"Default Deny Firewall Enforced: {Flag(req.DefaultDenyFirewallEnforced)}",
        }, ct);

    // ---- Detect (DE): rotas especializadas por categoria do SOC avançado -----------

    /// <summary>DE.AE — Adverse Event Analysis. Anomalia grave não investigada ou fadiga de alerta reprova.</summary>
    [HttpPost("detect/anomalies")]
    public Task<ActionResult<TelemetryVerdictDto>> IngestAnomaliesDetect(AnomaliesDetectTelemetryDto req, CancellationToken ct)
        => IngestCategory(req.SubcategoryCode, "Detect", "Anomalies", new[]
        {
            $"Uninvestigated High Anomalies: {req.UninvestigatedHighAnomaliesCount}",
            $"False Positive Rate: {req.FalsePositiveRate}%",
            $"Correlation Rules Fired: {req.CorrelationRulesFiredCount}",
        }, ct);

    /// <summary>DE.CM — Continuous Monitoring. Cobertura de logs críticos baixa ou ativo crítico sem monitoração reprova.</summary>
    [HttpPost("detect/monitoring")]
    public Task<ActionResult<TelemetryVerdictDto>> IngestMonitoringDetect(MonitoringDetectTelemetryDto req, CancellationToken ct)
        => IngestCategory(req.SubcategoryCode, "Detect", "Monitoring", new[]
        {
            $"Critical Log Source Coverage: {req.CriticalLogSourceCoverage}%",
            $"Unmonitored Critical Assets: {req.UnmonitoredCriticalAssetsCount}",
            $"Network Visibility Coverage: {req.NetworkVisibilityCoverage}%",
        }, ct);

    /// <summary>Detection Engineering (DE.AE, o antigo DE.DP). Baixa cobertura MITRE ou ataques simulados não detectados reprova.</summary>
    [HttpPost("detect/process")]
    public Task<ActionResult<TelemetryVerdictDto>> IngestProcessDetect(ProcessDetectTelemetryDto req, CancellationToken ct)
        => IngestCategory(req.SubcategoryCode, "Detect", "Detection Engineering", new[]
        {
            $"Mitre Attck Coverage Rate: {req.MitreAttckCoverageRate}%",
            $"Active Detection Rules: {req.ActiveDetectionRulesCount}",
            $"Simulated Attacks Detected Rate: {req.SimulatedAttacksDetectedRate}%",
        }, ct);

    // ---- Respond (RS): resposta a incidentes ---------------------------------------

    /// <summary>RS.MA — Incident Analysis. Reconhecimento lento (MTTA) ou baixa cobertura de threat hunting reprova.</summary>
    [HttpPost("respond/analysis")]
    public Task<ActionResult<TelemetryVerdictDto>> IngestAnalysisRespond(AnalysisRespondTelemetryDto req, CancellationToken ct)
        => IngestCategory(req.SubcategoryCode, "Respond", "Analysis", new[]
        {
            $"Mean Time To Acknowledge: {req.MeanTimeToAcknowledgeMins} min",
            $"Threat Hunting Coverage Rate: {req.ThreatHuntingCoverageRate}%",
        }, ct);

    /// <summary>RS.MI — Incident Mitigation. Sem isolamento automatizado ou resposta lenta (MTTR) reprova.</summary>
    [HttpPost("respond/mitigation")]
    public Task<ActionResult<TelemetryVerdictDto>> IngestMitigationRespond(MitigationRespondTelemetryDto req, CancellationToken ct)
        => IngestCategory(req.SubcategoryCode, "Respond", "Mitigation", new[]
        {
            $"Automated Isolation Enabled: {Flag(req.AutomatedIsolationEnabled)}",
            $"Mean Time To Respond: {req.MeanTimeToRespondMins} min",
        }, ct);

    // ---- Recover (RC): recuperação e resiliência -----------------------------------

    /// <summary>RC.RP — Recovery Plan Execution. Backup mutável, com integridade não-Valid ou RTO não atendido reprova.</summary>
    [HttpPost("recover/execution")]
    public Task<ActionResult<TelemetryVerdictDto>> IngestExecutionRecover(ExecutionRecoverTelemetryDto req, CancellationToken ct)
        => IngestCategory(req.SubcategoryCode, "Recover", "Execution", new[]
        {
            $"Immutable Backups Enabled: {Flag(req.ImmutableBackupsEnabled)}",
            $"Backup Integrity Status: {req.BackupIntegrityStatus}",
            $"Recovery Time Objective Met: {Flag(req.RecoveryTimeObjectiveMet)}",
        }, ct);

    // ---- Govern (GV): telemetria estruturada de governança (além da análise documental) --------

    /// <summary>
    /// GV.SC — Cybersecurity Supply Chain Risk Management. Governança tem métricas, não só PDFs: um
    /// fornecedor de TI com acesso à rede SEM auditoria de terceiros ativa é um elo não verificado e reprova.
    /// </summary>
    [HttpPost("govern/supply-chain")]
    public Task<ActionResult<TelemetryVerdictDto>> IngestSupplyChainGovern(SupplyChainTelemetryDto req, CancellationToken ct)
        => IngestCategory(req.SubcategoryCode, "Govern", "Supply Chain", new[]
        {
            $"Suppliers With Network Access: {req.SuppliersWithNetworkAccess}",
            $"Critical Suppliers: {req.CriticalSuppliersCount}",
            $"Third Party Audited: {Flag(req.ThirdPartyAudited)}",
        }, ct);

    /// <summary>
    /// GV.RR — Roles, Responsibilities &amp; Authorities. Conta de administrador SEM revisão periódica de
    /// acesso configurada é autoridade sem accountability e reprova.
    /// </summary>
    [HttpPost("govern/roles")]
    public Task<ActionResult<TelemetryVerdictDto>> IngestRolesGovern(RolesTelemetryDto req, CancellationToken ct)
        => IngestCategory(req.SubcategoryCode, "Govern", "Roles", new[]
        {
            $"Admin Accounts: {req.TotalAdminAccounts}",
            $"Admin Accounts Without Periodic Review: {req.AdminAccountsWithoutReview}",
            $"Privileged Access Review Configured: {Flag(req.PrivilegedAccessReviewConfigured)}",
        }, ct);

    // ---- helpers -------------------------------------------------------------------

    /// <summary>
    /// Seam ÚNICO por categoria (Protect/Detect/Respond/Recover): valida o alvo NIST, empacota as métricas
    /// no <see cref="CategoryTelemetrySignal"/> comum e delega ao motor. Elimina a duplicação por pilar.
    /// </summary>
    private async Task<ActionResult<TelemetryVerdictDto>> IngestCategory(
        string subcategoryCode, string pillar, string category, IReadOnlyList<string> metrics, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subcategoryCode))
            return BadRequest("SubcategoryCode é obrigatório: indica qual controle NIST a evidência endereça.");

        return await RunAsync(subcategoryCode,
            () => _ingestion.IngestCategoryAsync(new CategoryTelemetrySignal(subcategoryCode, pillar, category, metrics), ct));
    }

    /// <summary>
    /// Executa a ingestão, mapeia um código NIST inexistente no catálogo para 400 (erro do cliente, não do
    /// servidor) e projeta o veredito — já persistido no ledger — no DTO de resposta com o percentual.
    /// </summary>
    private async Task<ActionResult<TelemetryVerdictDto>> RunAsync(string code, Func<Task<ComplianceVerdict>> ingest)
    {
        ComplianceVerdict verdict;
        try
        {
            verdict = await ingest();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        var pct = verdict.MaxScorePoints == 0
            ? 0
            : (int)Math.Round(100.0 * verdict.AwardedScore / verdict.MaxScorePoints);

        return Ok(new TelemetryVerdictDto(
            code, verdict.Status.ToString(), verdict.AwardedScore, verdict.MaxScorePoints, pct, verdict.AiEvidence));
    }

    private static string Flag(bool b) => b ? "true" : "false";
}
