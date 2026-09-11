using System;
using System.Collections.Generic;
using System.Linq;

namespace AegisScore.Application.Queries;

/// <summary>
/// [AEGIS-LANGUAGE-STATES-01] O que se pode AFIRMAR sobre a leitura de uma fonte, derivado UMA vez a partir dos
/// resumos que as autoridades de leitura já produzem — e reutilizado pela Visão geral e pelo contexto do Auditor,
/// para que a tela e a IA não descrevam o mesmo estado de coleta de dois jeitos.
///
/// As distinções que isto preserva (e que antes chegavam iguais, como "tudo nulo" ou como um número sem ressalva):
///   • integração não configurada  → <see cref="DashboardSignalState.NoSource"/>;
///   • configurada, ainda sem coleta → <see cref="DashboardSignalState.NeverCollected"/> (inclusive quando a
///     primeira tentativa falhou — nesse caso a nota diz isso);
///   • última leitura disponível, com a tentativa mais recente FALHA → número preservado + nota;
///   • parte das fontes configuradas ainda sem coleta → número com ressalva de ESCOPO (não é o ambiente inteiro).
///
/// Nada aqui recalcula contagem, score ou lifecycle: só qualifica o número que a autoridade entregou.
/// </summary>
public static class CollectionReadings
{
    /// <summary>Rótulo da superfície baseada nas recomendações do Microsoft Secure Score.</summary>
    public const string PostureRecommendationsLabel = "Recomendações de postura";

    private const string FailedStatus = "Failed";
    private const string DegradedStatus = "Degraded";

    /// <summary>
    /// Leitura das recomendações de postura (Microsoft Secure Score). O valor é a quantidade de recomendações
    /// PENDENTES na fonte — diferença de pontuação da fonte, não configuração insegura nem exposição confirmada.
    /// </summary>
    public static DashboardMetricDto PostureRecommendations(PostureExposureSummaryDto s)
    {
        var failed = string.Equals(s.LastAttemptStatus, FailedStatus, StringComparison.OrdinalIgnoreCase);

        // Há recomendações registradas, mas a data da coleta não é conhecida (ex.: conector recriado): o dado
        // existe e continua sendo mostrado — sem inventar data nem afirmar atualidade.
        if (s.LastCollectedAt is null && s.TotalOpen + s.TotalResolved > 0)
            return new DashboardMetricDto(
                DashboardSignalState.Available, s.TotalOpen, s.SourceLabel, null,
                "Há recomendações registradas, mas a data da última coleta não é conhecida.");

        if (s.LastCollectedAt is null)
        {
            if (!s.SourceConfigured)
                return new DashboardMetricDto(
                    DashboardSignalState.NoSource, null, s.SourceLabel, null,
                    "Nenhuma fonte de recomendações de postura conectada (Microsoft Secure Score).");

            return new DashboardMetricDto(
                DashboardSignalState.NeverCollected, null, s.SourceLabel, null,
                failed
                    ? "Fonte configurada, mas a tentativa mais recente de coleta falhou antes de qualquer leitura."
                    : "Fonte configurada; nenhuma coleta concluída ainda.");
        }

        string? note = null;
        if (failed)
            note = "A tentativa mais recente de coleta falhou; o número é a última leitura disponível.";
        else if (string.Equals(s.LastAttemptStatus, DegradedStatus, StringComparison.OrdinalIgnoreCase))
            // Semântica do executor: Degraded aqui NÃO significa recomendações parciais (a completude delas é
            // independente) — só que a coleta registrou restrições. O número é o que ela entregou.
            note = "A coleta mais recente terminou com restrições; confira o detalhe em Integrações.";

        return new DashboardMetricDto(
            DashboardSignalState.Available, s.TotalOpen, s.SourceLabel, s.LastCollectedAt, note);
    }

    /// <summary>Rótulo das fontes de vulnerabilidade (provedores distintos), ou o nome genérico sem fonte.</summary>
    public static string VulnerabilitySourceLabel(VulnerabilitySummaryDto s) =>
        s.Sources.Count > 0
            ? string.Join(" · ", s.Sources.Select(x => x.Provider).Distinct())
            : "Gestão de vulnerabilidades";

    /// <summary>
    /// Leitura de vulnerabilidades com o <paramref name="value"/> escolhido pelo chamador (problemas distintos
    /// ou ativos afetados). <c>NeverCollected</c> do resumo é respeitado sem reinterpretação; a única distinção
    /// acrescentada é "sem fonte" × "fonte sem coleta" (com a falha antes da primeira leitura dita, quando é a
    /// causa conhecida), e as ressalvas de falha, de coleta com restrições e de escopo parcial.
    /// </summary>
    public static DashboardMetricDto Vulnerabilities(VulnerabilitySummaryDto s, int value, bool withNote = true)
    {
        var source = VulnerabilitySourceLabel(s);
        var failed = s.Sources.Count(x => string.Equals(x.Status, FailedStatus, StringComparison.OrdinalIgnoreCase));

        if (s.NeverCollected)
        {
            if (s.Sources.Count == 0)
                return new DashboardMetricDto(
                    DashboardSignalState.NoSource, null, source, null,
                    withNote ? "Nenhuma fonte de vulnerabilidades conectada neste ambiente." : null);

            return new DashboardMetricDto(
                DashboardSignalState.NeverCollected, null, source, null,
                !withNote ? null
                : failed > 0
                    ? "Fonte de vulnerabilidades configurada, mas a tentativa mais recente de coleta falhou antes de qualquer leitura."
                    : "Fonte de vulnerabilidades configurada, porém nenhuma coleta concluída ainda.");
        }

        var notes = new List<string>();
        var pending = s.Sources.Count(x => x.LastSyncAt is null);
        // Degraded no executor pode vir de QUALQUER dimensão do conector (vulnerabilidades incompletas, registros
        // inválidos ou, no Defender, só o inventário de software): a nota diz "restrições", sem afirmar população
        // parcial das vulnerabilidades.
        var degraded = s.Sources.Count(x => x.LastSyncAt is not null
            && string.Equals(x.Status, DegradedStatus, StringComparison.OrdinalIgnoreCase));
        if (pending > 0)
            notes.Add($"{pending} fonte(s) configurada(s) ainda sem coleta — os números cobrem só as fontes já coletadas.");
        if (failed > 0)
            notes.Add($"A tentativa mais recente de {failed} fonte(s) falhou; os números são a última leitura disponível.");
        if (degraded > 0)
            notes.Add($"A coleta mais recente de {degraded} fonte(s) terminou com restrições; confira o detalhe em Integrações.");

        return new DashboardMetricDto(
            DashboardSignalState.Available, value, source, s.LastCollectedAt,
            withNote && notes.Count > 0 ? string.Join(" ", notes) : null);
    }
}
