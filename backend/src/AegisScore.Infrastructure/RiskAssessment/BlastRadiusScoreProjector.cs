using Microsoft.Extensions.Logging;
using AegisScore.Application.Services;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.RiskAssessment;

/// <summary>
/// Ponte ID.RA → ledger. Traduz a MAGNITUDE de um raio de explosão numa penalização de conformidade dos
/// controles de avaliação de risco — ID.RA-01 ("vulnerabilidades/ameaças identificadas e registradas") e
/// ID.RA-05 ("risco priorizado por impacto × probabilidade"): um raio alto/crítico que alcança muitos
/// ativos é prova de que a priorização de risco NÃO contém o impacto.
///
/// <para><b>Fonte <see cref="VerdictSource.Telemetry"/> de propósito:</b> o raio deriva do estado TÉCNICO
/// REAL do ambiente (grafo de ativos, dependências, exposições), não de intenção documental — e precisa
/// ser AUTORITATIVO (rebaixar), coisa que a fonte <c>Documentary</c> (só faz upgrade) não faria.</para>
///
/// <para><b>Assimetria consciente:</b> é um PENALIZADOR. A recuperação do controle vem de evidência
/// POSITIVA de ID.RA (avaliação de risco adequada), não de um raio menor — um raio pequeno num único ativo
/// não prova a família ID.RA conforme no tenant.</para>
/// </summary>
public sealed class BlastRadiusScoreProjector : IBlastRadiusScoreProjector
{
    /// <summary>Alcance mínimo (nº de ativos colaterais) para o raio ser "amplo" o bastante para penalizar.</summary>
    private const int WideBlastAssetThreshold = 5;

    /// <summary>Controles de avaliação de risco (ID.RA) penalizados por um raio grande e não contido.</summary>
    private static readonly string[] TargetControls = { "ID.RA-01", "ID.RA-05" };

    private readonly IControlStateWriter _ledger;
    private readonly ILogger<BlastRadiusScoreProjector> _log;

    public BlastRadiusScoreProjector(IControlStateWriter ledger, ILogger<BlastRadiusScoreProjector> log)
    {
        _ledger = ledger;
        _log = log;
    }

    public async Task ProjectAsync(BlastRadiusAssessment assessment, CancellationToken ct = default)
    {
        if (!ShouldPenalize(assessment))
        {
            _log.LogDebug(
                "ID.RA: raio do ativo {Root} ({Level}, {Count} ativos) abaixo do limiar — ledger intocado.",
                assessment.RootAssetId, assessment.RiskLevel, assessment.ImpactedAssetCount);
            return;
        }

        // Gradua a penalização: Crítico zera o controle; Alto concede crédito PARCIAL (risco relevante,
        // porém não catastrófico) — mesma semântica de MitigatedByThirdParty no scoring (50%).
        var status = assessment.RiskLevel == RiskLevel.Critico
            ? ControlStatus.NonCompliant
            : ControlStatus.MitigatedByThirdParty;

        var evidence = BuildEvidence(assessment, status);

        // O tenant vem do assessment JÁ CARIMBADO (fail-closed no SaveChanges); o ControlStateWriter
        // re-valida contra o tenant ambiente (defesa em profundidade).
        foreach (var code in TargetControls)
            await _ledger.ApplyVerdictAsync(assessment.TenantId, code, status, evidence, VerdictSource.Telemetry, ct: ct);

        _log.LogInformation(
            "ID.RA: raio {Level} sobre {Count} ativos penalizou {Controls} como {Status} no tenant {Tenant}.",
            assessment.RiskLevel, assessment.ImpactedAssetCount,
            string.Join("/", TargetControls), status, assessment.TenantId);
    }

    /// <summary>
    /// Penaliza SÓ raios ao mesmo tempo SEVEROS (Alto/Crítico) e AMPLOS (≥ <see cref="WideBlastAssetThreshold"/>
    /// ativos). Um raio pequeno não é prova de conformidade — por isso o caminho negativo não credita nada.
    /// </summary>
    private static bool ShouldPenalize(BlastRadiusAssessment a) =>
        (a.RiskLevel == RiskLevel.Alto || a.RiskLevel == RiskLevel.Critico)
        && a.ImpactedAssetCount >= WideBlastAssetThreshold;

    private static string BuildEvidence(BlastRadiusAssessment a, ControlStatus status) =>
        $"[Raio de Explosão] Comprometer o ativo-raiz projeta risco {a.RiskLevel} sobre {a.ImpactedAssetCount} " +
        $"ativo(s) colateral(is) (profundidade {a.MaxDepth}, score {a.BlastRadiusScore:0.0}/100). A topologia " +
        $"atual não contém o impacto — priorização/gestão de risco (ID.RA) avaliada como {status}.";
}
