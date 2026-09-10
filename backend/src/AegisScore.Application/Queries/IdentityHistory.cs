using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Application.Queries;

/// <summary>
/// [AEGIS-ADM-02] Autoridade ÚNICA de leitura do HISTÓRICO MENSAL do ADM de identidade — tenant IMPLÍCITO
/// (claim + Global Query Filter fail-closed) e SOMENTE LEITURA.
///
/// Lê as consolidações já gravadas. NÃO consulta o Graph, NÃO reconstrói o passado a partir do cadastro atual
/// e — deliberadamente — NÃO dispara consolidação nem expurgo: uma rota de consulta que apaga dados é uma
/// armadilha, porque quem a chama não sabe que está escrevendo.
/// </summary>
public interface IIdentityHistoryQuery
{
    Task<IdentityHistoryViewDto> GetAsync(IdentityHistoryRangeRequest request, CancellationToken ct = default);
}

/// <summary>
/// Período pedido, em meses de calendário UTC. <c>null</c> nas duas pontas = a janela retida inteira. A
/// consulta é SEMPRE limitada: um período fora da janela é recortado, não negado em silêncio nem expandido.
/// </summary>
public sealed record IdentityHistoryRangeRequest(DateOnly? FromMonth = null, DateOnly? ToMonth = null);

/// <summary>
/// O que se sabe sobre UM mês. As quatro respostas são diferentes e não podem colapsar numa só:
///
///   • <see cref="NotConsolidated"/> — a consolidação ainda não passou por este mês. Não afirma nada sobre coleta.
///   • <see cref="NoCollection"/> — consolidado, e não houve NENHUMA coleta no mês. Não é zero: é silêncio.
///   • <see cref="NoData"/> — houve tentativa e NENHUMA produziu dados (falha, permissão, indisponibilidade).
///   • <see cref="Collected"/> — há fotografia do mês, completa ou declaradamente parcial.
/// </summary>
public enum IdentityHistoryMonthState { NotConsolidated = 0, NoCollection = 1, NoData = 2, Collected = 3 }

/// <summary>
/// A série do tenant. Os limites de retenção viajam junto para que a leitura saiba POR QUE a série termina
/// onde termina — sem eles, um mês ausente por prazo parece um mês sem coleta.
/// </summary>
public sealed record IdentityHistoryViewDto(
    DateOnly FromMonth,
    DateOnly ToMonth,
    DateOnly CurrentMonth,
    DateOnly OldestRetainedMonth,
    int RetainedMonths,
    int DetailRetentionDays,
    IReadOnlyList<IdentityHistoryDirectoryDto> Directories);

/// <summary>
/// Uma ORIGEM — provedor + namespace do diretório. Origens distintas nunca são somadas: seriam populações
/// diferentes apresentadas como uma só.
/// </summary>
public sealed record IdentityHistoryDirectoryDto(
    string Provider,
    string DirectoryNamespace,
    IReadOnlyList<IdentityHistoryMonthDto> Months);

/// <summary>
/// UM mês da série.
/// </summary>
/// <param name="Month">Mês de calendário UTC, no formato <c>yyyy-MM</c>.</param>
/// <param name="IsProvisional">TRUE no mês corrente: ele ainda pode receber coletas.</param>
/// <param name="AcquisitionCount">
/// Coletas registradas no mês — o denominador do que se está olhando, e um número HISTÓRICO: ele conta também
/// as coletas cuja evidência operacional a retenção já removeu. Não é "quantas coletas ainda existem no
/// banco", e é por isso que não encolhe quando o expurgo passa.
/// </param>
/// <param name="RetiredAcquisitionCount">
/// Quantas das coletas contadas acima já saíram por retenção. É o que separa "este mês teve três coletas" de
/// "temos três coletas deste mês guardadas" — duas afirmações diferentes que um número só faria colapsar.
/// </param>
/// <param name="DetailRetentionNote">
/// Presente quando o DETALHE OPERACIONAL deste mês já foi alcançado pela retenção. Os valores e a completude
/// exibidos continuam sendo os APURADOS na coleta; o que não existe mais é a lista de objetos por trás deles.
/// <c>null</c> significa que a retenção ainda não passou por este mês.
/// </param>
/// <param name="Comparable">
/// FALSE quando comparar este mês com o anterior da série induziria a erro: mês sem dados, ou mudança de
/// versão de schema/normalização entre eles. Uma reinterpretação nossa não pode parecer evolução de postura.
/// </param>
/// <param name="ComparabilityNote">O motivo, em texto, quando <paramref name="Comparable"/> é falso.</param>
public sealed record IdentityHistoryMonthDto(
    string Month,
    IdentityHistoryMonthState State,
    bool IsProvisional,
    int AcquisitionCount,
    int DataProducingCount,
    int RetiredAcquisitionCount,
    string? DetailRetentionNote,
    IdentityHistoryProvenanceDto? Snapshot,
    IdentityHistoryAttemptDto? LastAttempt,
    IReadOnlyList<IdentityHistorySetDto> Sets,
    bool Comparable,
    string? ComparabilityNote,
    DateTimeOffset? ConsolidatedAt,
    string? ConsolidationVersion);

/// <summary>Proveniência da fotografia do mês — de QUAL coleta vieram os números apresentados.</summary>
public sealed record IdentityHistoryProvenanceDto(
    Guid AcquisitionId,
    DateTimeOffset AcquiredAt,
    DateTimeOffset? ObservedAt,
    Guid? ConnectorConfigId,
    string? SourceLabel,
    string? SchemaVersion,
    string? NormalizationVersion,
    string State);

/// <summary>
/// A última TENTATIVA do mês, preservada em separado da fotografia — inclusive quando falhou. É aqui que uma
/// integração quebrada aparece num mês que ainda exibe dados de uma coleta anterior do mesmo mês.
/// </summary>
public sealed record IdentityHistoryAttemptDto(
    Guid AcquisitionId,
    DateTimeOffset At,
    string State,
    string? Detail);

/// <summary>
/// Um conjunto observado na fotografia do mês.
/// </summary>
/// <param name="ObservedCount">
/// <c>null</c> quando o conjunto NÃO foi coletado: um número ali significaria "apuramos", e não foi o caso.
/// </param>
/// <param name="IsComplete">TRUE só quando a coleta enumerou o conjunto inteiro. Parcial permanece parcial.</param>
/// <param name="DoesNotProve">O que a presença/ausência neste conjunto NÃO demonstra — do catálogo do ADM.</param>
public sealed record IdentityHistorySetDto(
    string Set,
    string Label,
    string Outcome,
    int? ObservedCount,
    int PreservedCount,
    bool IsComplete,
    string? Limitation,
    string DoesNotProve);
