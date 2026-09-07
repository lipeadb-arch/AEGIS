using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Remediation;

// ============================================================================
//  [AEGIS-MVP-PRODUCT-03] Jornada de remediação de um achado do AEGIS KNIGHT
// ============================================================================
// Abrir o achado → criar a ação → atribuir responsável e prazo → registrar execução → VALIDAR com evidência.
//
// Três invariantes que estes contratos existem para sustentar:
//
//   1. ALTERAR UMA AÇÃO NÃO ALTERA O DIAGNÓSTICO. Nada aqui escreve score, veredito, cobertura, snapshot de
//      avaliação, EvidenceSignal ou TenantControlState. Um plano concluído não torna um achado conforme.
//   2. RELATO NÃO É PROVA. "Marcar como executado" registra o que a pessoa diz ter feito. A comprovação é um
//      ato separado, com método, desfecho e evidência próprios.
//   3. O TEMPO NÃO SE INVERTE. A evidência de validação é sempre uma avaliação POSTERIOR à de origem, da
//      mesma fonte e com regras compatíveis — senão não é evidência, é coincidência.

/// <summary>Ator de uma mutação: quem o token diz ser. Nunca vem do corpo da requisição.</summary>
/// <param name="AccountId">Conta resolvida do token; nula quando o token não a traz.</param>
/// <param name="DisplayName">Nome de exibição do token; vazio quando ausente — nunca substituído por "sistema".</param>
public sealed record RemediationActor(Guid? AccountId, string DisplayName);

/// <summary>Uma entrada da trilha de auditoria, na visão de leitura.</summary>
public sealed record ActionPlanEventView(
    ActionPlanEventKind Kind,
    DateTimeOffset At,
    string ActorName,
    ActionPlanStatus? FromStatus,
    ActionPlanStatus? ToStatus,
    string? Note);

/// <summary>Uma validação registrada, na visão de leitura — método, desfecho e o que foi observado.</summary>
public sealed record ActionPlanValidationView(
    ActionPlanValidationMethod Method,
    ActionPlanValidationOutcome Outcome,
    Guid? ValidationRunId,
    string? EvidenceReference,
    int? ObservedBefore,
    int? ObservedAfter,
    int? ObjectsNoLongerPresent,
    bool ComparedBySets,
    string Rationale,
    DateTimeOffset DecidedAt,
    string DecidedByName);

/// <summary>
/// Uma ação de remediação na visão de leitura. Apresenta SEPARADAMENTE o estado do plano
/// (<see cref="Status"/>, <see cref="IsOverdue"/>, <see cref="NextStep"/>) e o resultado observado no achado
/// (<see cref="LatestValidation"/>) — o relatório e a tela nunca podem colapsar os dois num "resolvido".
/// </summary>
public sealed record ActionPlanView(
    Guid Id,
    string? KnightIndicatorId,
    Guid? OriginRunId,
    int? OriginAffectedCount,
    string Title,
    string? ProposedAction,
    string? ResponsiblePerson,
    string? ResponsibleArea,
    DateOnly? DueDate,
    ActionPlanStatus Status,
    bool IsOverdue,
    bool IsActive,
    string NextStep,
    string? ExecutionNotes,
    string? ExecutionEvidenceRef,
    DateTimeOffset? ExecutedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt,
    /// <summary>Versão lida — devolvida na próxima escrita para detectar atualização conflitante.</summary>
    int Version,
    ActionPlanValidationView? LatestValidation,
    IReadOnlyList<ActionPlanValidationView> Validations,
    IReadOnlyList<ActionPlanEventView> Events);

/// <summary>Criação de uma ação a partir de um ACHADO. Não exige risco, processo de negócio nem cadastro prévio.</summary>
/// <param name="RunId">Avaliação que originou o achado — preservada como referência de ORIGEM.</param>
/// <param name="IndicatorId">Achado endereçado.</param>
public sealed record CreateFindingActionPlanCommand(
    Guid RunId,
    string IndicatorId,
    string Title,
    string? ProposedAction,
    string? ResponsiblePerson,
    string? ResponsibleArea,
    DateOnly? DueDate);

/// <summary>Edição dos campos operacionais e/ou da etapa. Campos nulos permanecem como estão.</summary>
public sealed record UpdateActionPlanCommand(
    int ExpectedVersion,
    string? Title,
    string? ProposedAction,
    string? ResponsiblePerson,
    string? ResponsibleArea,
    DateOnly? DueDate,
    ActionPlanStatus? Status);

