using System.Text.Json;
using AegisScore.Domain;

namespace AegisScore.Api.Contracts;

// ---- Auth ----
/// <summary>
/// [AEGIS-AUD-012] <paramref name="LastTenantId"/> é a DICA do último ambiente usado, lembrada pelo cliente.
/// É OPCIONAL e nunca confiada sem revalidação: o backend só a reutiliza se ainda houver membership ativo nela.
/// </summary>
public record LoginRequest(string Email, string Password, Guid? LastTenantId = null);

/// <summary>O refresh token NÃO trafega aqui — vai apenas no cookie HttpOnly. Só o access token é exposto.</summary>
public record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt);

/// <summary>
/// [AEGIS-AUD-012] Resposta do login/troca federada com desfecho EXPLÍCITO — o cliente nunca é jogado num
/// tenant escolhido em silêncio. <paramref name="Status"/> discrimina:
///  - <c>"authenticated"</c>: sessão emitida (<paramref name="AccessToken"/> presente; cookie de refresh setado);
///  - <c>"selection_required"</c>: vários acessos sem último tenant válido — o cliente escolhe um dos
///    <paramref name="Tenants"/> e conclui em <c>POST /auth/select-tenant</c> com o <paramref name="SelectionTicket"/>.
/// Os campos de cada desfecho só vêm preenchidos no desfecho correspondente.
/// </summary>
public record LoginResultResponse(
    string Status,
    string? AccessToken = null,
    DateTimeOffset? AccessTokenExpiresAt = null,
    string? SelectionTicket = null,
    DateTimeOffset? SelectionTicketExpiresAt = null,
    IReadOnlyList<TenantOptionDto>? Tenants = null);

/// <summary>
/// [AEGIS-AUD-012] Corpo da conclusão da seleção inicial de ambiente. A identidade vem SÓ do
/// <paramref name="SelectionTicket"/> assinado (nunca do corpo); só o ALVO trafega, revalidado no serviço.
/// </summary>
public record SelectTenantRequest(string SelectionTicket, Guid TargetTenantId);

/// <summary>
/// [AEGIS-AUD-012] Corpo OPCIONAL da troca federada: só a dica do último ambiente. A identidade corporativa
/// vem das claims do token Entra validado, nunca daqui.
/// </summary>
public record FederationExchangeRequest(Guid? LastTenantId = null);

// ---- Tenant Switcher (SSO simulado) ----
/// <summary>
/// Um ambiente disponível no seletor do HUD. <paramref name="Role"/> é o papel NAQUELE cliente — a
/// mesma pessoa pode ser TenantAdmin num e Analyst noutro.
/// </summary>
public record TenantOptionDto(Guid Id, string Name, string Slug, string Role);

/// <summary>
/// Corpo da troca de ambiente. Só o ALVO trafega: a pessoa vem da claim <c>account_id</c> do JWT, que
/// o cliente não consegue forjar. Aceitar e-mail/conta aqui reabriria o vetor que a conta global fecha.
/// </summary>
public record SwitchTenantRequest(Guid TargetTenantId);

// ---- Platform: provisionamento global de identidade (PlatformAdmin) ----
/// <summary>
/// [AEGIS-AUD-010] Provisionamento de uma identidade GLOBAL. Só o e-mail e, conforme o modo de
/// autenticação, uma senha local OPCIONAL — NÃO há TenantId, membership, papel nem lista de tenants.
/// A senha (quando enviada) trafega em claro dentro do TLS e é derivada em PBKDF2 no servidor; ausência é
/// federated-only (a conta autentica só pelo Entra). Nunca persistida nem registrada em log.
/// </summary>
public record ProvisionIdentityRequest(string Email, string? Password = null);

/// <summary>
/// Identidade global na visão da API. Deliberadamente SEM <c>PasswordHash</c> — nem o hash sai daqui.
/// <paramref name="HasLocalCredential"/> distingue conta local/híbrida de federated-only sem revelar o segredo.
/// </summary>
public record PlatformIdentityDto(Guid Id, string Email, bool HasLocalCredential, DateTimeOffset CreatedAt);

/// <summary>
/// Corpo da redefinição ADMINISTRATIVA de senha (<c>POST /api/v1/platform/identities/{accountId}/password</c>).
/// Só a nova senha trafega — NÃO há senha atual (o alvo, por definição, não consegue autenticar): a autoridade
/// vem da policy de plataforma. A senha viaja em claro dentro do TLS, é derivada em PBKDF2 no servidor e nunca
/// é persistida em claro, devolvida nem registrada. O alvo é o <c>accountId</c> da rota (uma identidade global).
/// </summary>
public record AdminResetPasswordRequest(string NewPassword);

// ---- Users (concessão de acesso a tenant) ----
/// <summary>
/// [AEGIS-AUD-010] Concessão IDEMPOTENTE de acesso ao tenant ambiente a uma identidade global JÁ EXISTENTE.
/// A chave é o <paramref name="IdentityAccountId"/> (nunca e-mail), e NÃO trafega senha: esta superfície não
/// cria identidade nem toca credencial. O <c>TenantId</c> NÃO trafega — vem do claim <c>tenant_id</c> do JWT.
/// </summary>
public record AssignUserAccessRequest(Guid IdentityAccountId, string DisplayName, TenantRole Role);

/// <summary>Membership na visão da API. Deliberadamente SEM <c>PasswordHash</c> — nem o hash sai daqui.</summary>
public record UserDto(
    Guid Id, Guid TenantId, string Email, string DisplayName, string Role,
    bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);

/// <summary>
/// Um acesso na LISTAGEM tenant-scoped (<c>GET /api/v1/users</c>). Sem <c>TenantId</c> (implícito no
/// contexto) e sem hash. <paramref name="HasLocalCredential"/> é só um booleano — a UI o usa para explicar
/// por que alguém entra pelo provedor corporativo, sem revelar nada do segredo.
///
/// <paramref name="IdentityAccountId"/> é a chave da PESSOA global (não do membership <paramref name="Id"/>):
/// a UI a usa para chamar a rota GLOBAL de redefinição administrativa de senha. Expô-la aqui NÃO concede
/// autoridade — aquela rota permanece protegida pela policy de plataforma — e o isolamento tenant-scoped da
/// listagem é preservado (o filtro global de <c>User</c> segue restringindo ao ambiente).
/// </summary>
public record TenantUserDto(
    Guid Id, Guid IdentityAccountId, string Email, string DisplayName, string Role, bool IsActive,
    bool HasLocalCredential, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);

