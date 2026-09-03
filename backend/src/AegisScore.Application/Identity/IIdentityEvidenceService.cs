using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Application.Identity;

/// <summary>
/// [AEGIS-MVP-EVIDENCE-FABRIC-01] Estado do CONECTOR de identidade para o tenant — distinto do estado da
/// COLETA (que só existe quando há conector configurado). É a primeira dimensão da postura: "conector
/// conectado" NUNCA equivale a conformidade nem a evidência suficiente.
/// </summary>
public enum IdentityEvidenceConnectorState
{
    /// <summary>Não há conector de identidade (Microsoft/IdentityPosture) neste tenant.</summary>
    NotConfigured = 0,

    /// <summary>Conector existe, porém desabilitado/desconectado — não coleta.</summary>
    Disabled = 1,

    /// <summary>Conector habilitado, mas sem material de autenticação legível (credencial ausente/ilegível).</summary>
    MissingCredential = 2,

    /// <summary>Conector configurado e apto — a coleta pode ocorrer.</summary>
    Configured = 3,
}

/// <summary>
/// Fotografia NORMALIZADA e provider-neutral da evidência de identidade — a forma de aplicação do
/// <see cref="AegisScore.Domain.IdentityEvidenceSnapshot"/>. Carrega os fatos tipados (as MESMAS observações
/// normalizadas que o AEGIS KNIGHT avalia), os estados por capacidade e a PROVENIÊNCIA/COMPLETUDE. É o
/// contrato ÚNICO que KNIGHT, projeção NIST, dashboard e relatórios consomem — nenhuma entidade do Microsoft
/// Graph atravessa para cá, e nenhum dado pessoal é transportado.
/// </summary>
public sealed record IdentityEvidenceSnapshotView(
    Guid TenantId,
    Guid ConnectorConfigId,
    KnightSourceType SourceType,
    string Source,
    string SchemaVersion,
    /// <summary>Estado da coleta que PRODUZIU os dados armazenados (Completed/PartialCollection). Freshness = LastCollectionAt.</summary>
    KnightSourceState DataState,
    /// <summary>Desfecho da última TENTATIVA — onde a degradação aparece sem destruir a última evidência válida.</summary>
    KnightSourceState LastAttemptState,
    DateTimeOffset? LastCollectionAt,
    DateTimeOffset LastAttemptAt,
    string? LastAttemptDetail,
    IReadOnlyList<KnightObservation> Facts,
    IReadOnlyList<KnightCapabilityStatus> Capabilities)
{
    /// <summary>True quando o snapshot carrega dados de uma coleta completa (a leitura dos totais é verdade).</summary>
    public bool HasCompleteData => DataState == KnightSourceState.Completed;

    /// <summary>True quando há QUALQUER dado coletado (completo ou parcial), independente da última tentativa.</summary>
    public bool HasAnyData => DataState is KnightSourceState.Completed or KnightSourceState.PartialCollection;
}

/// <summary>
/// Desfecho de UMA aquisição lógica de evidência de identidade: o estado do conector, o resultado
/// NORMALIZADO da coleta (quando ocorreu) e o snapshot persistido resultante. A coleta acontece UMA vez por
/// operação lógica — este resultado é o que os consumidores (KNIGHT, postura) leem, sem uma segunda consulta
/// ao Graph.
/// </summary>
public sealed record IdentityEvidenceAcquisition(
    IdentityEvidenceConnectorState ConnectorState,
    KnightCollectionResult? CollectionResult,
    IdentityEvidenceSnapshotView? Snapshot);

/// <summary>
/// Serviço de aplicação COMPARTILHADO da Evidence Fabric de identidade. É o ÚNICO ponto que faz a aquisição
/// real do Microsoft Entra ID (via o coletor do KNIGHT) e persiste o snapshot normalizado; tanto o assessment
/// do AEGIS KNIGHT quanto a rota de postura NIST convergem para cá — uma aquisição por operação lógica, sem
/// um segundo cliente Graph, credencial ou consulta duplicada. Tenant SEMPRE do contexto autenticado
/// (fail-closed) — nunca de parâmetro livre.
/// </summary>
public interface IIdentityEvidenceService
{
    /// <summary>
    /// Executa UMA aquisição real da postura de identidade e persiste o snapshot normalizado (tenant-safe,
    /// com proveniência e completude). Uma coleta que FALHE não apaga nem falsifica o último snapshot válido —
    /// registra a degradação separadamente. Recusa conector desabilitado/sem credencial devolvendo o estado
    /// do conector (não coleta). Atualiza a saúde/última sincronização do conector UMA vez por operação.
    /// </summary>
    Task<IdentityEvidenceAcquisition> CollectAsync(CancellationToken ct = default);

    /// <summary>
    /// Lê o ÚLTIMO snapshot persistido (sem nova aquisição) e o estado do conector, projetando a postura de
    /// evidência para o dashboard/relatórios. Não consulta o Graph. Não altera score.
    /// </summary>
    Task<IdentityEvidenceProjection> GetLatestProjectionAsync(CancellationToken ct = default);
}
