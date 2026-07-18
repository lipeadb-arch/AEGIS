using System.Text;

namespace AegisScore.Application.Services;

/// <summary>
/// Tradução de uma família de controle NIST para o termo que o negócio entende. O código do framework
/// (ex.: "RS.MA") é indispensável ao motor e hostil na tela — este par é o que permite ao Auditor citar
/// "Resposta a Incidentes (MTTA/MTTR)" e só então a sigla.
///
/// ⚠️ Distinto do glossário do frontend (<c>models/nist-glossary.ts</c>), que traduz o código para o NOME
/// PT-BR da categoria ("PR.AA" → "Identidade e Acesso") para rotular o card. Aqui o alvo é o IMPACTO
/// operacional ("superfície de sequestro de conta privilegiada"), consumido pelo LLM ao redigir o plano.
/// </summary>
public sealed record AuditorTranslationRule(string Code, string BusinessTerm);

/// <summary>
/// Personalidade do Auditor Virtual, carregada de <c>AuditorPersonality.json</c> e injetada no System
/// Prompt do motor de avaliação. Governa TOM e REDAÇÃO da prosa em português do veredito
/// (<c>aiEvidence</c>, <c>remediationPlan</c>) — jamais o status, a confiança ou o que conta como
/// evidência. Numa plataforma de conformidade, uma persona que suaviza veredito é auditoria falsificada;
/// por isso a separação é reafirmada dentro do próprio prompt.
/// </summary>
public sealed record AuditorPersona(
    string Persona,
    IReadOnlyList<string> Tone,
    IReadOnlyList<AuditorTranslationRule> TranslationRules,
    IReadOnlyList<string> ActionDirectives)
{
    /// <summary>
    /// Persona VAZIA — o comportamento anterior à camada de personalidade (auditor técnico puro).
    /// É o fallback quando o arquivo de configuração não é encontrado e o valor usado nos testes:
    /// o veredito não pode depender do tom, então a ausência degrada a redação e nada mais.
    /// </summary>
    public static AuditorPersona Neutral { get; } = new(
        "", Array.Empty<string>(), Array.Empty<AuditorTranslationRule>(), Array.Empty<string>());

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Persona) && Tone.Count == 0
        && TranslationRules.Count == 0 && ActionDirectives.Count == 0;

    /// <summary>
    /// Renderiza a persona como bloco rotulado para o prompt — mesmo idioma do
    /// <c>AssessmentRuleContextBuilder</c>: caixa alta nos rótulos, um item por linha, prosa em vez de
    /// JSON cru (o modelo lê melhor). Devolve string vazia quando não há persona, e o prompt a omite.
    /// </summary>
    public string ToPromptBlock()
    {
        if (IsEmpty)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("AUDITOR PERSONA — governs TONE and WORDING only. It NEVER changes the status, the");
        sb.AppendLine("confidence score, or which evidence counts. A persona that softens a verdict is a");
        sb.AppendLine("falsified audit; stay as rigorous as the assessment rules above demand.");

        if (!string.IsNullOrWhiteSpace(Persona))
            sb.AppendLine($"  ROLE: {Persona}");

        if (Tone.Count > 0)
            sb.AppendLine($"  TONE: {string.Join(" · ", Tone)}");

        if (TranslationRules.Count > 0)
        {
            sb.AppendLine("  BUSINESS TRANSLATION GLOSSARY — when you cite a control family, lead with the");
            sb.AppendLine("  business term and put the NIST code in parentheses, never the code alone:");
            foreach (var rule in TranslationRules)
                sb.AppendLine($"    • {rule.Code} → {rule.BusinessTerm}");
        }

        if (ActionDirectives.Count > 0)
        {
            sb.AppendLine("  ACTION DIRECTIVES:");
            foreach (var directive in ActionDirectives)
                sb.AppendLine($"    • {directive}");
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Porta de leitura da personalidade do Auditor. Implementada na Infra por um provedor singleton que lê o
/// JSON UMA vez no startup (a persona é reference data de processo, não dado de tenant).
/// </summary>
public interface IAuditorPersonaProvider
{
    AuditorPersona Persona { get; }
}

/// <summary>
/// Provedor imutável de persona fixa — usado como fallback e nos testes, onde a personalidade é ruído.
/// </summary>
public sealed record StaticAuditorPersonaProvider(AuditorPersona Persona) : IAuditorPersonaProvider
{
    public static StaticAuditorPersonaProvider Neutral { get; } = new(AuditorPersona.Neutral);
}