/// <summary>
/// Onboarding de usuário no tenant ambiente (<c>POST /api/v1/platform/tenant-users</c>): e-mail, nome,
/// papel tenant-scoped e senha inicial OPCIONAL. ⚠️ A senha só é aplicada quando a identidade é CRIADA — se
/// a pessoa já existe, ela é IGNORADA (conceder acesso nunca redefine uma credencial existente).
/// </summary>
public record OnboardTenantUserRequest(
    string Email, string DisplayName, TenantRole Role, string? InitialPassword = null);

/// <summary>
/// Resposta do onboarding. <paramref name="IdentityExisted"/> deixa EXPLÍCITO se a pessoa já existia (e
/// portanto a senha não foi alterada); <paramref name="Outcome"/> discrimina o desfecho para a UI.
/// </summary>
public record OnboardTenantUserResponse(TenantUserDto User, string Outcome, bool IdentityExisted);

/// <summary>
/// Edição tenant-scoped de um membership: nome e/ou papel. Campos ausentes (<c>null</c>) NÃO são alterados.
/// </summary>
public record UpdateMembershipRequest(string? DisplayName = null, TenantRole? Role = null);

/// <summary>Troca da PRÓPRIA senha. Ancorada na sessão (claim <c>account_id</c>) — nunca num id do corpo.</summary>
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

// ---- Framework ----
public record FrameworkDto(Guid Id, string Name, string? Source, IReadOnlyList<FunctionDto> Functions);
public record FunctionDto(string Code, string Name, string Definition, IReadOnlyList<CategoryDto> Categories);
public record CategoryDto(string Code, string Name, string Definition, IReadOnlyList<SubcategoryDto> Subcategories);
public record SubcategoryDto(string Code, string Description);
public record MaturityLevelDto(int Level, string Name, string Description, int Score);

// ---- Onboarding ----
public record CreateTenantRequest(string Name, string Slug);

