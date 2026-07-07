namespace AegisScore.Application.Abstractions;

/// <summary>
/// O motor de IA está indisponível: não configurado (sem Ai:ApiKey) ou o provedor recusou/
/// não respondeu à chamada. É uma condição OPERACIONAL (dependência externa fora do ar), não
/// um defeito de código — deve ser mapeada para HTTP 503 (Service Unavailable), nunca 500.
/// </summary>
public sealed class AiUnavailableException : Exception
{
    public AiUnavailableException(string message) : base(message) { }
    public AiUnavailableException(string message, Exception inner) : base(message, inner) { }
}
