using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Abstractions;

// ---- [AEGIS-MVP-POSTURE-02] Coleta provider-neutral de exposições de configuração (postura) ----

/// <summary>
/// Uma exposição de configuração NORMALIZADA por um coletor, no vocabulário PROVIDER-NEUTRAL do AEGIS —
/// nunca a resposta bruta do fornecedor. É um DTO de transporte: o adaptador o produz e o
/// <c>EvidenceIngestionExecutor</c> o reconcilia em <see cref="PostureExposureFinding"/> (o adaptador NUNCA
/// escreve no DbContext). O <c>TenantId</c>/<c>ConnectorConfigId</c> NÃO viajam aqui — são carimbados pelo
/// executor sob o tenant proprietário. Campos sensíveis (actionUrl, updatedBy, comentário, PII) não existem
/// neste contrato de propósito.
/// </summary>
/// <param name="ExternalId">Controle ÚNICO do fornecedor (compõe a chave natural com tenant+conector).</param>
/// <param name="Gap">Gap CALCULADO pelo coletor (<c>MaxScore - CurrentScore</c>); só entra se for &gt; 0.</param>
public sealed record PostureFinding(
    string ExternalId,
    string Title,
    string? Category,
    string? Service,
    string? ActionType,
    double CurrentScore,
    double MaxScore,
    double Gap,
    int? SourceRank,
    string? Tier,
    string? ImplementationCost,
    string? UserImpact,
    string? Remediation,
    string? RemediationImpact,
    IReadOnlyList<string> Threats,
    string? SourceState);

/// <summary>
/// Resultado de uma coleta de exposições. <see cref="IsComplete"/> é a condição para RESOLUÇÃO: só quando a
/// coleta foi COMPLETA (todos os controles e pontuações lidos com sucesso) o reconciliador pode marcar como
/// <c>Resolved</c> uma exposição antes aberta que sumiu do conjunto — uma coleta parcial/incompleta jamais
/// resolve por omissão (ausência de dado não é prova de correção). <see cref="Findings"/> traz só os achados
/// com gap positivo; controles conformes/depreciados não geram exposição aberta.
/// </summary>
public sealed record PostureFindingCollection(
    IReadOnlyList<PostureFinding> Findings,
    bool IsComplete,
    string SourceLabel);

/// <summary>
/// Capacidade COMPLEMENTAR a <see cref="IEvidenceConnector"/>: um conector que, além dos sinais determinísticos
/// do score, produz EXPOSIÇÕES DE CONFIGURAÇÃO acionáveis. O <c>EvidenceIngestionExecutor</c> detecta esta
/// capacidade no MESMO fluxo pull, coleta os findings e os reconcilia (upsert idempotente + resolução em coleta
/// completa). Não quebra o contrato existente de <see cref="IEvidenceConnector"/> — um conector pode implementar
/// só os sinais, ou os dois. O adaptador NUNCA escreve no banco: apenas devolve a coleção normalizada.
/// </summary>
public interface IPostureFindingConnector
{
    ConnectorProvider Provider { get; }
    ConnectorCapability Capability { get; }

    /// <summary>Coleta as exposições de configuração do conector (só leitura). Falha da fonte é sanitizada e sobe.</summary>
    Task<PostureFindingCollection> CollectFindingsAsync(ConnectorConfig config, CancellationToken ct);
}

/// <summary>Saída de UMA coleta combinada: sinais + exposições derivados da MESMA fotografia da fonte.</summary>
public sealed record CombinedCollection(
    IReadOnlyList<EvidenceSignal> Signals,
    PostureFindingCollection Findings);

/// <summary>
/// [AEGIS-MVP-POSTURE-02] Capacidade de coleta COMBINADA numa ÚNICA aquisição da fonte: sinais determinísticos
/// E exposições de configuração vindos da MESMA fotografia (ex.: um único token + <c>secureScores</c> +
/// <c>secureScoreControlProfiles</c> por sincronização). Evita que o executor busque a fonte duas vezes e que
/// sinais e findings de uma sincronização venham de fotografias diferentes. Provider-neutral e COMPLEMENTAR: um
/// conector que NÃO a implemente continua funcionando pelo contrato existente (CollectAsync + CollectFindingsAsync).
/// Sem cache global/estático/temporal — cada chamada faz uma aquisição fresca. O adaptador NUNCA escreve no banco.
/// </summary>
public interface ICombinedEvidenceCollector
{
    ConnectorProvider Provider { get; }
    ConnectorCapability Capability { get; }

    /// <summary>Uma aquisição/coleta normalizada que produz sinais e exposições da MESMA fotografia. Falha sanitizada sobe.</summary>
    Task<CombinedCollection> CollectAllAsync(ConnectorConfig config, CancellationToken ct);
}