// ---- [AEGIS-MVP-ADMIN-LIFECYCLE-01] Platform: ciclo de vida administrativo de tenants (PlatformAdmin) ----
/// <summary>
/// Um tenant no catálogo administrativo da plataforma. <paramref name="Status"/> vai como STRING (nome do
/// enum — "Onboarding"/"Active"/"Suspended"), mesmo idioma dos demais DTOs de leitura. Sem dados operacionais
/// do cliente — este é o catálogo, não o painel interno de um tenant.
/// </summary>
public record TenantAdminDto(
    Guid Id, string Name, string Slug, string Status, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

/// <summary>
/// Corpo da renomeação de tenant (<c>PUT /api/v1/platform/tenants/{id}</c>). SÓ o nome de exibição — o
/// <c>Slug</c> é imutável e por isso NÃO trafega aqui.
/// </summary>
public record RenameTenantRequest(string Name);
public record CreateBusinessUnitRequest(string Name, string? Code, string? ManagerName, string? ManagerEmail);
public record CreateProcessRequest(string Name, string? ProcessCategory, ProcessClassification Classification, int ProcessValue);
public record CreateConnectorRequest(
    ConnectorProvider Provider,
    ConnectorCapability Capability,
    string DisplayName,
    ConnectorAuthType AuthType,
    string Settings,             // texto em claro; cifrado no servidor (Data Protection) antes de persistir
    int SyncIntervalMinutes = 360);

// ---- [AEGIS-MVP-MICROSOFT-HUB] Conexão Microsoft unificada ----
/// <summary>
/// Configuração da CONEXÃO MICROSOFT UNIFICADA: a credencial comum (informada UMA vez) e os serviços a conectar.
///
/// ⚠️ <see cref="ClientSecret"/> é escrita-apenas — trafega em claro só sob o TLS, é cifrado no servidor e NUNCA
/// retorna. O backend replica a credencial (cifrada) em cada serviço selecionado; cada um permanece um conector
/// independente. O <c>WorkspaceId</c> de um serviço só é usado pelo Sentinel (Siem).
/// </summary>
public record ConfigureMicrosoftHubRequest(
    string TenantId,
    string ClientId,
    string ClientSecret,
    IReadOnlyList<MicrosoftHubServiceRequest> Services);

/// <summary>Um serviço Microsoft selecionado. <see cref="WorkspaceId"/> só é exigido/usado para <c>Siem</c> (Sentinel).</summary>
public record MicrosoftHubServiceRequest(
    ConnectorCapability Capability,
    int SyncIntervalMinutes = 360,
    string? WorkspaceId = null,
    string? DisplayName = null);

public record IdResponse(Guid Id);

// ---- Connectors ----
/// <summary>
/// Conector configurado, na visão da API. Deliberadamente SEM o blob de credenciais: o segredo é
/// escrita-apenas (entra em claro no <see cref="CreateConnectorRequest.Settings"/>, é cifrado no
/// servidor e só o coletor o decifra). Nunca ecoa numa resposta, nem cifrado.
/// </summary>
/// <param name="HasCredentials">
/// Há segredo guardado? Distingue "configurado" de "cadastrado sem credencial" na UI, sem revelar
/// nada do segredo em si.
/// </param>
public record ConnectorConfigDto(
    Guid Id, string Provider, string Capability, string DisplayName, string AuthType,
    bool Enabled, int SyncIntervalMinutes, DateTimeOffset? LastSyncAt, string LastStatus,
    bool HasCredentials,
    // [AEGIS-AUD-020] Há chave de ingestão configurada? (só o booleano — a chave nunca sai). Distingue um
    // conector genérico de push pronto para receber de um ainda sem credencial própria.
    bool HasIngestionKey = false);

// ---- [AEGIS-AUD-020/041/043] Ingestão genérica de evidências (push SIEM/EDR) ----
/// <summary>
/// Um evento de um lote de ingestão, no vocabulário do EMISSOR (dado NÃO confiável). O cliente NÃO envia
/// TenantId, subcategoria NIST, veredito, score, papel nem ConnectorConfigId — esses campos não existem aqui
/// (o servidor resolve o tenant pelo conector autenticado e o mapping pela autoridade central). <paramref name="Data"/>
/// é o payload bruto (objeto JSON) que será PROTEGIDO em repouso e nunca devolvido.
/// </summary>
public record IngestionEventDto(
    string? EventId,
    string? SignalKey,
    string? EventType,
    string? Source,
    int? Severity,
    double? NumericValue,
    string? Unit,
    DateTimeOffset CollectedAt,
    JsonElement? Data);

/// <summary>Lote de eventos. <paramref name="SchemaVersion"/> versiona o contrato; TenantId NÃO trafega.</summary>
public record IngestionBatchDto(string? SchemaVersion, IReadOnlyList<IngestionEventDto>? Events);

/// <summary>
/// Resposta curta da ingestão: aceitos, deduplicados, erros de contrato e horário de recebimento. Em falha
/// de contrato/mapping, <paramref name="Errors"/> descreve o problema (o payload/segredo nunca aparece aqui).
/// </summary>
public record IngestionResultDto(
    int Accepted, int Deduplicated, int ContractErrors, DateTimeOffset ReceivedAt,
    IReadOnlyList<string>? Errors = null);

/// <summary>
/// [AEGIS-MVP-ADMIN-LIFECYCLE-01] Corpo da edição administrativa de um conector (<c>PUT
/// /api/v1/connectors/{id}</c>): SÓ nome de exibição e intervalo de coleta. ⚠️ NÃO há campo de segredo —
/// editar jamais reescreve a credencial; a rotação é o caminho explícito da (re)configuração.
/// </summary>
public record UpdateConnectorRequest(string DisplayName, int SyncIntervalMinutes);

public record ConnectorHealthDto(string Status, string? Message);
public record SignalDto(string SignalKey, double? NumericValue, string? Unit, int? Severity, IReadOnlyList<string> MappedSubcategoryCodes, DateTimeOffset CollectedAt);
public record SyncResultDto(
    int SignalsCollected, IReadOnlyList<SignalDto> Signals,
    // [AEGIS-MVP-VULN-01] Aditivo: contagens honestas de uma sincronização de vulnerabilidades (null p/ outros conectores).
    VulnerabilitySyncSummaryDto? Vulnerabilities = null,
    // [AEGIS-MVP-SIEM] Aditivo e PROVIDER-NEUTRAL: postura operacional do SIEM (null para os demais conectores). O
    // mesmo campo serve Microsoft Sentinel, Google SecOps e futuros SIEMs — o rótulo da fonte vive dentro do resumo.
    SiemSyncSummaryDto? Siem = null,
    // [AEGIS-MVP-GOOGLE-SECOPS-02] Aditivo e PROVIDER-NEUTRAL: cobertura de detecção (regras × MITRE), dimensão
    // INDEPENDENTE de casos/alertas. Estado por dimensão — completa/parcial/indisponível. NUNCA vira sinal/score.
    DetectionCoverageSyncSummaryDto? DetectionCoverage = null,
    // [AEGIS-MVP-MICROSOFT-COVERAGE-02] Aditivo e PROVIDER-NEUTRAL: postura de configuração/conformidade de
    // dispositivos, em DUAS dimensões independentes. Null para os demais conectores. NUNCA vira sinal/score.
    DevicePostureSyncSummaryDto? DevicePosture = null);

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-02] Resumo ADITIVO e PROVIDER-NEUTRAL de uma sincronização de postura de
/// dispositivos — só estados e agregados. Os estados das TRÊS dimensões (políticas, atribuição e dispositivos)
/// viajam SEPARADOS: uma delas bloqueada por permissão nunca aparece como "zero" nas outras. É FATO CONSULTIVO:
/// não vira EvidenceSignal nem altera o AEGIS Score. Nunca identificador/nome de dispositivo, usuário ou payload.
/// <see cref="TotalDevices"/> é ANULÁVEL: null quando a dimensão de dispositivos não produziu inventário.
/// </summary>
public record DevicePostureSyncSummaryDto(
    string ConfigurationState, string AssignmentState, string DeviceState,
    int PoliciesStored, int DeviceGroupsStored, int? TotalDevices,
    bool ConfigurationPreserved, bool DevicesPreserved);

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Resumo ADITIVO e PROVIDER-NEUTRAL de uma sincronização de cobertura de detecção —
/// só agregados. <see cref="State"/> = Available/Partial/Unavailable (dimensão INDEPENDENTE de casos/alertas). É FATO
/// CONSULTIVO: não vira EvidenceSignal nem altera o AEGIS Score. Nunca nome/texto de regra, credencial ou payload.
/// </summary>
public record DetectionCoverageSyncSummaryDto(
    string Source, string AttackVersion, string State,
    int ActiveRules, int RulesWithMitre, int RulesWithoutMitre,
    int RulesInLiveMode, int RulesWithAlerting, int TechniquesObserved);

/// <summary>[AEGIS-MVP-VULN-01] Contagens de uma sincronização de vulnerabilidades (ativos/CVEs/exposições/observações).</summary>
public record VulnerabilitySyncSummaryDto(
    int MachinesObserved, int AssetsCreated, int CvesUpserted, int ExposuresCreated,
    int ObservationsOpened, int ObservationsReopened, int ObservationsResolved,
    int BindingsDeactivated, int AssetsDeactivated, bool WasComplete,
    int InvalidMachines, int InvalidCves, int InvalidRelations);

/// <summary>
/// [AEGIS-MVP-SIEM] Fotografia OPERACIONAL, PROVIDER-NEUTRAL, de uma sincronização de SIEM — só agregados e instantes
/// (nunca título, entidade, IP, host, usuário, alerta bruto ou payload). É FATO CONSULTIVO: não vira EvidenceSignal
/// nem altera o AEGIS Score. Modelada em DUAS DIMENSÕES INDEPENDENTES (<see cref="Cases"/> e <see cref="Alerts"/>);
/// <see cref="Source"/> identifica a origem ("Microsoft Sentinel", "Google SecOps"). <see cref="IsComplete"/> falso =
/// alguma dimensão parcial/degradada. Contagens ANULÁVEIS: null = não coletada/não aplicável, NUNCA zero sintético.
/// </summary>
public record SiemSyncSummaryDto(
    string Source, bool IsComplete, SiemCasePostureDto Cases, SiemAlertPostureDto Alerts);

