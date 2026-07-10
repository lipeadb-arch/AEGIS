using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// <see cref="ILLMClient"/> determinístico e sem rede, para DEV/demo e testes. Devolve um veredito
/// JSON bem-formado (o mesmo contrato do System Prompt do avaliador) a partir de uma varredura ingênua
/// de palavras-chave no payload — o suficiente para exercitar o pipeline evaluate→persist e os três
/// status, NÃO uma avaliação real. Para produção, registre um ILLMClient de verdade (Anthropic/OpenAI)
/// — o transporte HTTP do <see cref="ClaudeAssessmentService"/> serve de molde.
/// </summary>
public sealed class StubLlmClient : ILLMClient
{
    public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var p = userPrompt.ToLowerInvariant();

        var (status, evidence) =
            p.Contains("mssp") || p.Contains("managed service") || p.Contains("third party") || p.Contains("thirdparty")
                ? ("MitigatedByThirdParty", "Stub: log indica cobertura por serviço gerenciado/terceiro (SOC/MSSP).")
            : p.Contains("blocked") || p.Contains("prevented") || p.Contains("\"mfa\":true") || p.Contains("success")
                ? ("Compliant", "Stub: telemetria mostra ação de bloqueio/MFA bem-sucedida para o controle alvo.")
                : ("NonCompliant", "Stub: sem evidência conclusiva de controle efetivo no payload analisado.");

        return Task.FromResult($"{{\"status\":\"{status}\",\"aiEvidence\":\"{evidence}\"}}");
    }
}
