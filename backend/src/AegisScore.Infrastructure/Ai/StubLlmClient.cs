using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Assessment;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// [AEGIS-AUD-019] <see cref="ILLMClient"/> determinístico e sem rede, para DEV/demo e testes. NÃO contém
/// regra de conformidade PRÓPRIA: DELEGA ao <see cref="DeterministicControlEvaluator"/> — a autoridade única
/// e reutilizável — e apenas serializa o veredito no contrato JSON do transporte. Assim o Stub deixa de ser
/// o lugar onde mora a regra oficial (que agora vive na autoridade). Não emite bloco <c>intelligence</c>: o
/// enriquecimento (severidade, ameaças, remediação) é papel do motor real de IA, e o
/// <see cref="AegisAiEvaluatorService"/> ignora qualquer status devolvido aqui de qualquer forma.
/// Para produção, registre um ILLMClient de verdade (Anthropic/OpenAI) — o <see cref="GeminiLlmClient"/> serve de molde.
/// </summary>
public sealed class StubLlmClient : ILLMClient
{
    public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        // A regra de conformidade vem da autoridade, não daqui. O código-alvo é lido do cabeçalho do prompt
        // (âncora para regras multi-controle); o restante do prompt é a telemetria varrida pela autoridade.
        var verdict = DeterministicControlEvaluator.Evaluate(
            ExtractSubcategoryCode(userPrompt), userPrompt, evidenceRequirements: null);

        var json = JsonSerializer.Serialize(new
        {
            status = verdict.Status.ToString(),
            aiEvidence = verdict.Evidence,
            checks = verdict.Checks,
        });
        return Task.FromResult(json);
    }

    /// <summary>Lê o código-alvo do cabeçalho "SUBCATEGORY: PR.AA-01" do prompt (vazio se ausente).</summary>
    private static string ExtractSubcategoryCode(string userPrompt)
    {
        const string marker = "SUBCATEGORY:";
        var i = userPrompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        var rest = userPrompt[(i + marker.Length)..].TrimStart();
        return rest.Split('\n', ' ', '\r', '\t')[0].Trim();
    }
}
