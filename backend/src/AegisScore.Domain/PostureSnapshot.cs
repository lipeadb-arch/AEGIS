using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

// ============================================================================
//  Fotografia AUDITÁVEL de postura — histórico imutável compartilhado
// ============================================================================
// [AEGIS-AUD-035/036/037] Uma PostureSnapshot é a publicação IMUTÁVEL da postura
// de um tenant num instante — a "fotografia" que permanece verdadeira mesmo que
// os dados operacionais (controles, evidências, fórmula, catálogo) mudem depois.
//
// Serve às DUAS autoridades distintas do produto, sem misturá-las: a postura
// AEGIS Score/NIST (fórmula aegis-score-v1 sobre o ledger TenantControlState) e o
// assessment do AEGIS KNIGHT (fórmula knight-score-v1). NÃO existe "score
// combinado" — o Type separa os instrumentos, e a comparação só é válida entre
// fotografias da MESMA família semântica.
//
// O agregado é APPEND-ONLY: depois de publicado nada é atualizado ou removido por
// endpoint operacional. A imutabilidade é garantida no serviço (sem update/delete)
// e reforçada no banco (gatilho que recusa UPDATE/DELETE — ver a migration). O
// ContentHash determinístico permite detectar alteração indevida do conteúdo.

/// <summary>
/// Instrumento de origem de uma fotografia de postura. Separa deliberadamente os dois eixos do produto —
/// nunca há um score combinado entre eles.
/// </summary>
public enum PostureSnapshotType
{
    /// <summary>Postura AEGIS Score/NIST (fórmula aegis-score-v1 sobre o catálogo NIST do framework ativo).</summary>
    AegisScoreNist = 0,

    /// <summary>Assessment do AEGIS KNIGHT (fórmula knight-score-v1) — postura de identidade/exposição.</summary>
    Knight = 1,
}