/// <summary>
/// [AEGIS-MVP-SIEM] Dimensão de CASOS/INCIDENTES. <see cref="State"/> é PROVIDER-NEUTRAL (Available/Partial/Unsupported/
/// PermissionDenied/Throttled/Timeout/Unavailable) — a UI mostra indisponibilidade quando ≠ Available, nunca "0".
/// <see cref="Period"/> distingue RollingWindow (com <see cref="WindowDays"/>) de CurrentInventory (sem janela).
/// </summary>
public record SiemCasePostureDto(
    string State, string Period, int? WindowDays, bool IsComplete,
    int? Observed, int? Open, int? New, int? Closed,
    int? OpenHighSeverity, int? OpenMediumSeverity, int? OpenLowSeverity, int? OpenInformationalSeverity,
    IReadOnlyList<SiemPriorityCountDto>? OpenByPriority,
    double? MeanTimeToCloseHours, DateTimeOffset? LastEvidenceAt);

/// <summary>[AEGIS-MVP-SIEM] Dimensão de ALERTAS. Mesma semântica de estado/período; contagens ANULÁVEIS.</summary>
public record SiemAlertPostureDto(
    string State, string Period, int? WindowDays, bool IsComplete,
    int? Observed, int? HighSeverity, int? MediumSeverity, DateTimeOffset? LastEvidenceAt);

/// <summary>[AEGIS-MVP-SIEM] Uma contagem de casos por prioridade declarada pela fonte (ex.: Google SecOps).</summary>
public record SiemPriorityCountDto(string Priority, int Count);

// ---- Assessments ----
public record CreateAssessmentRequest(string Name, Guid? FrameworkVersionId);
public record CreateScopeRequest(Guid BusinessProcessId, Guid BusinessUnitId);
public record AiSuggestRequest(
    string SubcategoryCode,
    IReadOnlyList<AnswerInput> Answers,
    IReadOnlyList<string> EvidenceSummaries);
public record AnswerInput(string Question, string Answer, string? Comment);
public record MaturitySuggestionDto(int CurrentLevel, double Confidence, string Rationale);
public record EvaluationUpsertRequest(
    int? CurrentLevel, int? CurrentScore, string? CurrentComments,
    int? TargetLevel, int? TargetScore, string? TargetComments);

public record AggregateDto(string Level, string RefCode, double CurrentScore, double TargetScore, double Gap, int Count);
public record MaturityRollupDto(AggregateDto Overall, IReadOnlyList<AggregateDto> Functions, IReadOnlyList<AggregateDto> Categories);

// ---- Risk ----
public record CreateRiskRequest(string Code, string Title, string? Description, Guid? BusinessProcessId, Guid? BusinessUnitId, string? Threats, string? Vulnerabilities);
public record RiskEvaluationRequest(RiskPhase Phase, int Probability, int Impact, int ProcessValue);
public record RiskEvaluationDto(int Score, string Level);

// ---- Executive dashboard ----
public record ExecutiveDashboardDto(
    string ClientName,
    DateTimeOffset GeneratedAt,
    ExposureCardsDto Exposure,
    IReadOnlyList<RadarPointDto> MaturityByFunction,
    IReadOnlyList<GapPointDto> TopGaps,
    IReadOnlyList<HeatCellDto> RiskHeatmap,
    IReadOnlyList<RiskLevelCountDto> RiskByLevel,
    // ICR ANULÁVEL: null quando NENHUM IcrScore foi medido para o tenant — nunca um proxy sintético.
    IcrDto? Icr);

public record ExposureCardsDto(
    int CriticalProcessesExposed,
    int OverdueActionPlans,
    double OverallMaturity,
    double TargetMaturity);

/// <summary>
/// Resumo do PIOR raio de explosão conhecido do tenant — o "custo do fracasso" em linguagem de negócio.
///
/// ⚠️ Vive FORA do <see cref="ExecutiveDashboardDto"/> de propósito. O dashboard executivo já faz 6
/// consultas e é o que decide o First Contentful Paint; pendurar mais um JOIN nele atrasaria a tela
/// inteira por um painel secundário. Endpoint próprio ⇒ o painel carrega sozinho, depois, sem bloquear.
/// </summary>
/// <param name="RootAssetName">Epicentro — o ativo cujo comprometimento produz o maior alcance.</param>
/// <param name="Score">Magnitude 0–100 do raio (mesma régua do ICR).</param>
/// <param name="RiskLevel">Banda de risco do raio ("Critico", "Alto"…).</param>
/// <param name="ImpactedAssetCount">Ativos alcançados transitivamente a partir do epicentro.</param>
/// <param name="ImpactedProcessCount">Processos de negócio atingidos — a tradução para a diretoria.</param>
/// <param name="MaxDepth">Profundidade máxima da propagação, em saltos.</param>
/// <param name="AssessedAt">Quando este raio foi calculado.</param>
public record BlastRadiusSummaryDto(
    string RootAssetName,
    double Score,
    string RiskLevel,
    int ImpactedAssetCount,
    int ImpactedProcessCount,
    int MaxDepth,
    DateTimeOffset AssessedAt);

public record RadarPointDto(string Function, string FunctionName, double Current, double Target);
public record GapPointDto(string Code, string Name, double Current, double Target, double Gap);
public record HeatCellDto(int Probability, int Impact, int Count);
public record RiskLevelCountDto(string Level, int Count);
public record IcrDto(double Score, string Band);

