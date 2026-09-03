using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Abstractions;

// ---- [AEGIS-MVP-MICROSOFT-COVERAGE-01] Coleta provider-neutral de inventário de software ----
// Espelha o idioma de VulnerabilityCollection/DetectionCoverageSnapshot: o ADAPTADOR normaliza a resposta da fonte
// no vocabulário PROVIDER-NEUTRAL do AEGIS; o EvidenceIngestionExecutor reconcilia (o adaptador NUNCA escreve no
// banco). Software Inventory é EVIDÊNCIA OPERACIONAL/DE EXPOSIÇÃO: não gera EvidenceSignal, veredito NIST nem
// pontos — não toca o AEGIS Score. Nunca hostname/machineId/payload bruto (machineId já é o vínculo natural com o
// Asset via AssetSourceBinding existente — não se repete aqui).

/// <summary>
/// Fato AGREGADO de um produto de software (grão de <c>GET /api/Software</c>) — vendor+nome SEM versão, com os
/// agregados de exposição que a fonte já calcula por produto. <see cref="ExternalProductId"/> é o "id" ESTÁVEL da
/// fonte (ex.: "microsoft-_-edge"); NUNCA usado para correlacionar entre fontes distintas.
/// </summary>
public sealed record SoftwareProductFact(
    string ExternalProductId,
    string? Vendor,
    string? Name,
    int? Weaknesses,
    bool? PublicExploit,
    bool? ActiveAlert,
    int? ExposedMachines,
    double? ImpactScore);

/// <summary>
/// Instalação de UM produto (com versão, quando informada) NUMA máquina — grão de
/// <c>GET /api/machines/SoftwareInventoryByMachine</c>. Este endpoint NÃO repete o "id" do endpoint agregado: a
/// correlação ao produto usa <see cref="Vendor"/>/<see cref="Name"/> normalizados (mesma identidade natural do
/// <see cref="SoftwareProduct"/> consolidado). <see cref="MachineId"/> é validado contra as máquinas da MESMA
/// sincronização (nunca uma nova consulta a <c>/api/machines</c>).
/// </summary>
public sealed record MachineSoftwareInstallation(
    string MachineId,
    string? Vendor,
    string? Name,
    string? Version);

/// <summary>
/// Resultado NORMALIZADO de uma coleta de inventário de software. Ao contrário de <see cref="VulnerabilityCollection"/>
/// (que só tem sucesso/exceção), esta fotografia carrega um <see cref="SoftwareInventoryCollectionState"/> EXPLÍCITO
/// — o coletor NUNCA deixa uma falha classificável (permissão/licença/indisponibilidade/teto) propagar como exceção;
/// ela vira estado. Isso é o que permite a dimensão de software degradar SEM invalidar uma coleta de vulnerabilidades
/// já obtida na MESMA sincronização (e vice-versa). <see cref="IsComplete"/> só é verdadeiro em
/// <see cref="SoftwareInventoryCollectionState.Available"/> — é a condição para resolução/desativação por omissão.
/// </summary>
public sealed record SoftwareInventoryCollection(
    string Source,
    SoftwareInventoryCollectionState State,
    DateTimeOffset AttemptedAt,
    IReadOnlyList<SoftwareProductFact> Products,
    IReadOnlyList<MachineSoftwareInstallation> Installations,
    int InvalidProducts,
    int InvalidInstallations,
    /// <summary>Detalhe SANITIZADO da tentativa (ex.: menção a Software.Read.All) — nunca token/URL/payload.</summary>
    string? Detail = null)
{
    public bool IsComplete => State == SoftwareInventoryCollectionState.Available;
}

/// <summary>
/// Par (vulnerabilidades, inventário de software) produzido por UMA aquisição combinada do Defender — token e
/// <c>/api/machines</c> adquiridos/consultados UMA VEZ, reaproveitados pelas duas dimensões. <see cref="SoftwareInventory"/>
/// é aditivo (nulo quando a fonte não implementa/tenta a dimensão); <see cref="Vulnerabilities"/> preserva
/// integralmente a semântica já estabelecida por <see cref="IVulnerabilityFindingConnector"/>.
/// </summary>
public sealed record VulnerabilityAndSoftwareCollection(
    VulnerabilityCollection Vulnerabilities,
    SoftwareInventoryCollection? SoftwareInventory);

/// <summary>
/// Capacidade COMBINADA de uma fonte que produz vulnerabilidades E inventário de software a partir de UMA ÚNICA
/// aquisição (token + inventário de máquinas). Estende <see cref="IVulnerabilityFindingConnector"/> em vez de
/// substituí-lo: o método legado <see cref="IVulnerabilityFindingConnector.CollectVulnerabilitiesAsync"/> continua
/// válido/testável isoladamente, mas o <c>EvidenceIngestionExecutor</c> PREFERE esta capacidade quando presente —
/// evita autenticar e listar <c>/api/machines</c> duas vezes por sincronização.
/// </summary>
public interface ICombinedVulnerabilityConnector : IVulnerabilityFindingConnector
{
    /// <summary>
    /// Coleta vulnerabilidades e inventário de software numa ÚNICA aquisição (só leitura). Uma falha CLASSIFICÁVEL
    /// da dimensão de software (permissão/licença/indisponibilidade) NUNCA lança — vira estado em
    /// <see cref="SoftwareInventoryCollection.State"/>, preservando a coleta de vulnerabilidades já obtida. Uma
    /// falha de transporte na dimensão de vulnerabilidades é isolada da dimensão de software (não impede a
    /// tentativa de coletar software) mas ainda preserva a semântica fail-closed de <see cref="VulnerabilityCollection.IsComplete"/>.
    /// </summary>
    Task<VulnerabilityAndSoftwareCollection> CollectVulnerabilitiesAndSoftwareAsync(ConnectorConfig config, CancellationToken ct);
}

/// <summary>
/// Contagens HONESTAS de uma reconciliação de inventário de software (superfície do resultado de sincronização).
/// Só números — nunca produto/ativo concretos. Aditivo a <see cref="PullIngestionResult"/>.
/// </summary>
public sealed record SoftwareInventorySyncResult(
    SoftwareInventoryCollectionState State,
    int ProductsUpserted,
    int ProductsCreated,
    int BindingsDeactivated,
    int InstallationsOpened,
    int InstallationsReopened,
    int InstallationsResolved,
    bool WasComplete,
    int InvalidProducts,
    int InvalidInstallations);