/// <summary>Registro da EXECUÇÃO relatada: o que foi feito e a referência da evidência. Não comprova correção.</summary>
public sealed record RecordExecutionCommand(
    int ExpectedVersion,
    string Notes,
    string? EvidenceReference);

/// <summary>
/// Pedido de VALIDAÇÃO. Com <paramref name="ValidationRunId"/>, o servidor compara a avaliação indicada com a
/// de origem e decide o desfecho — o cliente NÃO escolhe o desfecho. Sem ela, registra-se uma atestação
/// HUMANA, que exige uma referência de evidência e é sempre identificada como atestação.
/// </summary>
public sealed record ValidateActionPlanCommand(
    int ExpectedVersion,
    Guid? ValidationRunId,
    string? EvidenceReference,
    string? Note);

/// <summary>Filtro de leitura da lista de ações do tenant.</summary>
/// <param name="IndicatorId">Restringe a um achado; nulo traz todos.</param>
/// <param name="ActiveOnly">Somente ações ativas (Aberto/Em andamento/Aguardando validação).</param>
public sealed record ActionPlanFilter(string? IndicatorId = null, bool ActiveOnly = false);

/// <summary>
/// Conflito de escrita: já existe ação ATIVA para a mesma origem, ou a versão enviada não é a vigente. Nos
/// dois casos o serviço RECUSA em vez de duplicar ou sobrescrever, e informa a ação existente quando há uma.
/// </summary>
public sealed class ActionPlanConflictException : Exception
{
    /// <summary>Ação ATIVA já existente para a mesma origem, quando o conflito é de duplicidade.</summary>
    public Guid? ExistingActionPlanId { get; }

    public ActionPlanConflictException(string message, Guid? existingActionPlanId = null) : base(message) =>
        ExistingActionPlanId = existingActionPlanId;
}

/// <summary>Pedido inválido de mutação (transição impossível, campo obrigatório ausente, evidência inaceitável).</summary>
public sealed class ActionPlanValidationException : Exception
{
    public ActionPlanValidationException(string message) : base(message) { }
}

/// <summary>
/// Serviço de aplicação da jornada de remediação. TODAS as operações são tenant-scoped pelo Global Query
/// Filter fail-closed; uma ação de outro tenant é indistinguível de inexistente (<c>null</c>). NENHUMA delas
/// escreve score, veredito, cobertura ou snapshot de avaliação.
/// </summary>
public interface IRemediationService
{
    /// <summary>
    /// Cria uma ação para um achado de uma avaliação do tenant. Recusa (409) quando já existe ação ATIVA para
    /// o mesmo achado — clicar duas vezes abre a existente, não duplica. Uma ação CONCLUÍDA libera a origem:
    /// o problema pode reaparecer e merecer um ciclo novo.
    /// </summary>
    /// <returns><c>null</c> quando a avaliação ou o achado não existem neste tenant.</returns>
    Task<ActionPlanView?> CreateForFindingAsync(
        CreateFindingActionPlanCommand command, RemediationActor actor, CancellationToken ct = default);

    /// <summary>Ações do tenant (mais recentes primeiro), opcionalmente filtradas por achado e atividade.</summary>
    Task<IReadOnlyList<ActionPlanView>> ListAsync(ActionPlanFilter filter, CancellationToken ct = default);

    /// <summary>Uma ação do tenant com trilha e validações (<c>null</c> quando inexistente ou de outro tenant).</summary>
    Task<ActionPlanView?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Edita campos e/ou avança a etapa, registrando a mudança na trilha.</summary>
    Task<ActionPlanView?> UpdateAsync(
        Guid id, UpdateActionPlanCommand command, RemediationActor actor, CancellationToken ct = default);

    /// <summary>Registra a execução RELATADA e leva a ação para "Aguardando validação".</summary>
    Task<ActionPlanView?> RecordExecutionAsync(
        Guid id, RecordExecutionCommand command, RemediationActor actor, CancellationToken ct = default);

    /// <summary>
    /// Registra uma validação. Com avaliação indicada, o DESFECHO é decidido no servidor pela comparação
    /// (compatibilidade de fonte, regras e ordem temporal + suficiência da evidência para AQUELE indicador).
    /// Sem ela, registra atestação humana com evidência referenciada, identificada como tal.
    /// </summary>
    Task<ActionPlanView?> ValidateAsync(
        Guid id, ValidateActionPlanCommand command, RemediationActor actor, CancellationToken ct = default);
}