// ---- Govern: Document Hub ----
public record ConnectDocumentRequest(string Title, GovernanceDocumentType Type, string SourceReference);
/// <summary>
/// Mapeamento documento→controle exposto ao Hub. <paramref name="EvidenceQuote"/> é o TRECHO LITERAL
/// validado (a citação verbatim que sustenta o mapeamento — nulo em heranças não probatórias);
/// <paramref name="Evidence"/> é o RACIONAL da análise (separado do trecho). Só um mapping com trecho
/// literal tem valor probatório.
/// </summary>
public record DocumentMappingDto(
    string SubcategoryCode, double Confidence, string? EvidenceQuote, string? Evidence, bool AnalystConfirmed);
public record GovernanceDocumentDto(
    Guid Id, string Title, string Type, string Source, string? SourceReference,
    string? FileName, string? ContentType, long? FileSizeBytes, string? Sha256,
    DateOnly? DocumentDate, string Status, string AnalysisStatus, string? AnalysisSummary,
    string? AnalysisError, DateTimeOffset? AnalyzedAt, IReadOnlyList<DocumentMappingDto> Mappings);
public record DocumentAcceptedDto(Guid Id, string AnalysisStatus);
public record ConfirmMappingRequest(bool Confirmed, double? Confidence);
/// <summary>Resposta 202 do gatilho manual de sincronização de políticas (Govern): trabalho aceito e enfileirado.</summary>
public record PolicySyncAcceptedDto(Guid TenantId, string Status, string Message);

// ---- Govern: cobertura híbrida (documentos + entrevistas) ----
public record CoverageCellDto(string Code, string Description, string Status, string EvidenceSource);
public record GovernCategoryCoverageDto(string Code, string Name, IReadOnlyList<CoverageCellDto> Subcategories);
public record GovernCoverageDto(double CoveredPct, double PartialPct, IReadOnlyList<GovernCategoryCoverageDto> Categories);
public record GapDto(string Code, string Description, string Status);

// ---- Govern: Auditor Virtual (GRC) ----
public record StartInterviewRequest(string? Title, Guid? AssessmentId, IReadOnlyList<string>? SubcategoryCodes);
public record InterviewMessageDto(
    Guid Id, string Role, string Content, int Sequence, string? TargetSubcategoryCode, DateTimeOffset SentAt);
public record InterviewSessionDto(
    Guid Id, string Title, string Status, IReadOnlyList<string> TargetSubcategoryCodes,
    DateTimeOffset StartedAt, IReadOnlyList<InterviewMessageDto> Messages);
public record PostAnswerRequest(string Content);
public record CoverageChangeDto(string SubcategoryCode, string Status, string EvidenceSource);
public record InterviewTurnDto(
    Guid SessionId, InterviewMessageDto? Question, bool IsComplete,
    CoverageChangeDto? CoverageChange, Guid? IdentifiedRiskId);
public record IdentifiedRiskDto(
    Guid Id, string Title, string Description, string? Cause, string? Consequence,
    string SubcategoryCode, Guid? AssessmentId, bool PromotedToRisk, DateTimeOffset IdentifiedAt);

// ---- Identify: inventário de ativos (ID.AM) — somente leitura (a avaliação é ativa, via telemetria) ----
public record AssetDto(
    Guid Id, string Name, string Category, string? SubType, string? Description,
    int Criticality, string? OwnerName, string? ExternalRef, Guid? BusinessProcessId,
    string DiscoverySource, DateTimeOffset? LastSeenAt, bool IsActive,
    double? RiskScore, string? RiskLevel, DateTimeOffset? RiskScoredAt, DateTimeOffset CreatedAt);

