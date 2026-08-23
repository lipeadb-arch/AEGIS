using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Abstractions;

// ---- [AEGIS-MVP-VULN-01] Coleta provider-neutral de vulnerabilidades associadas a ativos ----
// Espelha o idioma de IPostureFindingConnector/PostureFindingCollection: o ADAPTADOR normaliza a resposta da
// fonte no vocabulário PROVIDER-NEUTRAL do AEGIS e o EvidenceIngestionExecutor a reconcilia (o adaptador NUNCA
// escreve no banco). Vulnerabilidades são FATOS OPERACIONAIS/DE EXPOSIÇÃO: não geram EvidenceSignal, veredito
// NIST nem pontos — não tocam o AEGIS Score. Campos sensíveis (IP, aadDeviceId, resposta bruta, PII) NÃO existem
// neste contrato de propósito.

/// <summary>
/// Uma MÁQUINA (dispositivo onboardado) normalizada pelo coletor. Só os campos permitidos e necessários à
/// reconciliação/inventário — nunca IP, aadDeviceId, usuário ou payload bruto.
/// </summary>
/// <param name="MachineId">Id ÚNICO e estável do dispositivo na fonte (compõe a chave externa do ativo).</param>
public sealed record VulnerabilityMachine(
    string MachineId,
    string? ComputerDnsName,
    string? OsPlatform,
    DateTimeOffset? LastSeen);

/// <summary>
/// Metadado GLOBAL e consultável de um CVE (catálogo público) — fato da fonte, somente leitura. O AEGIS nunca
/// inventa CVSS/severidade/exploit. Campos dependentes do tenant (exposedMachines, status de exceção) NÃO moram
/// aqui — o vínculo por tenant é a exposição ativo×CVE.
/// </summary>
public sealed record VulnerabilityCve(
    string CveId,
    string? Name,
    string? Description,
    string? Severity,
    double? CvssScore,
    string? CvssVector,
    DateTimeOffset? PublishedOn,
    DateTimeOffset? UpdatedOn,
    bool? PublicExploit,
    bool? ExploitVerified,
    double? Epss);

/// <summary>
/// Relação MÁQUINA × CVE (a aresta ativo↔ameaça, no vocabulário da fonte). Uma mesma dupla (machine, cve) pode
/// vir em VÁRIAS linhas produto/versão — o reconciliador as COLAPSA numa única exposição, preservando o detalhe
/// de produto como dado normalizado. Nunca a resposta bruta.
/// </summary>
public sealed record MachineCveRelation(
    string MachineId,
    string CveId,
    string? ProductName,
    string? ProductVendor,
    string? ProductVersion,
    string? FixingKbId,
    string? Severity);

/// <summary>
/// Resultado NORMALIZADO de UMA coleta de vulnerabilidades (máquinas + catálogo de CVE + relações). O adaptador
/// não escreve no banco: devolve esta coleção e o executor a reconcilia. <see cref="IsComplete"/> é a condição
/// para RESOLUÇÃO/DESATIVAÇÃO — só uma coleta COMPLETA (todas as páginas dos três endpoints lidas com 200, sem
/// relação órfã) autoriza resolver por omissão uma exposição antes aberta ou desativar uma máquina ausente. Uma
/// coleta parcial/degradada JAMAIS resolve nem desativa. As contagens de inválidos são transparência (registros
/// descartados por estrutura), não uma segunda coleta.
/// </summary>
public sealed record VulnerabilityCollection(
    IReadOnlyList<VulnerabilityMachine> Machines,
    IReadOnlyList<VulnerabilityCve> Cves,
    IReadOnlyList<MachineCveRelation> Relations,
    bool IsComplete,
    int InvalidMachines,
    int InvalidCves,
    int InvalidRelations,
    string SourceLabel);

/// <summary>
/// Capacidade COMPLEMENTAR a <see cref="IEvidenceConnector"/>: um conector que produz VULNERABILIDADES associadas
/// a ativos (máquinas × CVEs). O <c>EvidenceIngestionExecutor</c> detecta esta capacidade no MESMO fluxo pull,
/// coleta e reconcilia via um reconciliador dedicado (upsert idempotente + resolução em coleta completa). Não
/// quebra o contrato de <see cref="IEvidenceConnector"/>: o conector pode não emitir sinais. O adaptador NUNCA
/// escreve no banco — apenas devolve a coleção normalizada.
/// </summary>
public interface IVulnerabilityFindingConnector
{
    ConnectorProvider Provider { get; }
    ConnectorCapability Capability { get; }

    /// <summary>Coleta as vulnerabilidades associadas a ativos (só leitura). Falha da fonte é sanitizada e sobe.</summary>
    Task<VulnerabilityCollection> CollectVulnerabilitiesAsync(ConnectorConfig config, CancellationToken ct);
}

/// <summary>
/// Contagens HONESTAS de uma reconciliação de vulnerabilidades (superfície do resultado de sincronização). Só
/// números — nunca ativos/CVEs concretos. Aditivo ao <see cref="PullIngestionResult"/> para preservar consumidores.
/// Multifonte: as observações e bindings são POR FONTE (conector); ativos/exposições/CVEs são consolidados.
/// </summary>
public sealed record VulnerabilitySyncResult(
    int MachinesObserved,
    int AssetsCreated,
    int CvesUpserted,
    int ExposuresCreated,
    int ObservationsOpened,
    int ObservationsReopened,
    int ObservationsResolved,
    int BindingsDeactivated,
    int AssetsDeactivated,
    bool WasComplete,
    int InvalidMachines,
    int InvalidCves,
    int InvalidRelations);
