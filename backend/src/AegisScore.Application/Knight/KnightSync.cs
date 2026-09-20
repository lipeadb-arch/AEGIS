using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

// ============================================================================
//  [AEGIS-KNIGHT-MULTICLOUD-01] Sincronização do KNIGHT iniciada em Integrações
// ============================================================================
// A coleta real de um diretório pode levar minutos. Presa à requisição HTTP, ela dependia do tempo limite do
// navegador e do proxy — e, quando o corte acontecia, a tela ficava sem saber o que houve e sem nenhum
// identificador para perguntar. Aqui o pedido nasce DURÁVEL e IDENTIFICADO antes da resposta (202): a tela
// acompanha AQUELE pedido; o processamento acontece num escopo próprio, reusando a autoridade única de
// coleta → ADM → avaliação.

/// <summary>Visão de leitura de um pedido de sincronização — tudo sanitizado, nada de segredo.</summary>
/// <param name="LastCompletedRunId">
/// A última avaliação CONCLUÍDA da mesma fonte no momento da leitura — os dados que continuam valendo quando
/// este pedido falha. Nunca é apresentada como resultado deste pedido.
/// </param>
public sealed record KnightSyncRequestView(
    Guid Id,
    Guid ConnectorId,
    KnightSourceType Source,
    KnightSyncStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int Attempts,
    Guid? RunId,
    KnightSourceState? ResultSourceState,
    string? FailureCategory,
    string? Message,
    Guid? LastCompletedRunId,
    DateTimeOffset? LastCompletedAt);

/// <summary>Desfecho de um pedido: registrado agora, ou um pedido ATIVO que já existia para o conector.</summary>
public sealed record KnightSyncEnqueueResult(KnightSyncRequestView Request, bool AlreadyActive);

/// <summary>
/// Fontes KNIGHT que um conector de Integrações consegue sincronizar.
///
/// [AEGIS-KNIGHT-COVERAGE-02] UM conector passa a poder alimentar MAIS DE UMA fonte. O conector Microsoft é o
/// mesmo registro de aplicação para o Microsoft Entra ID e para o Microsoft Teams — muda o recurso para o qual
/// o token é emitido e o papel de diretório exigido. Cada fonte tem o próprio pedido, o próprio desfecho e a
/// própria avaliação: sincronizar o Teams NÃO apaga nem substitui a avaliação do Entra ID.
/// </summary>
public static class KnightConnectorSources
{
    /// <summary>Todas as fontes que este conector alimenta, na ordem de apresentação (a principal primeiro).</summary>
    public static IReadOnlyList<KnightSourceType> SourcesOf(ConnectorProvider provider, ConnectorCapability capability) =>
        capability != ConnectorCapability.IdentityPosture
            ? Array.Empty<KnightSourceType>()
            : provider switch
            {
                ConnectorProvider.Microsoft => new[] { KnightSourceType.MicrosoftEntraId, KnightSourceType.MicrosoftTeams },
                ConnectorProvider.Google => new[] { KnightSourceType.GoogleWorkspace },
                _ => Array.Empty<KnightSourceType>(),
            };

    /// <summary>Fonte PADRÃO do conector (a que um pedido sem fonte declarada sincroniza) — ou nenhuma.</summary>
    public static KnightSourceType? SourceOf(ConnectorProvider provider, ConnectorCapability capability) =>
        SourcesOf(provider, capability) is { Count: > 0 } list ? list[0] : null;

    /// <summary>
    /// Resolve a fonte pedida pelo nome. Um nome desconhecido, ou de uma fonte que este conector NÃO alimenta,
    /// devolve <c>null</c>: a chamada é recusada em vez de sincronizar silenciosamente outra coisa.
    /// </summary>
    public static KnightSourceType? Resolve(ConnectorProvider provider, ConnectorCapability capability, string? requested)
    {
        var sources = SourcesOf(provider, capability);
        if (sources.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(requested)) return sources[0];

        var wanted = requested.Trim();
        foreach (var s in sources)
        {
            if (s.ToString().Equals(wanted, StringComparison.OrdinalIgnoreCase)) return s;
            if (Slug(s).Equals(wanted, StringComparison.OrdinalIgnoreCase)) return s;
        }
        return null;
    }

