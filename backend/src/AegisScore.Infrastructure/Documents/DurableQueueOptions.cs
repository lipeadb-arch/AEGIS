namespace AegisScore.Infrastructure.Documents;

/// <summary>
/// [AEGIS-AUD-050] Parâmetros da fila operacional durável de análise de documentos. Ligados da seção
/// <c>DocumentAnalysisQueue</c> da configuração; os defaults servem à demo e a ambientes pequenos sem
/// exigir configuração. Tempos em segundos para granularidade de teste.
/// </summary>
public sealed class DocumentAnalysisQueueOptions
{
    public const string SectionName = "DocumentAnalysisQueue";

    /// <summary>Duração do lease. Um <c>Processing</c> com lease vencido volta a ser adquirível (worker caído).
    /// Renovado por batimento durante o trabalho (heartbeat), então NÃO precisa cobrir a operação inteira —
    /// só folgar sobre um intervalo de batimento.</summary>
    public int LeaseSeconds { get; set; } = 300;

    /// <summary>Intervalo de sondagem do worker quando a fila está vazia. Ao encontrar trabalho, drena sem esperar.</summary>
    public int PollSeconds { get; set; } = 5;

    /// <summary>Limite de aquisições antes de a falha virar terminal (<c>Failed</c>).</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Backoff aplicado ao devolver um documento para nova tentativa após falha transitória.</summary>
    public int RetryBackoffSeconds { get; set; } = 30;

    /// <summary>Intervalo do batimento de lease (renovação). Nulo/zero = derivar de <see cref="LeaseSeconds"/>/3.</summary>
    public int HeartbeatSeconds { get; set; }

    /// <summary>Intervalo efetivo do batimento: o configurado, ou 1/3 do lease (mínimo 1s), sempre menor que o lease.</summary>
    public int EffectiveHeartbeatSeconds => HeartbeatSeconds > 0 ? HeartbeatSeconds : Math.Max(1, LeaseSeconds / 3);

    /// <summary>Valida os limites; devolve <c>false</c> e uma mensagem clara quando algum valor é inválido.</summary>
    public bool TryValidate(out string? error) => DurableQueueValidation.Validate(
        SectionName, LeaseSeconds, PollSeconds, MaxAttempts, RetryBackoffSeconds, EffectiveHeartbeatSeconds, out error);
}

/// <summary>
/// [AEGIS-AUD-050] Parâmetros da fila operacional durável de sincronização de políticas. Seção
/// <c>PolicySyncQueue</c>. Inclui o intervalo do ciclo PERIÓDICO — o timer que apenas ENFILEIRA trabalho
/// (persiste pedidos), nunca o transporta.
/// </summary>
public sealed class PolicySyncQueueOptions
{
    public const string SectionName = "PolicySyncQueue";

    public int LeaseSeconds { get; set; } = 300;
    public int PollSeconds { get; set; } = 5;
    public int MaxAttempts { get; set; } = 5;
    public int RetryBackoffSeconds { get; set; } = 60;
    public int HeartbeatSeconds { get; set; }

    /// <summary>Período do ciclo que varre os tenants com integração de documentos e enfileira um sync para cada.</summary>
    public int PeriodicIntervalMinutes { get; set; } = 60;

    public int EffectiveHeartbeatSeconds => HeartbeatSeconds > 0 ? HeartbeatSeconds : Math.Max(1, LeaseSeconds / 3);

    public bool TryValidate(out string? error)
    {
        if (!DurableQueueValidation.Validate(
                SectionName, LeaseSeconds, PollSeconds, MaxAttempts, RetryBackoffSeconds, EffectiveHeartbeatSeconds, out error))
            return false;
        if (PeriodicIntervalMinutes <= 0)
        {
            error = $"{SectionName}: PeriodicIntervalMinutes deve ser > 0 (recebido {PeriodicIntervalMinutes}).";
            return false;
        }
        return true;
    }
}

/// <summary>Regras comuns de validação das opções de fila durável — falha CLARA em valores inválidos/negativos.</summary>
internal static class DurableQueueValidation
{
    public static bool Validate(
        string section, int leaseSeconds, int pollSeconds, int maxAttempts,
        int retryBackoffSeconds, int heartbeatSeconds, out string? error)
    {
        if (leaseSeconds <= 0)
            error = $"{section}: LeaseSeconds deve ser > 0 (recebido {leaseSeconds}).";
        else if (pollSeconds <= 0)
            error = $"{section}: PollSeconds deve ser > 0 (recebido {pollSeconds}).";
        else if (maxAttempts <= 0)
            error = $"{section}: MaxAttempts deve ser > 0 (recebido {maxAttempts}).";
        else if (retryBackoffSeconds < 0)
            error = $"{section}: RetryBackoffSeconds não pode ser negativo (recebido {retryBackoffSeconds}).";
        else if (heartbeatSeconds <= 0 || heartbeatSeconds >= leaseSeconds)
            error = $"{section}: o batimento ({heartbeatSeconds}s) deve ser > 0 e MENOR que o lease " +
                    $"({leaseSeconds}s), senão o lease expira antes de renovar.";
        else
            error = null;

        return error is null;
    }
}