/// <summary>
/// Fotografia AUDITÁVEL e IMUTÁVEL da postura de UM tenant num instante — tenant-owned. Preserva tudo o que é
/// necessário para reconstruir e explicar o resultado publicado (versões, universo, score anulável, cobertura,
/// contagens, recência, controles/indicadores e referências de evidência) e um hash determinístico do conteúdo.
/// NÃO copia segredo, token, credencial, payload bruto, mensagem crua de exceção nem PII desnecessária.
/// </summary>
public class PostureSnapshot : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Instrumento de origem (AEGIS Score/NIST ou KNIGHT) — separa os eixos, sem score combinado.</summary>
    public PostureSnapshotType Type { get; set; }

    /// <summary>Versão do schema da própria fotografia (ex.: "posture-snapshot-v1") — evolução do formato.</summary>
    public string SchemaVersion { get; set; } = "";

    /// <summary>Versão da fórmula que produziu o score (aegis-score-v1 ou knight-score-v1).</summary>
    public string FormulaVersion { get; set; } = "";

    /// <summary>Versão do catálogo/framework usado (nome do framework NIST ativo ou catálogo KNIGHT, ex.: "ak-knight-v2").</summary>
    public string CatalogVersion { get; set; } = "";

    /// <summary>
    /// Família semântica: a chave de COMPATIBILIDADE de comparação. Duas fotografias só se comparam quando
    /// pertencem à mesma família (além de mesmo tenant, tipo e versões). Ex.: "aegis-nist:{framework}" ou
    /// "knight:{fonte}". Comparar Entra com Google, ou frameworks distintos, é semanticamente inválido.
    /// </summary>
    public string SemanticFamily { get; set; } = "";

    /// <summary>Fonte/provedor do KNIGHT quando aplicável (Demo/MicrosoftEntraId/GoogleWorkspace); nulo para AEGIS Score.</summary>
    public KnightSourceType? SourceType { get; set; }

    /// <summary>Rótulo legível da fonte (ex.: "Microsoft Entra ID"); nulo/oco quando não aplicável.</summary>
    public string? SourceLabel { get; set; }

    /// <summary>Instante UTC da captura/publicação da fotografia (parte do conteúdo assinado pelo hash).</summary>
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    // ---- Postura consolidada (anulável de propósito; NUNCA 0 por ausência de avaliação) ----

    /// <summary>Score (0..100) ANULÁVEL: <c>null</c> quando nada foi avaliado — nunca 0. Score do instrumento, nunca combinado.</summary>
    public double? Score { get; set; }

    /// <summary>Pontos obtidos (numerador do score).</summary>
    public int AchievedPoints { get; set; }

    /// <summary>Pontos possíveis dos itens AVALIADOS (denominador do score).</summary>
    public int PossiblePoints { get; set; }

    /// <summary>Peso ELEGÍVEL do universo (denominador de cobertura). Igual a PossiblePoints quando não há peso elegível separado.</summary>
    public int EligiblePoints { get; set; }

    /// <summary>Cobertura (0..100): peso/itens avaliados sobre o universo elegível.</summary>
    public double Coverage { get; set; }

    /// <summary>Nº de itens (controles/indicadores) efetivamente avaliados — compõem o score.</summary>
    public int EvaluatedItems { get; set; }

    /// <summary>Nº de itens do universo elegível (denominador de cobertura por contagem).</summary>
    public int EligibleItems { get; set; }

    // ---- Contagens por veredito (denormalizadas para leitura barata; semântica compartilhada) ----
    // AEGIS: Compliant/NonCompliant/Mitigated/NotEvaluated. KNIGHT: Passed→Compliant, Exposed→NonCompliant,
    // Mitigated→Mitigated, NotEvaluated→NotEvaluated, além de Error e NotApplicable (0 em AEGIS Score).
    public int CompliantCount { get; set; }
    public int NonCompliantCount { get; set; }
    public int MitigatedCount { get; set; }
    public int NotEvaluatedCount { get; set; }
    public int ErrorCount { get; set; }
    public int NotApplicableCount { get; set; }

    /// <summary>Recência dos dados: instante da evidência/avaliação mais recente que fundamenta a fotografia (nulo se nada avaliado).</summary>
    public DateTimeOffset? DataRecency { get; set; }

    /// <summary>
    /// Hash SHA-256 (hex, 64 chars) determinístico do CONTEÚDO publicado — versões, universo, números, contagens,
    /// recência e todos os itens ordenados. Re-derivável a partir da linha persistida: permite detectar alteração
    /// indevida. NÃO cobre a si mesmo nem os timestamps de auditoria (CreatedAt/UpdatedAt).
    /// </summary>
    public string ContentHash { get; set; } = "";

    // ---- Filhos: exatamente um conjunto por tipo (o outro fica vazio) ----
    /// <summary>Controles NIST congelados (apenas em fotografias AEGIS Score/NIST).</summary>
    public ICollection<PostureSnapshotControl> Controls { get; set; } = new List<PostureSnapshotControl>();

    /// <summary>Indicadores KNIGHT congelados (apenas em fotografias KNIGHT).</summary>
    public ICollection<PostureSnapshotIndicator> Indicators { get; set; } = new List<PostureSnapshotIndicator>();
}

/// <summary>
/// Referência de evidência SANITIZADA que fundamentou um resultado — só metadados administrativos/de origem,
/// nunca payload bruto, segredo ou PII. Persistida como item de lista jsonb no controle/indicador da fotografia.
/// </summary>
/// <param name="Kind">Natureza da evidência: <c>"telemetry"</c> (sinal) ou <c>"document"</c> (mapeamento documental).</param>
/// <param name="Source">Origem/conector/hub que produziu a evidência (ex.: "Generic SIEM", "Document Hub"), sem segredo.</param>
/// <param name="Reference">Identificador estável da evidência (SignalKey/ExternalEventId ou título do documento) — nunca conteúdo bruto.</param>
/// <param name="CollectedAt">Instante da evidência referenciada, quando disponível.</param>
public record PostureEvidenceRef(
    string Kind,
    string Source,
    string Reference,
    DateTimeOffset? CollectedAt);

