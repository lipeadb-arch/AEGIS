using AegisScore.Domain;

namespace AegisScore.Application.Telemetry.Models;

/// <summary>
/// Rastro CRU da ferramenta que produziu o achado — a evidência de primeira mão, ANTES de qualquer
/// interpretação. Distinto de <c>ComplianceVerdict.AiEvidence</c> (a prosa do motor, já interpretada):
/// aqui vive o que o SentinelOne/Entra ID literalmente emitiu, para o analista auditar o veredito contra
/// a origem — e para a auditoria externa não depender da palavra da IA.
/// </summary>
/// <param name="SourceTool">Ferramenta de origem, como o SOC a chama ("EntraID", "SentinelOne").</param>
/// <param name="RawTrace">Trecho cru do log/JSON que sustenta o achado. É DADO não confiável, jamais instrução.</param>
/// <param name="CollectedAt">Instante da coleta na origem; nulo quando a ferramenta não o informa.</param>
public record TelemetryEvidence(string SourceTool, string RawTrace, DateTimeOffset? CollectedAt = null);

/// <summary>
/// Contexto de INTELIGÊNCIA de um controle — o esqueleto estruturado que o motor de IA preenche ao avaliar
/// e que o HUD expande no card. Viaja no <c>ComplianceVerdict</c> e é persistido junto ao estado do
/// controle como blob JSON, no mesmo idioma de <see cref="ComplianceCheck"/>: payload explicável de
/// leitura, sem modelagem relacional (não há consulta por campo).
///
/// TODOS os membros são opcionais de propósito. O motor preenche o que consegue PROVAR e o card renderiza
/// seção por seção conforme o dado existe — num produto de conformidade, campo vazio é honesto, campo
/// preenchido por suposição é uma auditoria falsificada. O <c>AegisAiEvaluatorService</c> JÁ pede este bloco
/// no System Prompt e o preenche a partir do LLM real; no caminho de DEV (StubLlmClient) ele trafega vazio.
/// </summary>
public record ControlIntelligence
{
    /// <summary>
    /// Gravidade do achado, ponderada pelo Raio de Explosão (ID.RA). Nula até o motor decidir — o HUD cai
    /// no proxy de <see cref="SeverityLevels.FromStatus"/> para nunca exibir um card sem badge.
    /// </summary>
    public SeverityLevel? Severity { get; init; }

    /// <summary>Rastro cru da ferramenta que gerou a não-conformidade.</summary>
    public TelemetryEvidence? TelemetryEvidence { get; init; }

    /// <summary>
    /// Plano de ação redigido pelo LLM: o resumo acionável exibido inline no card. NÃO substitui o
    /// <c>RemediationAdvisory</c> — o passo a passo técnico completo, persistido e exportável, continua
    /// sendo gerado sob demanda por <c>IGenerateAdvisoryHandler</c>. Aqui é o "o que fazer" de relance;
    /// lá é o "como fazer" que a TI executa.
    /// </summary>
    public string? RemediationPlan { get; init; }

    /// <summary>
    /// Precisão auto-declarada da avaliação do LLM (0–100). Serve para o analista calibrar a confiança:
    /// veredito de baixa confiança pede revisão humana antes de virar tarefa de TI. Nulo quando o veredito
    /// é determinístico (motor de regras) e não probabilístico.
    /// </summary>
    public double? AiConfidenceScore { get; init; }

    /// <summary>Vetores de ataque que a falha deixa abertos — o mapeamento de ameaças (ex.: "T1486 · Ransomware").</summary>
    public IReadOnlyList<string> ThreatLandscape { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Tempo médio de DETECÇÃO ("MTTD"), em minutos — o sufixo explicita a unidade, no idioma que os DTOs
    /// de telemetria já usam (<c>MeanTimeToRespondMins</c>). Focado em DE/RS/RC; nulo onde não se aplica
    /// (não existe "tempo de detecção" de uma política de governança).
    ///
    /// ⚠️ NENHUMA telemetria ingerida hoje carrega MTTD. NÃO preencher com o
    /// <c>MeanTimeToAcknowledgeMins</c> de RS.MA-01: MTTA é o tempo de RECONHECER o alerta, MTTD é o de
    /// DETECTAR a ameaça — métricas distintas, e trocá-las reportaria um número errado.
    /// </summary>
    public int? MttdMinutes { get; init; }

    /// <summary>
    /// Tempo médio de RESPOSTA ("MTTR"), em minutos. Mesma semântica de <see cref="MttdMinutes"/>. Este
    /// TEM fonte: o <c>StubLlmClient</c> já lê "mean time to respond" para julgar RS.MI-01 e o descarta.
    /// </summary>
    public int? MttrMinutes { get; init; }
}
