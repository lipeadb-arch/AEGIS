using AegisScore.Application.Telemetry.Models;
using AegisScore.Domain;

namespace AegisScore.Application.Queries;

/// <summary>
/// Uma lacuna de evidência achatada para o HUD (espelha <see cref="MissingRequirement"/> do domínio).
///
/// Existe em vez de serializar o record de domínio direto por causa do <c>Type</c>: sem
/// <c>JsonStringEnumConverter</c> global na API, um enum ANINHADO sairia como número ("type": 1) e o
/// Angular passaria a depender da ordem do enum C# — exatamente o acoplamento que o resto deste contrato
/// evita com <c>.ToString()</c>. Aqui o tipo já viaja como nome ("Telemetry"/"Documentation"/"Both").
/// </summary>
/// <param name="Type">Natureza da prova ausente — é o que decide o ícone (rede × pasta) no HUD.</param>
/// <param name="SourceIdentifier">Fonte que deveria supri-la: "Entra ID", "MANUAL_AUDIT_REQUIRED".</param>
/// <param name="Description">O que falta, em uma frase, pronta para exibição.</param>
public record MissingRequirementDto(string Type, string SourceIdentifier, string Description);

/// <summary>
/// Estado atual de UM controle NIST do tenant, achatado para consumo do HUD. É um contrato de leitura:
/// o frontend jamais recebe a entidade de domínio (<c>TenantControlState</c>) crua, o que nos deixa
/// evoluir o modelo sem quebrar o Angular — e impede que campos internos vazem por acidente.
///
/// Enums viram <c>string</c> na fronteira ("Compliant", "Telemetry"): um cliente TypeScript não deve
/// depender do valor numérico de um enum C#, que muda ao reordenar o domínio.
/// </summary>
/// <param name="SubcategoryCode">Código NIST ("PR.AA-01") — o identificador que o HUD exibe. Vem do
/// mesmo JOIN que traz o peso, portanto não custa nada e evita uma segunda chamada ao catálogo.</param>
/// <param name="ScorePoints">Pontos obtidos (numerador do Aegis Score).</param>
/// <param name="MaxScorePoints">Peso da subcategoria no catálogo (denominador) — nunca do estado do tenant.</param>
/// <param name="LastVerdictSource">Procedência do veredito vigente: "Telemetry" (autoritativa) ou
/// "Documentary" (crédito parcial). É o que permite ao HUD rotular "50% (Documentado)".</param>
public record TenantControlStateDto(
    Guid SubcategoryId,
    string SubcategoryCode,
    int ScorePoints,
    int MaxScorePoints,
    string ControlStatus,
    string? AiEvidence,
    DateTimeOffset LastEvaluatedAt,
    string LastVerdictSource,
    IReadOnlyList<ComplianceCheck> Checks)
{
    // ---- Enriquecimento para o HUD e para a injeção de contexto da IA -------------------------------
    // Membros ADITIVOS (init) e não parâmetros posicionais, de propósito: o record já tem 9 posições e o
    // idioma de "campo opcional que o motor preenche" no projeto é o init prop (ver ComplianceVerdict.Checks).
    // Todos com default seguro — um controle avaliado antes do enriquecimento existir continua serializando.

    /// <summary>
    /// Gravidade do achado (<c>SeverityLevel</c> como string na fronteira): a do motor de IA quando existe,
    /// senão o proxy derivado do status. É o que tinge o badge do card e ordena o que dói primeiro.
    /// </summary>
    public string Severity { get; init; } = nameof(SeverityLevel.Informational);

    /// <summary>
    /// Série de conformidade do controle para a sparkline de 30 dias. ⚠️ VAZIA hoje — não existe snapshot
    /// por controle (só o agregado diário do tenant); ver <see cref="ComplianceHistoryPoint"/>.
    /// </summary>
    public IReadOnlyList<ComplianceHistoryPoint> HistoricalCompliance { get; init; } = Array.Empty<ComplianceHistoryPoint>();

    /// <summary>Rastro CRU da ferramenta que gerou a não-conformidade (EntraID, SentinelOne…).</summary>
    public TelemetryEvidence? TelemetryEvidence { get; init; }

    /// <summary>Plano de ação inline redigido pelo LLM. O passo a passo completo continua no advisory sob demanda.</summary>
    public string? RemediationPlan { get; init; }

    /// <summary>Confiança auto-declarada do LLM na avaliação (0–100); nula em veredito determinístico.</summary>
    public double? AiConfidenceScore { get; init; }

    /// <summary>Vetores de ataque abertos pela falha (mapeamento de ameaças).</summary>
    public IReadOnlyList<string> ThreatLandscape { get; init; } = Array.Empty<string>();

    /// <summary>Tempo médio de detecção em minutos (MTTD) — DE/RS/RC; nulo onde não se aplica.</summary>
    public int? MttdMinutes { get; init; }

    /// <summary>Tempo médio de resposta em minutos (MTTR) — DE/RS/RC; nulo onde não se aplica.</summary>
    public int? MttrMinutes { get; init; }

    /// <summary>
    /// Lacunas de evidência que sustentam a não-conformidade, discriminadas entre telemetria e
    /// documentação. É o que permite ao HUD separar "falta o log" (ícone de rede, ação: ligar conector)
    /// de "falta a política" (ícone de pasta, ação: subir documento) — duas pendências com donos,
    /// prazos e orçamentos diferentes. Vazia quando o controle é conforme ou quando a reprovação é de
    /// MÉRITO (a evidência existia e o controle falhou).
    /// </summary>
    public IReadOnlyList<MissingRequirementDto> MissingRequirements { get; init; } = Array.Empty<MissingRequirementDto>();
}

/// <summary>
/// Consulta de leitura do estado de conformidade de TODOS os controles do tenant — a matriz que alimenta
/// o HUD de scoring. O CONTRATO vive na Application (que não conhece EF Core); a implementação sobre o
/// AegisScoreDbContext mora na Infrastructure — mesmo padrão porta/adaptador de
/// <see cref="ICurrentScoreQuery"/>, <see cref="ITenantScoreTrendQuery"/> e <see cref="IGetPendingControlsQuery"/>.
///
/// Zero Trust: o tenant NÃO é parâmetro. O isolamento é fail-closed via ITenantContext + Global Query
/// Filter, de modo que a consulta enxerga exclusivamente o tenant resolvido do claim <c>tenant_id</c>.
/// </summary>
public interface IControlStateDashboardQuery
{
    /// <summary>Estado de cada controle avaliado do tenant ambiente, ordenado pelo código NIST.</summary>
    Task<IReadOnlyList<TenantControlStateDto>> GetDashboardAsync(CancellationToken ct = default);
}