/// <summary>
/// Um controle NIST CONGELADO dentro de uma fotografia AEGIS Score/NIST — tenant-owned. A fotografia parte do
/// catálogo ATIVO COMPLETO: cada subcategoria elegível vira uma linha, INCLUSIVE quando ainda não avaliada
/// (sem <c>TenantControlState</c>). Um controle não avaliado é distinto de <c>NonCompliant</c>: <see cref="Evaluated"/>
/// é <c>false</c> e <see cref="Status"/>/<see cref="VerdictSource"/>/<see cref="EvaluatedAt"/> ficam nulos, com
/// zero pontos obtidos — o peso elegível é preservado para explicar a cobertura por item.
/// </summary>
public class PostureSnapshotControl : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    public Guid SnapshotId { get; set; }
    public PostureSnapshot? Snapshot { get; set; }

    /// <summary>Código NIST da subcategoria (ex.: "PR.AA-01").</summary>
    public string SubcategoryCode { get; set; } = "";

    /// <summary>Código da Função NIST (GV/ID/PR/DE/RS/RC) — para agrupamento na leitura.</summary>
    public string FunctionCode { get; set; } = "";

    /// <summary>
    /// O controle foi efetivamente avaliado (havia <c>TenantControlState</c>) no instante da fotografia?
    /// <c>false</c> = elegível porém sem avaliação — NÃO é <c>NonCompliant</c>, é "sem score" (reduz a cobertura).
    /// </summary>
    public bool Evaluated { get; set; }

    /// <summary>Estado do controle — <c>null</c> quando NÃO avaliado (nunca confundir com <c>NonCompliant</c>).</summary>
    public ControlStatus? Status { get; set; }

    /// <summary>Pontos obtidos pelo controle (numerador parcial). 0 quando não avaliado — sem virar NonCompliant.</summary>
    public int AchievedPoints { get; set; }

    /// <summary>Peso elegível do controle (MaxScorePoints da subcategoria) — preservado mesmo sem avaliação.</summary>
    public int MaxPoints { get; set; }

    /// <summary>Procedência do veredito vigente (Telemetry/Documentary) — <c>null</c> quando não avaliado.</summary>
    public VerdictSource? VerdictSource { get; set; }

    /// <summary>Instante da última avaliação do controle (recência por item) — <c>null</c> quando não avaliado.</summary>
    public DateTimeOffset? EvaluatedAt { get; set; }

    /// <summary>
    /// Referências de evidência sanitizadas que fundamentaram o veredito VIGENTE (jsonb): para telemetria, a(s)
    /// evidência(s) DECISIVA(S) do recompute (a mais nova, com os empates decisivos); para documental, o(s)
    /// documento(s)/mapeamento(s). Vazia quando não avaliado ou quando a proveniência não é reconstruível.
    /// </summary>
    public List<PostureEvidenceRef> EvidenceRefs { get; set; } = new();
}

/// <summary>
/// Um indicador KNIGHT CONGELADO dentro de uma fotografia KNIGHT — tenant-owned. Registra o veredito
/// determinístico, a evidência factual sanitizada e os mapeamentos, sem segredo/token/payload bruto.
/// </summary>
public class PostureSnapshotIndicator : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    public Guid SnapshotId { get; set; }
    public PostureSnapshot? Snapshot { get; set; }

    /// <summary>ID estável do indicador no catálogo (ex.: "AK-ENTRA-001").</summary>
    public string IndicatorId { get; set; } = "";

    /// <summary>Título original AEGIS do indicador.</summary>
    public string Title { get; set; } = "";

    public KnightIndicatorCategory Category { get; set; }
    public SeverityLevel Severity { get; set; }
    public KnightIndicatorStatus Status { get; set; }

    /// <summary>Evidência factual e legível (números do snapshot), sem PII nem segredo.</summary>
    public string Evidence { get; set; } = "";

    /// <summary>Quantidade de objetos afetados pela exposição (0 quando não se aplica).</summary>
    public int AffectedObjectCount { get; set; }

    /// <summary>Códigos NIST endereçados pelo indicador (jsonb) — projeção informativa.</summary>
    public List<string> NistCodes { get; set; } = new();

    /// <summary>Técnicas MITRE ATT&amp;CK fundamentadas (jsonb).</summary>
    public List<string> MitreTechniques { get; set; } = new();

    /// <summary>Fonte que produziu o resultado (Demo/MicrosoftEntraId/GoogleWorkspace).</summary>
    public KnightSourceType SourceType { get; set; }

    /// <summary>Instante da coleta que originou o resultado.</summary>
    public DateTimeOffset CollectedAt { get; set; }
}
