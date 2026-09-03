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
/// [AEGIS-MVP-EVIDENCE-FABRIC-01] Contexto da Evidence Fabric de identidade acoplado a UM controle NIST de
/// identidade (PR.AA-01/PR.AA-03/GV.RR-01) no HUD vivo. Faz a postura reconhecer a coleta REAL do AEGIS KNIGHT
/// — fonte e horário — e separa as TRÊS dimensões que o operador precisa distinguir: o estado do CONECTOR
/// (não configurado / desabilitado / credencial ausente / apto), o estado da COLETA (nunca coletado / completa
/// / parcial / última tentativa falhou porém há snapshot preservado) e o estado da EVIDÊNCIA deste controle
/// (sem fonte / nunca coletado / coletado porém insuficiente / efetivamente avaliado).
///
/// NUNCA concede veredito nem pontos: o teto para os controles de identidade nesta fundação é "coletado, porém
/// insuficiente" — a existência de telemetria não é aprovação. Nulo em todo controle que não seja de identidade.
/// Enums viajam como STRING (nome), no mesmo idioma dos demais enums deste contrato.
/// </summary>
/// <param name="ConnectorState">"NotConfigured" | "Disabled" | "MissingCredential" | "Configured".</param>
/// <param name="CollectionState">"NoConnector" | "Disabled" | "MissingCredential" | "NeverCollected" | "Complete" | "Partial".</param>
/// <param name="ControlEvidenceState">"NoSource" | "NeverCollected" | "CollectedButInsufficient" | "Evaluated".</param>
/// <param name="IsDegraded">Há evidência preservada, mas a última tentativa falhou ou o conector já não está apto.</param>
/// <param name="Source">Fonte real do snapshot ("Microsoft Entra ID") — nunca segredo nem PII.</param>
/// <param name="CollectedAt">Horário da coleta que produziu os dados armazenados (freshness). Nulo se nunca coletado.</param>
/// <param name="LastAttemptAt">Horário da última TENTATIVA (onde a degradação aparece sem destruir a evidência válida).</param>
/// <param name="LastAttemptState">Desfecho da última tentativa (Completed/PartialCollection/AuthenticationFailure…).</param>
/// <param name="Explanation">Razão HONESTA: reconhece a coleta e explica por que ela não basta para o requisito do controle.</param>
public record IdentityEvidenceContextDto(
    string ConnectorState,
    string CollectionState,
    string ControlEvidenceState,
    bool IsDegraded,
    string Source,
    DateTimeOffset? CollectedAt,
    DateTimeOffset? LastAttemptAt,
    string LastAttemptState,
    string Explanation);

/// <summary>
/// [AEGIS-MVP-LANGUAGE-01] Classificação DETERMINÍSTICA do motivo de um controle estar <c>NotEvaluated</c> —
/// derivada do <see cref="AegisScore.Domain.RuleEvidenceType"/> da regra (ou da ausência dela), NUNCA de LLM
/// nem de parsing livre de texto. Distingue as quatro situações que o operador precisa separar: o que espera
/// telemetria, o que espera prova documental, o que espera as duas, e o que o AEGIS ainda não sabe avaliar.
///
/// Cruza a fronteira como STRING (<c>.ToString()</c>), no mesmo idioma dos demais enums deste contrato — um
/// cliente TypeScript não deve depender do valor numérico. Nulo em controle AVALIADO (só descreve o não avaliado).
/// </summary>
public enum NotEvaluatedReasonKind
{
    /// <summary>Regra tipada como telemetria: falta um sinal técnico elegível para medir o controle.</summary>
    TelemetryRequired = 0,

    /// <summary>Regra tipada como documental: exige documento ou validação humana, não medível por telemetria.</summary>
    DocumentationRequired = 1,

    /// <summary>Regra tipada como híbrida: exige as DUAS provas (telemetria e validação documental).</summary>
    BothRequired = 2,

    /// <summary>Sem regra/método avaliável: o AEGIS ainda não possui como avaliar este controle.</summary>
    Unsupported = 3,
}

