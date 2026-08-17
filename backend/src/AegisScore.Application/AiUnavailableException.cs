namespace AegisScore.Application.Abstractions;

/// <summary>
/// O motor de IA está indisponível: não configurado (sem Ai:ApiKey) ou o provedor recusou/
/// não respondeu à chamada. É uma condição OPERACIONAL (dependência externa fora do ar), não
/// um defeito de código — deve ser mapeada para HTTP 503 (Service Unavailable), nunca 500.
/// </summary>
public class AiUnavailableException : Exception
{
    public AiUnavailableException(string message) : base(message) { }
    public AiUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Caso específico de <see cref="AiUnavailableException"/>: a COTA gratuita do provedor foi esgotada
/// (HTTP 429 / RESOURCE_EXHAUSTED). Continua sendo uma condição operacional transitória (503), mas o
/// middleware a distingue para exibir uma mensagem compreensível ("cota gratuita esgotada"). Nenhum
/// mecanismo de cobrança automática é acionado — o Free Tier não tem billing.
/// </summary>
public sealed class AiQuotaExhaustedException : AiUnavailableException
{
    public AiQuotaExhaustedException(string message) : base(message) { }
    public AiQuotaExhaustedException(string message, Exception inner) : base(message, inner) { }
}
