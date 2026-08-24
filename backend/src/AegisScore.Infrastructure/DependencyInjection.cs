using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Advisories;
using AegisScore.Application.Knight;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Application.Queries;
using AegisScore.Application.RiskAssessment;
using AegisScore.Application.Scoring;
using AegisScore.Application.Services;
using AegisScore.Infrastructure.Advisories;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Auth;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Documents;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Posture.Export;
using AegisScore.Infrastructure.Queries;
using AegisScore.Infrastructure.RiskAssessment;
using AegisScore.Infrastructure.Scoring;
using AegisScore.Infrastructure.Tenancy;

namespace AegisScore.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers persistence, the AI engine, the connector registry and scoring services.</summary>
    public static IServiceCollection AddAegisScoreInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // [AEGIS-AUD-057] A connection string NÃO é mais versionada em appsettings — vem de user-secrets
        // (Development) ou variável de ambiente/secret manager (demais ambientes). Fail-fast aqui, na
        // composição, evita que UseNpgsql(null) adie a falha para a primeira conexão (mensagem obscura)
        // e barra qualquer tentativa de conexão ambígua. A mensagem NUNCA inclui o valor — só diz onde
        // configurar. Mesmo idioma do fail-fast de Jwt:SigningKey no Program.cs.
        var connectionString = config.GetConnectionString("AegisScore");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "ConnectionStrings:AegisScore ausente ou vazia. Configure a conexão do banco por " +
                "user-secrets (dev) ou variável de ambiente/secret manager (produção). " +
                "Credenciais não devem ser versionadas em appsettings.");

        services.AddDbContext<AegisScoreDbContext>(o => o.UseNpgsql(connectionString));

        // Autenticação: JWT de acesso + refresh tokens com rotação (RTR). Opções da seção "Jwt".
        services.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));
        // [AEGIS-AUD-007] Federação Entra ID: modo (Local/Federated/Hybrid) + identificadores públicos.
        // O fail-fast de configuração incompleta acontece no startup da API (Program.cs), antes de servir.
        services.Configure<FederationOptions>(config.GetSection(FederationOptions.SectionName));
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();   // stateless
        services.AddSingleton<IJwtTokenService, JwtTokenService>();       // stateless
        services.AddSingleton<IRefreshTokenHasher, Sha256RefreshTokenHasher>();  // [AEGIS-AUD-009] stateless
        services.AddScoped<IAuthService, AuthService>();                  // usa o DbContext (scoped)

        // [AEGIS-AUD-010] Autoridades SEPARADAS de identidade:
        //  - Provisionamento GLOBAL da IdentityAccount (PlatformAdmin): cria a pessoa/credencial e nada mais.
        //    Scoped: usa o DbContext, o hasher PBKDF2 e o modo de federação (política de senha por modo).
        services.AddScoped<IIdentityProvisioningService, IdentityProvisioningService>();
        //  - Concessão de acesso ao tenant AMBIENTE (TenantAdmin): cria/atualiza o membership de uma
        //    identidade preexistente. Scoped: usa o DbContext (query filter + stamping fail-closed). NÃO
        //    injeta o hasher — esta autoridade não toca credencial global, por construção.
        services.AddScoped<IUserManagementService, UserManagementService>();
        //  - Onboarding de usuário no tenant (PlatformAdmin + TenantAdmin): cria a identidade global quando
        //    nova E concede o acesso, atomicamente — orquestra as duas autoridades acima sem afrouxá-las.
        services.AddScoped<IPlatformTenantUserService, PlatformTenantUserService>();

        // [Médio 6/Baixo] Encriptação server-side dos segredos de conector (Data Protection). Depende
        // de IDataProtectionProvider, registrado por AddDataProtection() no composition root (Program).
        services.AddSingleton<IConnectorSecretProtector, ConnectorSecretProtector>();

        // [AEGIS-AUD-041] Proteção do payload BRUTO da evidência em repouso — MESMO key ring, purpose PRÓPRIO
        // e distinto. Singleton, como o ConnectorSecretProtector (depende só do IDataProtectionProvider).
        services.AddSingleton<IEvidenceRawPayloadProtector, EvidenceRawPayloadProtector>();

        // [AEGIS-AUD-020/043] Ingestão genérica de evidências: autenticador do endpoint externo (boundary
        // cross-tenant controlado), autoridade determinística de mapping NIST e EXECUTOR ÚNICO push/pull.
        // Scoped: usam o DbContext (o executor abre um contexto por tenant para persistir, como o AuthService).
        services.AddScoped<IConnectorIngestionAuthenticator, ConnectorIngestionAuthenticator>();
        services.AddScoped<INistSignalMapper, NistSignalMapper>();
        services.AddScoped<IEvidenceIngestionExecutor, EvidenceIngestionExecutor>();

        // Onboarding — provisionamento de clientes e configuração de conectores. Scoped: usa o DbContext
        // (query filter + stamping fail-closed) e o protetor de segredos. Concentra a cifragem estática
        // das credenciais, que assim deixa de morar na camada HTTP.
        services.AddScoped<ITenantManagementService, TenantManagementService>();

        // ---- Motor de IA: provedor externo ÚNICO (Anthropic/Claude demonstrativo) e configuração ÚNICA ("Ai") ----
        // Portabilidade: o domínio, os controllers e os workers dependem SÓ das interfaces neutras do AEGIS
        // (IAiAssessmentService de alto nível + ILLMClient de transporte). Trocar de provedor é implementar
        // outro adaptador de ILLMClient, registrá-lo aqui, ajustar a config e rodar os testes de contrato —
        // nada mais muda. Nenhum tipo Anthropic aparece fora da Infrastructure.
        services.Configure<AiOptions>(config.GetSection(AiOptions.SectionName));

        // Motores CONCRETOS (sempre registrados). O gate do Free Tier decide REAL × SIMULADO em RUNTIME;
        // sem chave ou fora da allowlist os motores reais nunca são invocados — o serviço SEMPRE inicia
        // sem chave, em modo simulado (a demo nunca quebra por ausência de chave/rede).
        services.AddSingleton<StubLlmClient>();
        services.AddSingleton<StubAssessmentService>();
        services.AddScoped<AegisAssessmentService>();     // IAiAssessmentService neutro sobre ILLMClient
        // Adaptador Anthropic ISOLADO na Infrastructure (HttpClient tipado + resiliência Polly já existente). O
        // HttpClient nativo cancela aos 100s por padrão — abortaria ANTES do timeout de 120s do Polly. Aqui o
        // timeout nativo é DESABILITADO para o Polly reger o limite sozinho (única autoridade de timeout).
        services.AddHttpClient<AnthropicLlmClient>(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
            .AddAiResilience();

        // Gate do Free Tier (configuração pura), resolver de tenant→slug (scoped; overridável no worker) e os
        // ROTEADORES que são a ÚNICA ligação das interfaces neutras na DI — a fronteira de dados do modo
        // demonstrativo passa por eles: allowlist → Anthropic; fora dela → stub; sem provedor → stub.
        services.AddSingleton<IAiFreeTierGate, AiFreeTierGate>();
        services.AddScoped<IAiTenantResolver, AiTenantResolver>();
        services.AddScoped<ILLMClient>(sp => new TenantScopedLlmRouter(
            sp.GetRequiredService<AnthropicLlmClient>(), sp.GetRequiredService<StubLlmClient>(),
            sp.GetRequiredService<IAiFreeTierGate>(), sp.GetRequiredService<IAiTenantResolver>()));
        services.AddScoped<IAiAssessmentService>(sp => new TenantScopedAssessmentRouter(
            sp.GetRequiredService<AegisAssessmentService>(), sp.GetRequiredService<StubAssessmentService>(),
            sp.GetRequiredService<IAiFreeTierGate>(), sp.GetRequiredService<IAiTenantResolver>()));
        // Escritor ÚNICO do ledger de conformidade (upsert idempotente + regra de scoring). Compartilhado
        // pelo motor de telemetria e pela ponte do Govern — nenhuma das duas fontes reimplementa scoring.
        services.AddScoped<IControlStateWriter, ControlStateWriter>();
        // Rotina ÚNICA de reconciliação documental (retração/recálculo de ledger+cobertura), usada pela
        // exclusão de documento (controller) e pela reanálise (worker constrói a sua própria instância).
        services.AddScoped<IDocumentEvidenceReconciler, DocumentEvidenceReconciler>();
        // RAG por chave: injeta as "Regras do Jogo" (AegisAssessmentRule do 800-53 5.2.0) no prompt do
        // avaliador. Scoped: usa o DbContext. Consumido pelo AegisAiEvaluatorService.
        services.AddScoped<IAssessmentRuleContextBuilder, AssessmentRuleContextBuilder>();
        // Camada de PERSONALIDADE do Auditor (tom, tradução de siglas, proatividade) — o terceiro bloco do
        // System Prompt, ao lado do RAG e do contrato de saída. Singleton: o JSON é lido UMA vez no startup.
        // Caminho relativo ao diretório do binário (o Data/ do Api é copiado para o output).
        var personalityPath = config[$"{AiOptions.SectionName}:PersonalityPath"]
            ?? Path.Combine("Data", "AuditorPersonality.json");
        if (!Path.IsPathRooted(personalityPath))
            personalityPath = Path.Combine(AppContext.BaseDirectory, personalityPath);
        services.AddSingleton<IAuditorPersonaProvider>(sp => new AuditorPersonaProvider(
            personalityPath, sp.GetRequiredService<ILogger<AuditorPersonaProvider>>()));
        services.AddScoped<IAegisAiEvaluatorService, AegisAiEvaluatorService>();

        // Auditor Virtual — construtor do CONTEXTO tenant-scoped (somente leitura) que fundamenta o chat:
        // score/cobertura, lacunas, controles, evidência documental curta, conectores e recomendações. Scoped:
        // usa o DbContext + as projeções de leitura sob o Global Query Filter fail-closed do tenant.
        services.AddScoped<IAuditorContextBuilder, AuditorContextBuilder>();

        // AEGIS KNIGHT — assessment MULTICOLETOR de postura de identidade/exposição. Coletor de DEMONSTRAÇÃO
        // (sintético, sem rede) + registro/factory de coletores (montado a partir de TODOS os IKnightCollector,
        // incluindo o Entra real do pacote Microsoft) + provedor de configuração por tenant (lê ConnectorConfig
        // e DECIFRA os segredos pela proteção existente) + camada consultiva de IA (fallback determinístico) +
        // serviço de aplicação dedicado. Scoped: usam o DbContext (Global Query Filter + stamping fail-closed).
        services.AddScoped<IKnightCollector, DemoKnightCollector>();
        services.AddScoped<IKnightCollectorRegistry, KnightCollectorRegistry>();
        services.AddScoped<IKnightSourceConfigurationProvider, KnightSourceConfigurationProvider>();
        services.AddScoped<IKnightAdvisoryGenerator, KnightAdvisoryGenerator>();
        services.AddScoped<IAegisKnightAssessmentService, AegisKnightAssessmentService>();

        // [AEGIS-AUD-035/036/037] Fotografia AUDITÁVEL de postura — publicação controlada (o servidor constrói
        // pela autoridade do domínio: aegis-score-v1 sobre o ledger e o último assessment knight-score-v1),
        // leitura por tenant e comparação compatível. Scoped: usa o DbContext (Global Query Filter + stamping
        // fail-closed). A fotografia é APPEND-ONLY — o serviço não expõe update/delete.
        services.AddScoped<IPostureSnapshotService, PostureSnapshotService>();

        // [AEGIS-AUD-034] Exportação executiva da fotografia (PDF/CSV) — abstração pequena e focada. Carrega a
        // fotografia pelo Global Query Filter fail-closed, reverifica o ContentHash e renderiza. Somente leitura.
        services.AddScoped<IPostureSnapshotExporter, PostureSnapshotExporter>();

        // Superfície de ingestão passiva de telemetria (webhook EDR/SIEM) — o CHAMADOR do EvaluateAsync.
        // Orquestração fina: normaliza o sinal, resolve o tenant e delega ao motor (fonte Telemetry).
        services.AddScoped<ITelemetryIngestionService, TelemetryIngestionService>();

        // Aegis Score — consultas de leitura do HUD (Score Atual em tempo real + série temporal + KPI
        // de pendências). Scoped: usam o DbContext e, com ele, o Global Query Filter fail-closed do tenant.
        services.AddScoped<ICurrentScoreQuery, CurrentScoreQuery>();
        services.AddScoped<ITenantScoreTrendQuery, TenantScoreTrendQuery>();
        services.AddScoped<IGetPendingControlsQuery, PendingControlsQuery>();
        services.AddScoped<IControlStateDashboardQuery, ControlStateDashboardQuery>();
        services.AddScoped<IWorkspacePostureQuery, WorkspacePostureQuery>();
        // [AEGIS-MVP-POSTURE-02] Leitura tenant-scoped das exposições de configuração (Global Query Filter fail-closed).
        services.AddScoped<IPostureExposureQuery, PostureExposureQuery>();
        // [AEGIS-MVP-VULN-01] Leitura tenant-scoped das vulnerabilidades ativo×CVE (Global Query Filter fail-closed).
        services.AddScoped<IVulnerabilityQuery, VulnerabilityQuery>();
        // [AEGIS-MVP-PRIORITIES-01] Central de Prioridades: leitura COMPOSTA (postura + exposições + vulnerabilidades).
        // Scoped: apenas orquestra as três queries acima, que compartilham o mesmo DbContext scoped da requisição.
        services.AddScoped<IPriorityWorkspaceQuery, PriorityWorkspaceQuery>();
        // Janela de frescor do sinal (TTL) usada pela auditoria de obsolescência do dashboard. TimeProvider
        // é o relógio injetável do .NET — mantém a regra de TTL testável sem congelar o sistema todo.
        services.Configure<ScoringOptions>(config.GetSection(ScoringOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        // Aegis Score — motor consultivo: handler de criação de advisories (escrita). Scoped: usa o
        // DbContext (stamping fail-closed do tenant) + o IAiAssessmentService para redigir o texto.
        services.AddScoped<IGenerateAdvisoryHandler, GenerateAdvisoryHandler>();

        // Connector registry resolves every IEvidenceConnector registered in DI. SCOPED (não singleton): os
        // conectores que ele agrega podem ser scoped — ex.: o MicrosoftSecureScoreConnector, que injeta um typed
        // HttpClient (IEntraGraphClient) e NÃO pode ser capturado no root provider. Os consumidores do registry
        // (ConnectorsController, EvidenceIngestionExecutor) já são scoped, então nenhuma dependência cativa surge.
        services.AddScoped<IConnectorRegistry, ConnectorRegistry>();

        // Govern → Provider Pattern de ingestão de documentos: a fábrica resolve a estratégia de fonte
        // (SharePoint, Google Workspace…) por ConnectorProvider. Os providers concretos são registrados
        // nos pacotes de conector (ex.: AddMicrosoftConnectors → SharePointProvider); adicionar uma fonte
        // nova não toca aqui. Singleton sobre providers singletons — mesmo idioma do IConnectorRegistry.
        services.AddSingleton<IDocumentIntegrationFactory, DocumentIntegrationFactory>();

        // Pure scoring logic (stateless).
        services.AddSingleton<MaturityScoringService>();
        services.AddSingleton<RiskScoringService>();
        services.AddSingleton<IcrScoringService>();

        // Identify (ID.RA) — Raio de Explosão: motor PURO (stateless, como os *ScoringService acima) +
        // orquestrador scoped que carrega o grafo do tenant, chama o motor e persiste o snapshot; o
        // projector é o hook que penaliza ID.RA-01/05 no ledger quando o raio é alto/amplo.
        services.AddSingleton<IBlastRadiusCalculator, BlastRadiusCalculator>();
        services.AddScoped<IBlastRadiusScoreProjector, BlastRadiusScoreProjector>();
        services.AddScoped<IBlastRadiusAssessmentService, BlastRadiusAssessmentService>();

        // Document Hub (Govern): armazenamento, extração de texto e fila de leitura da IA.
        // O worker que consome a fila (DocumentAnalysisWorker) é registrado no host da API.
        var docRoot = config["DocumentStorage:RootPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "document-store");
        services.AddSingleton<IDocumentStorage>(new LocalDocumentStorage(docRoot));
        services.AddSingleton<IDocumentTextExtractor, PlainTextExtractor>();
        // PDFs (Document Hub / Govern) via PdfPig. Mais um IDocumentTextExtractor na coleção: o
        // DocumentAnalysisWorker resolve GetServices<>() e escolhe pelo CanHandle (text/* vs application/pdf).
        services.AddSingleton<IDocumentTextExtractor, PdfTextExtractor>();
        // DOCX (políticas corporativas) via Open XML SDK — só leitura de parágrafos/tabelas, sem macro. Mesmo
        // padrão: a coleção de extratores é a autoridade dos formatos aceitos (upload e worker consultam CanHandle).
        services.AddSingleton<IDocumentTextExtractor, DocxTextExtractor>();
        // [AEGIS-AUD-050] Filas operacionais DURÁVEIS no PostgreSQL — substituem os canais em memória
        // (sem durabilidade), que perdiam trabalho em qualquer reinício e não coordenavam réplicas. O
        // status persistido do próprio GovernanceDocument é a fila de análise; a PolicySyncRequest persistida é
        // a fila de sync. A aquisição é atômica (FOR UPDATE SKIP LOCKED) com lease, retry e limite de
        // tentativas — sem broker externo. Singletons: constroem o DbContext à mão por operação (SystemTenant),
        // como os workers, então dependem só de TimeProvider (relógio testável) + IServiceScopeFactory.
        services.Configure<DocumentAnalysisQueueOptions>(config.GetSection(DocumentAnalysisQueueOptions.SectionName));
        services.Configure<PolicySyncQueueOptions>(config.GetSection(PolicySyncQueueOptions.SectionName));
        services.AddSingleton<IDocumentAnalysisQueue, DurableDocumentAnalysisQueue>();
        services.AddSingleton<IPolicySyncQueue, DurablePolicySyncQueue>();

        return services;
    }
}