/// <summary>
/// Estado atual de UM controle NIST do tenant, achatado para consumo do HUD. É um contrato de leitura:
/// o frontend jamais recebe a entidade de domínio (<c>TenantControlState</c>) crua, o que nos deixa
/// evoluir o modelo sem quebrar o Angular — e impede que campos internos vazem por acidente.
///
/// [AEGIS-AUD-002] Quatro estados DISTINTOS na fronteira (<see cref="ControlStatus"/>):
/// <list type="bullet">
/// <item><c>Compliant</c> / <c>MitigatedByThirdParty</c> / <c>NonCompliant</c> — AVALIADOS (têm estado);</item>
/// <item><c>NotEvaluated</c> — subcategoria do catálogo SEM <c>TenantControlState</c>: pontos 0 acompanhados
/// inequivocamente do status, datas/fonte nulas, e FORA do denominador do score (entra na cobertura). NÃO é
/// 0% e NÃO é NonCompliant.</item>
/// </list>
///
/// Enums viram <c>string</c> na fronteira ("Compliant", "Telemetry"): um cliente TypeScript não deve
/// depender do valor numérico de um enum C#, que muda ao reordenar o domínio.
/// </summary>
/// <param name="SubcategoryCode">Código NIST ("PR.AA-01") — o identificador que o HUD exibe.</param>
/// <param name="ScorePoints">Pontos obtidos (numerador). Sempre 0 quando <c>ControlStatus == "NotEvaluated"</c>.</param>
/// <param name="MaxScorePoints">Peso da subcategoria no catálogo (denominador) — nunca do estado do tenant.</param>
/// <param name="ControlStatus">"Compliant" | "MitigatedByThirdParty" | "NonCompliant" | "NotEvaluated".</param>
/// <param name="LastEvaluatedAt">Instante da última avaliação — NULO em NotEvaluated.</param>
/// <param name="LastVerdictSource">Procedência do veredito ("Telemetry"/"Documentary") — NULA em NotEvaluated.</param>
public record TenantControlStateDto(
    Guid SubcategoryId,
    string SubcategoryCode,
    int ScorePoints,
    int MaxScorePoints,
    string ControlStatus,
    string? AiEvidence,
    DateTimeOffset? LastEvaluatedAt,
    string? LastVerdictSource,
    IReadOnlyList<ComplianceCheck> Checks)
{
    // ---- Enriquecimento para o HUD e para a injeção de contexto da IA -------------------------------
    // Membros ADITIVOS (init) e não parâmetros posicionais, de propósito: o record já tem 9 posições e o
    // idioma de "campo opcional que o motor preenche" no projeto é o init prop (ver ComplianceVerdict.Checks).
    // Todos com default seguro — um controle avaliado antes do enriquecimento existir continua serializando.

    /// <summary>
    /// [AEGIS-AUD-002] Motivo legível de por que o controle não pontua (ou pontua parcialmente): "Sem
    /// evidência avaliada" (NotEvaluated), a lacuna concreta (NonCompliant) ou o crédito de terceiro
    /// (Mitigated). Nulo em Compliant (pontua integralmente — não há motivo de não-pontuação).
    /// </summary>
    public string? Reason { get; init; }

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

    // ---- [AEGIS-MVP-LANGUAGE-01] Camada de apresentação em LINGUAGEM CLARA (autoral, provider-neutral) ----
    // ADITIVOS (init) com default seguro NULO: um cliente/serviço antigo continua funcionando, e a ausência
    // de redação NUNCA vira o nome genérico da categoria — o frontend degrada para o próprio código NIST.

    /// <summary>Título direto e específico do controle em pt-BR (nunca o nome da categoria). Nulo se sem redação.</summary>
    public string? Title { get; init; }

    /// <summary>O que o controle garante, em uma frase. Nulo se sem redação.</summary>
    public string? Summary { get; init; }

    /// <summary>Por que a ausência do controle importa, em uma frase. Nulo se sem redação.</summary>
    public string? Impact { get; init; }

    /// <summary>Primeira ação prática e curta para avançar no controle. Nulo se sem redação.</summary>
    public string? InitialAction { get; init; }

    /// <summary>
    /// Descrição OFICIAL da subcategoria (conteúdo NIST), como referência técnica SECUNDÁRIA — separada da
    /// redação autoral acima. Nunca é o título principal; existe para quem quiser conferir o texto de origem.
    /// </summary>
    public string? OfficialDescription { get; init; }

    /// <summary>
    /// Motivo DETERMINÍSTICO de o controle ainda não ter sido avaliado (<see cref="NotEvaluatedReasonKind"/> como
    /// string): "TelemetryRequired" | "DocumentationRequired" | "BothRequired" | "Unsupported". NULO em controle
    /// avaliado — só descreve o <c>NotEvaluated</c>. Derivado do tipo de evidência da regra, sem LLM.
    /// </summary>
    public string? NotEvaluatedReason { get; init; }

    /// <summary>
    /// [AEGIS-MVP-EVIDENCE-FABRIC-01] Contexto da Evidence Fabric de identidade para os controles de identidade
    /// (PR.AA-01/PR.AA-03/GV.RR-01): faz o HUD vivo reconhecer que a telemetria real do AEGIS KNIGHT foi coletada
    /// (fonte + horário) e distinguir "sem fonte" de "coletado, porém insuficiente" — sem conceder veredito nem
    /// pontos ao AEGIS Score. Nulo em todo controle que não seja de identidade (default seguro: cliente antigo
    /// continua serializando). Ver <see cref="IdentityEvidenceContextDto"/>.
    /// </summary>
    public IdentityEvidenceContextDto? IdentityEvidence { get; init; }
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
    /// <summary>
    /// Estado de CADA subcategoria do catálogo ativo para o tenant, ordenado pelo código NIST — os
    /// avaliados com o estado real e os SEM estado como NotEvaluated (AUD-002).
    /// </summary>
    Task<IReadOnlyList<TenantControlStateDto>> GetDashboardAsync(CancellationToken ct = default);
}