    /// <summary>Apelido curto usado nas rotas e na tela ("entra", "teams", "google").</summary>
    public static string Slug(KnightSourceType source) => source switch
    {
        KnightSourceType.MicrosoftEntraId => "entra",
        KnightSourceType.MicrosoftTeams => "teams",
        KnightSourceType.GoogleWorkspace => "google",
        KnightSourceType.Demo => "demo",
        _ => source.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// Pedidos de sincronização do tenant do CONTEXTO (Global Query Filter fail-closed): registrar e ler. Um pedido
/// de outro tenant é indistinguível de inexistente.
/// </summary>
public interface IKnightSyncRequests
{
    /// <summary>
    /// Registra o pedido para o conector E a fonte — idempotente: se já existe um pedido ATIVO para esse par,
    /// devolve esse (<see cref="KnightSyncEnqueueResult.AlreadyActive"/>), sem criar outro nem coletar de novo.
    /// [AEGIS-KNIGHT-COVERAGE-02] A chave é o par (conector, fonte): uma sincronização do Microsoft Teams em
    /// andamento não impede uma do Microsoft Entra ID no mesmo conector, e vice-versa.
    /// </summary>
    Task<KnightSyncEnqueueResult> EnqueueAsync(Guid connectorId, KnightSourceType source, Guid? requestedBy, CancellationToken ct = default);

    Task<KnightSyncRequestView?> GetAsync(Guid connectorId, Guid requestId, CancellationToken ct = default);

    /// <summary>
    /// O pedido mais recente do conector (qualquer estado) — para a tela retomar depois de recarregar. Com
    /// <paramref name="source"/>, o mais recente DAQUELA fonte; sem ela, o mais recente de qualquer fonte.
    /// </summary>
    Task<KnightSyncRequestView?> GetLatestAsync(Guid connectorId, KnightSourceType? source = null, CancellationToken ct = default);

    /// <summary>Conectores com pedido ATIVO (Pending/Running) — a listagem mostra "sincronizando".</summary>
    Task<System.Collections.Generic.IReadOnlySet<Guid>> ActiveConnectorIdsAsync(CancellationToken ct = default);
}

/// <summary>Lease de um pedido adquirido pelo worker.</summary>
public sealed record KnightSyncLease(Guid RequestId, Guid TenantId, Guid ConnectorId, KnightSourceType Source, Guid LeaseId, int Attempts);

/// <summary>
/// Identifica, para a avaliação, o pedido que a originou e o lease sob o qual ela roda. Com ele, a avaliação é
/// VINCULADA ao pedido na MESMA transação que a grava — só por quem detém o lease e só se o pedido ainda não tem
/// resultado. Sem ele, o caminho de avaliação é o de sempre.
/// </summary>
public sealed record KnightSyncBinding(Guid RequestId, Guid LeaseId);

/// <summary>Desfecho da avaliação pedida por uma sincronização.</summary>
public enum KnightSyncRunOutcome
{
    /// <summary>Esta tentativa gravou a avaliação e a vinculou ao pedido.</summary>
    Registered = 0,

    /// <summary>O pedido JÁ tinha avaliação vinculada (tentativa anterior): nada foi coletado nem gravado de novo.</summary>
    AlreadyRegistered = 1,

    /// <summary>O lease não pertence mais a esta tentativa: nada foi gravado — outra tentativa responde pelo pedido.</summary>
    LeaseLost = 2,
}

/// <param name="Assessment">A avaliação vinculada ao pedido; <c>null</c> quando <see cref="KnightSyncRunOutcome.LeaseLost"/>.</param>
public sealed record KnightSyncRunResult(KnightSyncRunOutcome Outcome, KnightAssessment? Assessment);

/// <summary>A avaliação já vinculada a um pedido, lida da própria execução gravada.</summary>
public sealed record KnightSyncLinkedResult(Guid RunId, KnightSourceState SourceState);

/// <summary>Operações de fila (entre tenants, sob contexto de sistema) usadas SÓ pelo worker.</summary>
public interface IKnightSyncQueue
{
    Task<KnightSyncLease?> TryClaimNextAsync(CancellationToken ct = default);
    Task<bool> RenewAsync(Guid requestId, Guid leaseId, CancellationToken ct = default);

    /// <summary>
    /// Finaliza o pedido com a avaliação vinculada. Guardado pelo lease e pelo vínculo: nunca finaliza com uma
    /// avaliação diferente da que a transação da avaliação gravou no pedido.
    /// </summary>
    Task<bool> CompleteAsync(Guid requestId, Guid leaseId, Guid runId, KnightSourceState sourceState, string message, CancellationToken ct = default);

    /// <summary>
    /// Registra a falha. Guardado pelo lease E pela ausência de vínculo: um pedido que já tem avaliação gravada
    /// nunca é marcado como falho com "nenhuma avaliação nova foi registrada".
    /// </summary>
    Task<bool> FailAsync(Guid requestId, Guid leaseId, string category, string message, CancellationToken ct = default);

    /// <summary>
    /// A avaliação já vinculada ao pedido (gravada por uma tentativa anterior), ou <c>null</c>. É a identificação
    /// DURÁVEL que permite retomar sem coletar de novo — nunca inferida por horário, fonte ou "última avaliação".
    /// </summary>
    Task<KnightSyncLinkedResult?> GetLinkedResultAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>Devolve o pedido à fila sem consumir tentativa (encerramento da aplicação).</summary>
    Task<bool> ReleaseAsync(Guid requestId, Guid leaseId, CancellationToken ct = default);
}

/// <summary>Parâmetros da fila de sincronização do KNIGHT (seção <c>KnightSync</c>).</summary>
public sealed class KnightSyncOptions
{
    public const string SectionName = "KnightSync";

    /// <summary>Duração do lease; o worker o renova a cada <see cref="HeartbeatSeconds"/> enquanto trabalha.</summary>
    public int LeaseSeconds { get; set; } = 120;
    public int HeartbeatSeconds { get; set; } = 30;
    public int PollSeconds { get; set; } = 3;

    /// <summary>
    /// Tentativas antes de desistir. 2 = a coleta interrompida (reinício no meio) é refeita UMA vez; na
    /// seguinte o pedido termina como "interrompido", nunca em espera infinita.
    /// </summary>
    public int MaxAttempts { get; set; } = 2;

    public bool Enabled { get; set; } = true;

    public bool TryValidate(out string? error)
    {
        error = null;
        if (LeaseSeconds < 10) error = $"{SectionName}: LeaseSeconds deve ser >= 10.";
        else if (HeartbeatSeconds < 1 || HeartbeatSeconds >= LeaseSeconds) error = $"{SectionName}: HeartbeatSeconds deve ser >= 1 e menor que LeaseSeconds.";
        else if (PollSeconds < 1) error = $"{SectionName}: PollSeconds deve ser >= 1.";
        else if (MaxAttempts < 1) error = $"{SectionName}: MaxAttempts deve ser >= 1.";
        return error is null;
    }
}