/// <summary>Filtros combinados da grid tática (NIST). Ligados por AND; categorias por OR entre si.</summary>
public class AssetQuery
{
    public List<AssetCategory>? Category { get; set; }   // ?category=Hardware&category=Software
    public RiskLevel? RiskLevel { get; set; }
    public int? Criticality { get; set; }
    public bool? IsActive { get; set; }
    public string? Search { get; set; }                  // Name / SubType / ExternalRef
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

/// <summary>Envelope de paginação genérico (reutilizável por outras grids).</summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount, int TotalPages);

// ---- Telemetry: ingestão passiva de sinais de segurança (EDR/SIEM → motor de IA) ----
/// <summary>
/// Payload do webhook de ingestão de telemetria. Envelope genérico de um alerta de ferramenta de
/// segurança (Defender, Sentinel, CrowdStrike…). O <paramref name="SubcategoryCode"/> é o que direciona
/// o motor: diz QUAL controle NIST esta evidência endereça — o mapeamento evento→controle é
/// responsabilidade do emissor/conector (que conhece a semântica da ferramenta), não do motor, que só
/// julga se a evidência PROVA o controle. <paramref name="RawData"/> é a evidência técnica crua, tratada
/// como dado NÃO confiável (fronteira anti-injeção no User Prompt do avaliador).
/// </summary>
public record TelemetryIngestionRequest(
    string Source, string EventName, string Severity, string SubcategoryCode, string RawData);

/// <summary>
/// Telemetria de UM ativo (Identify / ID.AM), com os metadados táticos que decidem a postura: cobertura
/// de EDR, ciclo de vida do SO, nº de CVEs críticas ativas e se o ativo é vital. O motor os avalia contra
/// o controle de gestão de ativos (default <c>ID.AM-01</c>) e grava com fonte <c>Telemetry</c>.
/// </summary>
public record AssetTelemetryRequest(
    string AssetName,
    EdrCoverageStatus EdrCoverage,
    OsLifecycleStatus OsLifecycle,
    int CriticalVulnerabilitiesCount,
    bool IsCriticalAsset,
    string? SubcategoryCode = null);   // default ID.AM-01 (resolvido no controller)

// ---- Protect (PR): telemetria especializada por categoria (SOC multicloud) ----
// Contratos específicos por categoria do Protect. Todos carregam o SubcategoryCode — o motor avalia a
// evidência CONTRA um controle NIST concreto (PR.AA-01, PR.DS-01, PR.PS-01, PR.IR-01).

/// <summary>PR.AA — Identity & Access Management. Privilégio sem MFA integral é falha crítica.</summary>
public record IdentityProtectTelemetryDto(
    double PrivilegedMfaCoverage, double StandardMfaCoverage, int StaleAccountsActive,
    bool ConditionalAccessEnforced, string SubcategoryCode);

/// <summary>PR.DS — Data Security. Criptografia de endpoint insuficiente ou tráfego em claro reprova.</summary>
public record DataProtectTelemetryDto(
    double EndpointEncryptionCoverage, int DlpActivePoliciesCount, bool UnencryptedTrafficDetected,
    string SubcategoryCode);

/// <summary>PR.PS — Platform Security. Hardening CIS abaixo do mínimo ou patch crítico pendente reprova.</summary>
public record PlatformProtectTelemetryDto(
    double CisBenchmarkComplianceRate, bool AppLockerEnforced, int MissingCriticalPatchesCount,
    string SubcategoryCode);

/// <summary>PR.IR — Technology Infrastructure Resilience. Firewall sem política default-deny reprova.</summary>
public record NetworkProtectTelemetryDto(
    bool MicrosegmentationActive, bool DefaultDenyFirewallEnforced, string SubcategoryCode);

// ---- Detect (DE): telemetria especializada por categoria (SOC avançado) ----
// Contratos por categoria do Detect. NB (NIST CSF 2.0): a função DE tem apenas DE.AE (Adverse Event
// Analysis) e DE.CM (Continuous Monitoring); o antigo DE.DP (Detection Processes) do CSF 1.1 foi absorvido
// em DE.AE (ex.: DE.AE-06 herda o DE.DP-4). Códigos reais sugeridos: DE.AE-02, DE.CM-01, DE.AE-06.

/// <summary>DE.AE — Adverse Event Analysis. Anomalia grave não investigada ou fadiga de alerta reprova.</summary>
public record AnomaliesDetectTelemetryDto(
    int UninvestigatedHighAnomaliesCount, double FalsePositiveRate, int CorrelationRulesFiredCount,
    string SubcategoryCode);

/// <summary>DE.CM — Continuous Monitoring. Cobertura de logs críticos baixa ou ativo crítico sem monitoração reprova.</summary>
public record MonitoringDetectTelemetryDto(
    double CriticalLogSourceCoverage, int UnmonitoredCriticalAssetsCount, double NetworkVisibilityCoverage,
    string SubcategoryCode);

/// <summary>Detection Engineering (o antigo DE.DP, hoje sob DE.AE). Baixa cobertura MITRE ou ataques simulados não detectados reprova.</summary>
public record ProcessDetectTelemetryDto(
    double MitreAttckCoverageRate, int ActiveDetectionRulesCount, double SimulatedAttacksDetectedRate,
    string SubcategoryCode);

// ---- Respond (RS) & Recover (RC): resposta a incidentes e resiliência (SOC de alta performance) ----
// Códigos reais no catálogo CSF 2.0: RS.MA-01, RS.MI-01, RC.RP-01.

/// <summary>RS.MA — Incident Analysis. Reconhecimento lento (MTTA) ou baixa cobertura de threat hunting reprova.</summary>
public record AnalysisRespondTelemetryDto(
    int MeanTimeToAcknowledgeMins, double ThreatHuntingCoverageRate, string SubcategoryCode);

/// <summary>RS.MI — Incident Mitigation. Sem isolamento automatizado ou resposta lenta (MTTR) reprova.</summary>
public record MitigationRespondTelemetryDto(
    bool AutomatedIsolationEnabled, int MeanTimeToRespondMins, string SubcategoryCode);

/// <summary>RC.RP — Recovery Plan Execution. Backup mutável, integridade não-Valid ou RTO não atendido reprova.</summary>
public record ExecutionRecoverTelemetryDto(
    bool ImmutableBackupsEnabled, string BackupIntegrityStatus, bool RecoveryTimeObjectiveMet,
    string SubcategoryCode);

// ---- Govern (GV): telemetria estruturada de governança (além da análise documental) ----
// Governança não se resume a ler PDFs: métricas estruturadas de cadeia de suprimentos (GV.SC) e de
// papéis/autoridades (GV.RR) chegam como telemetria — fonte AUTORITATIVA, não o teto documental de 50%.
// Códigos reais no catálogo CSF 2.0: GV.SC-01, GV.RR-01.

/// <summary>GV.SC — Cybersecurity Supply Chain Risk Mgmt. Fornecedor de TI com acesso à rede sem auditoria de terceiros ativa reprova.</summary>
public record SupplyChainTelemetryDto(
    int SuppliersWithNetworkAccess, int CriticalSuppliersCount, bool ThirdPartyAudited, string SubcategoryCode);

/// <summary>GV.RR — Roles, Responsibilities & Authorities. Conta de administrador sem revisão periódica de acesso reprova.</summary>
public record RolesTelemetryDto(
    int TotalAdminAccounts, int AdminAccountsWithoutReview, bool PrivilegedAccessReviewConfigured, string SubcategoryCode);

/// <summary>Veredito devolvido pela ingestão: o status técnico e os pontos já gravados no ledger.</summary>
public record TelemetryVerdictDto(
    string SubcategoryCode, string Status, int AwardedScore, int MaxScorePoints, int Percentage, string AiEvidence);

// ---- [AEGIS-MVP-EVIDENCE-FABRIC-01] Postura de evidência de identidade (Entra ID) ----
// Projeção CONSULTIVA da Evidence Fabric: separa conector × coleta × controle e NUNCA carrega score/veredito
// NIST. É a leitura honesta do último snapshot compartilhado (a mesma aquisição que alimenta o AEGIS KNIGHT).

/// <summary>Estado por capacidade da coleta (o que a fonte obteve ou por que faltou), sem PII.</summary>
public record IdentityCapabilityDto(string Capability, string Outcome, string? Detail);

/// <summary>
/// Evidência de identidade de UM controle NIST: o estado (NoSource/NeverCollected/CollectedButInsufficient/
/// Evaluated) e a explicação honesta. NÃO carrega status de conformidade, pontos nem percentual — telemetria
/// de identidade não concede score aos controles de identidade nesta fundação.
/// </summary>
public record IdentityControlEvidenceDto(string Code, string Title, string State, string Explanation);

/// <summary>
/// Postura de evidência de identidade projetada da Evidence Fabric: o estado do conector, o da coleta
/// (completa/parcial/nunca), a degradação da última tentativa, a fonte/freshness reais e a evidência por
/// controle NIST — tudo CONSULTIVO, sem tocar o AEGIS Score.
/// </summary>
public record IdentityEvidenceProjectionDto(
    string ConnectorState,
    string CollectionState,
    string LastAttemptState,
    bool IsDegraded,
    string Source,
    string? SchemaVersion,
    DateTimeOffset? CollectedAt,
    DateTimeOffset? LastAttemptAt,
    string? LastAttemptDetail,
    IReadOnlyList<IdentityCapabilityDto> Capabilities,
    IReadOnlyList<IdentityControlEvidenceDto> Controls,
    /// <summary>
    /// [AEGIS-MVP-MICROSOFT-COVERAGE-03] Risco de identidade AGREGADO do mesmo snapshot. <c>null</c> em
    /// snapshots do schema v1 e quando nunca houve coleta — a UI diz "não coletado", nunca "zero".
    /// </summary>
    IdentityRiskDto? IdentityRisk = null,
    /// <summary>[AEGIS-MVP-MICROSOFT-COVERAGE-03] Postura agregada de métodos de autenticação registrados.</summary>
    IdentityAuthenticationPostureDto? AuthenticationPosture = null);

// ---- [AEGIS-MVP-MICROSOFT-COVERAGE-03] Risco de identidade (Microsoft Entra ID Protection) ----
// Contrato de LEITURA, agregado e CONSULTIVO. Não carrega — e não pode carregar — id de usuário, nome,
// userPrincipalName, IP, localização, requestId, correlationId, additionalInfo, user agent, token, segredo
// nem payload bruto do Graph. Não altera o AEGIS Score, o KNIGHT Score, o ledger ou qualquer controle NIST.

/// <summary>Estado de UMA das duas capacidades de risco: desfecho tipado, detalhe sanitizado e completude.</summary>
public record IdentityRiskCapabilityDto(string Outcome, string? Detail, bool HasData, bool IsComplete);

/// <summary>
/// Distribuição por nível. <c>Hidden</c> (nível suprimido pela fonte, tipicamente por licença) e
/// <c>Unknown</c> ficam separados de <c>None</c> — ausência de nível não é ausência de risco.
/// </summary>
public record IdentityRiskLevelsDto(long High, long Medium, long Low, long None, long Hidden, long Unknown);

/// <summary>
/// Distribuição por estado, com os derivados operacionais. <c>Active</c> = em aberto (atRisk +
/// confirmedCompromised); <c>Resolved</c> = tratado; <c>Unknown</c> não entra em nenhum dos dois.
/// </summary>
public record IdentityRiskStatesDto(
    long AtRisk, long ConfirmedCompromised, long Remediated, long Dismissed,
    long ConfirmedSafe, long None, long Unknown, long Active, long Resolved);

/// <summary>Contagem de uma categoria sanitizada (tipo de detecção / método de autenticação).</summary>
public record IdentityRiskCategoryDto(string Category, long Count);

/// <summary>Inventário agregado de usuários em risco. <c>IsComplete=false</c> ⇒ os números são um piso.</summary>
public record IdentityRiskyUsersDto(
    long Total, long Deleted, long Processing, long Live, long Active, long HighRiskActive,
    IdentityRiskLevelsDto Levels, IdentityRiskStatesDto States,
    DateTimeOffset? MostRecentRiskUpdateAt, bool IsComplete);

/// <summary>Detecções agregadas na janela determinística, com o que ficou fora dela explicitado.</summary>
public record IdentityRiskDetectionsDto(
    int WindowDays, DateTimeOffset WindowStart, DateTimeOffset WindowEnd,
    long TotalInWindow, long OutsideWindow, long Undated, long InRecentWindow,
    long Active, long Resolved, long HighRiskActive, long PremiumDetailWithheld,
    long Realtime, long NearRealtime, long Offline, long TimingNotDefined, long TimingUnknown,
    IdentityRiskLevelsDto Levels, IdentityRiskStatesDto States,
    IReadOnlyList<IdentityRiskCategoryDto> TopTypes,
    DateTimeOffset? MostRecentDetectionAt, bool IsComplete);

/// <summary>Postura agregada de métodos de autenticação — conceito DISTINTO de risco e de detecção.</summary>
public record IdentityAuthenticationPostureDto(
    long TotalUsers, long MfaCapable, long MfaRegistered, long PasswordlessCapable, long CapabilityUnknown,
    double? MfaCapableCoveragePercent, double? PasswordlessCoveragePercent,
    IReadOnlyList<IdentityRiskCategoryDto> MethodsRegistered, bool IsComplete);

/// <summary>Postura de risco de identidade: as DUAS capacidades independentes e seus agregados.</summary>
public record IdentityRiskDto(
    IdentityRiskCapabilityDto RiskyUsersCapability,
    IdentityRiskCapabilityDto RiskDetectionsCapability,
    IdentityRiskyUsersDto? RiskyUsers,
    IdentityRiskDetectionsDto? Detections,
    DateTimeOffset EvaluatedAt);

// ---- Auditor Virtual (Copiloto GRC onipresente, com escopo de contexto) ----
/// <summary>Uma fala do histórico do chat (Role: "user"|"assistant"; Content: texto). Dado NÃO confiável.</summary>
public record AuditorChatMessageDto(string Role, string Content);

/// <summary>
/// Turno do Copiloto GRC. <paramref name="ContextScope"/> é o código da tela ativa ("GLOBAL","GV","ID",
/// "PR","DE","RS","RC"), que ajusta dinamicamente o System Prompt da IA. O TenantId NÃO trafega aqui — é
/// resolvido do claim <c>tenant_id</c> do JWT (Zero Trust).
/// </summary>
public record AuditorChatRequestDto(
    string ContextScope, string Message, IReadOnlyList<AuditorChatMessageDto>? History);

/// <summary>
/// Resposta do Copiloto com ROTEAMENTO DE INTENÇÃO. <paramref name="Intent"/> ("COPILOT"|"START_INTERVIEW")
/// diz à UI como reagir; <paramref name="Metadata"/> é a carga estruturada opcional da intenção (em
/// START_INTERVIEW, semeia a entrevista com a subcategoria investigada).
/// </summary>
public record AuditorChatResponseDto(string Reply, string Scope, string Intent, object? Metadata);

// ---- Risk Assessment (ID.RA) — Raio de Explosão ----

/// <summary>Corpo OPCIONAL do POST de raio de explosão: um cenário de ameaça para simulação. Ausente = raio topológico puro.</summary>
public record BlastRadiusRequestDto(Guid? ScenarioThreatId);

/// <summary>Um ativo colateral no raio de explosão (espelha <see cref="AegisScore.Domain.BlastRadiusImpactNode"/>).</summary>
public record BlastRadiusNodeDto(Guid ImpactedAssetId, int Distance, double PropagatedImpact, string PathStrength);

/// <summary>Resposta do cálculo: score/nível agregado + métricas + os nós impactados (ordenados por impacto).</summary>
public record BlastRadiusResponseDto(
    Guid AssessmentId,
    Guid RootAssetId,
    double BlastRadiusScore,
    string RiskLevel,
    int ImpactedAssetCount,
    int ImpactedProcessCount,
    int MaxDepth,
    DateTimeOffset ComputedAt,
    IReadOnlyList<BlastRadiusNodeDto> ImpactedNodes);

// ---- Scoring: Recomendações de Remediação (Advisories) ----
/// <summary>
/// Corpo do POST de criação de advisory: só o código NIST-alvo trafega. O texto (título, risco, passo a
/// passo) é REDIGIDO pelo motor de IA no servidor — o cliente não injeta prosa. O TenantId NÃO trafega:
/// é resolvido do claim <c>tenant_id</c> do JWT (Zero Trust).
/// </summary>
public record CreateAdvisoryRequest(string SubcategoryCode);

// ---- AEGIS KNIGHT: assessment de postura de identidade e exposição ----
// Enums viajam como STRING (nome), nunca como ordinal — mesmo idioma dos demais DTOs de leitura. O score é
// o score KNIGHT (fórmula própria), ANULÁVEL e DISTINTO do AEGIS Score geral.

/// <summary>Um indicador avaliado de uma execução KNIGHT (espelha KnightIndicatorView; enums como nome).</summary>
public record KnightIndicatorDto(
    string IndicatorId,
    string Title,
    string Category,
    string Severity,
    string Status,
    string Evidence,
    int AffectedObjectCount,
    IReadOnlyList<string> NistCodes,
    IReadOnlyList<string> MitreTechniques,
    string Recommendation,
    DateTimeOffset CollectedAt,
    string SourceType,
    string? NotEvaluatedReason);

/// <summary>Estado por capacidade da fonte (o que foi coletado e o que faltou) — cobertura/limitações na UI.</summary>
public record KnightCapabilityDto(string Capability, string Outcome, string? Detail);

/// <summary>Disponibilidade de uma fonte KNIGHT para o tenant.</summary>
public record KnightSourceDto(string Source, string Label, bool Configured, bool Enabled);

/// <summary>Estado das fontes: Demo sempre disponível; fontes reais conforme configuração por tenant.</summary>
public record KnightSourcesDto(bool DemoAvailable, IReadOnlyList<KnightSourceDto> RealSources);

/// <summary>Contagens por veredito da execução (denormalizadas para leitura direta na UI).</summary>
public record KnightCountsDto(
    int Passed, int Exposed, int Mitigated, int NotEvaluated, int Error, int NotApplicable);

/// <summary>Um risco prioritário do resumo consultivo, citando os indicadores que o embasam.</summary>
public record KnightPriorityRiskDto(string Title, string Rationale, IReadOnlyList<string> IndicatorIds);

/// <summary>Uma ação recomendada (ordenada), citando os indicadores que a motivam.</summary>
public record KnightRecommendedActionDto(int Order, string Action, IReadOnlyList<string> IndicatorIds);

/// <summary>Uma correlação entre achados, citando os indicadores correlacionados.</summary>
public record KnightCorrelationDto(string Description, IReadOnlyList<string> IndicatorIds);

/// <summary>
/// Resumo consultivo estruturado (interpretação/priorização assistida por IA, ou fallback determinístico).
/// NUNCA contém status, severidade, score, cobertura ou mapeamento — a IA não decide nada disso.
/// </summary>
public record KnightAdvisoryDto(
    string ExecutiveSummary,
    IReadOnlyList<KnightPriorityRiskDto> PriorityRisks,
    IReadOnlyList<KnightRecommendedActionDto> RecommendedActions,
    IReadOnlyList<KnightCorrelationDto> Correlations,
    IReadOnlyList<string> CollectionGaps);

/// <summary>
/// Um assessment KNIGHT completo na visão da API. <paramref name="IsDemo"/> deriva de <paramref name="Mode"/>
/// para a UI distinguir claramente DEMONSTRAÇÃO de coleta real. <paramref name="AdvisoryFromAi"/> indica se o
/// resumo veio de IA ou do fallback determinístico.
/// </summary>
public record KnightAssessmentDto(
    Guid Id,
    string Mode,
    bool IsDemo,
    string SourceType,
    string SourceState,
    string Source,
    string Status,
    string CatalogVersion,
    string ScoreFormulaVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    double? Score,
    double Coverage,
    KnightCountsDto Counts,
    IReadOnlyList<KnightIndicatorDto> Indicators,
    IReadOnlyList<KnightCapabilityDto> Capabilities,
    KnightAdvisoryDto? Advisory,
    bool AdvisoryFromAi);

/// <summary>
/// [AEGIS-AUD-035] Requisição de PUBLICAÇÃO de uma fotografia auditável de postura. O cliente só escolhe o
/// instrumento (<paramref name="Type"/>: "AegisScoreNist" ou "Knight") e, para KNIGHT, opcionalmente a fonte
/// (<paramref name="Source"/>: "entra"/"google"/"demo"). NUNCA fornece score/cobertura/contagens/vereditos — o
/// servidor constrói a fotografia exclusivamente pelas autoridades atuais do domínio.
/// </summary>
public record PublishPostureSnapshotRequest(string Type, string? Source);
