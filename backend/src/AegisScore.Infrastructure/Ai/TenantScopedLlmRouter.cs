using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Roteador tenant-scoped do <see cref="ILLMClient"/> (transporte cru): por chamada, decide o motor REAL
/// (<see cref="AnthropicLlmClient"/>) × SIMULADO (<see cref="StubLlmClient"/>) pelo gate do Free Tier. Tenant
/// fora da allowlist → NUNCA chama o motor externo. É a ÚNICA ligação de <see cref="ILLMClient"/> na DI:
/// o enriquecimento de telemetria (<see cref="AegisAiEvaluatorService"/>) e a IA consultiva do KNIGHT passam
/// por aqui, e o próprio <see cref="AegisAssessmentService"/> transporta por este roteador — nenhum injeta o
/// cliente Anthropic diretamente, ignorando o gate.
/// </summary>
public sealed class TenantScopedLlmRouter : ILLMClient
{
    private readonly AnthropicLlmClient _real;
    private readonly StubLlmClient _stub;
    private readonly IAiFreeTierGate _gate;
    private readonly IAiTenantResolver _resolver;

    public TenantScopedLlmRouter(
        AnthropicLlmClient real, StubLlmClient stub, IAiFreeTierGate gate, IAiTenantResolver resolver)
    {
        _real = real;
        _stub = stub;
        _gate = gate;
        _resolver = resolver;
    }

    public async Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        if (!_gate.ProviderConfigured)
            return await _stub.ExecutePromptAsync(systemPrompt, userPrompt, ct);

        var slug = await _resolver.GetCurrentSlugAsync(ct);
        var client = _gate.IsExternalAllowedForSlug(slug) ? (ILLMClient)_real : _stub;
        return await client.ExecutePromptAsync(systemPrompt, userPrompt, ct);
    }
}
