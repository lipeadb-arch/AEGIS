using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Advisories;
using AegisScore.Application.Identity;
using AegisScore.Application.Identity.Adm;
using AegisScore.Application.Knight;
using AegisScore.Application.Posture;
using AegisScore.Application.Remediation;
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
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Remediation;
using AegisScore.Infrastructure.Posture.Export;
using AegisScore.Infrastructure.Queries;
using AegisScore.Infrastructure.Reference;
using AegisScore.Infrastructure.RiskAssessment;
using AegisScore.Infrastructure.Scoring;
using AegisScore.Infrastructure.Tenancy;

namespace AegisScore.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Persistência, motor de IA, AEGIS KNIGHT, AEGIS NIST, biblioteca documental e inventário de ativos.</summary>
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

        // Onboarding — provisionamento de clientes e configuração de conectores. Scoped: usa o DbContext
        // (query filter + stamping fail-closed) e o protetor de segredos. Concentra a cifragem estática
        // das credenciais, que assim deixa de morar na camada HTTP.
        services.AddScoped<ITenantManagementService, TenantManagementService>();

        // [AEGIS-MVP-ADMIN-LIFECYCLE-01] Ciclo de vida ADMINISTRATIVO dos tenants (PlatformAdmin): catálogo,
        // renomear, suspender e reativar. Scoped: opera sobre a entidade GLOBAL Tenant (sem query filter) e
        // usa o DbContext para a guarda de auto-lockout e a revogação de sessões na suspensão.
        services.AddScoped<IPlatformTenantAdminService, PlatformTenantAdminService>();

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
        // Rotina ÚNICA de reconciliação da cobertura documental, usada pela exclusão de documento (controller) e
        // pela reanálise (o worker constrói a sua própria instância). Não grava conformidade nem maturidade.
        services.AddScoped<IDocumentEvidenceReconciler, DocumentEvidenceReconciler>();
        // Camada de PERSONALIDADE do Auditor (tom, tradução de siglas, proatividade) — o terceiro bloco do
        // System Prompt, ao lado do RAG e do contrato de saída. Singleton: o JSON é lido UMA vez no startup.
        // Caminho relativo ao diretório do binário (o Data/ do Api é copiado para o output).
        var personalityPath = config[$"{AiOptions.SectionName}:PersonalityPath"]
            ?? Path.Combine("Data", "AuditorPersonality.json");
        if (!Path.IsPathRooted(personalityPath))
            personalityPath = Path.Combine(AppContext.BaseDirectory, personalityPath);
        services.AddSingleton<IAuditorPersonaProvider>(sp => new AuditorPersonaProvider(
            personalityPath, sp.GetRequiredService<ILogger<AuditorPersonaProvider>>()));

        // [AEGIS-MVP-LANGUAGE-01] Camada de LINGUAGEM CLARA (autoral, provider-neutral, pt-BR) por subcategoria —
        // título/resumo/impacto/ação usados pela jornada do AEGIS NIST. Singleton: o JSON é
        // lido UMA vez, na PRIMEIRA resolução (a fábrica abaixo é lazy). Caminho relativo ao diretório do binário
        // (o Data/ do Api é copiado para o output). FAIL-CLOSED por dentro (ver ControlLanguageCatalog):
        // ausência/duplicidade/campo vazio abortam; a jornada exige redação para todo código ativo (GetRequired).
        var controlLanguagePath = config["Reference:ControlLanguagePath"]
            ?? Path.Combine("Data", "aegis_control_language.pt-BR.json");
        if (!Path.IsPathRooted(controlLanguagePath))
            controlLanguagePath = Path.Combine(AppContext.BaseDirectory, controlLanguagePath);
        services.AddSingleton<IControlLanguageCatalog>(sp => new ControlLanguageCatalog(
            controlLanguagePath, sp.GetRequiredService<ILogger<ControlLanguageCatalog>>()));

        // Auditor Virtual — construtor do CONTEXTO tenant-scoped (somente leitura) que fundamenta o chat:
        // registros dos assessments KNIGHT/NIST, evidência documental curta e integrações. Scoped: usa o
        // DbContext sob o Global Query Filter fail-closed do tenant.
        services.AddScoped<IAuditorContextBuilder, AuditorContextBuilder>();
        // [AEGIS-AUDITOR-CONTEXT-01] Registros dos assessments (KNIGHT, NIST, documentos, inventário, publicações) para o foco da
        // conversa, e a memória da conversa por tenant + conta.
        services.AddScoped<IAuditorAssessmentContextBuilder, AuditorAssessmentContextBuilder>();
        services.AddScoped<IAuditorConversationStore, AuditorConversationStore>();

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
        // [AEGIS-KNIGHT-CLOSURE-01] Resultado manual estruturado dos controles de referência sem avaliação automatizada.
        services.AddScoped<AegisScore.Application.Knight.Reference.IKnightManualResultService, KnightManualResultService>();
        // [AEGIS-NIST-JOURNEY-01] Jornada persistida do AEGIS NIST (avaliação, escopo, subcategoria, evidência, perfil).
        services.AddScoped<AegisScore.Application.Nist.INistAssessmentService, AegisScore.Infrastructure.Nist.NistAssessmentService>();
        // [AEGIS-NIST-JOURNEY-02] Procedimentos, achados e planos; publicação da maturidade; importação CSV de trabalho.
        services.AddScoped<AegisScore.Application.Nist.INistWorkService, AegisScore.Infrastructure.Nist.NistWorkService>();
        services.AddScoped<AegisScore.Application.Nist.INistPublicationService, AegisScore.Infrastructure.Nist.NistPublicationService>();
        services.AddScoped<AegisScore.Application.Nist.INistImportService, AegisScore.Infrastructure.Nist.NistImportService>();
        // [AEGIS-NIST-AI-ASSIST-01] Assistência contextual de IA da jornada NIST — sobre o IAiAssessmentService roteado pelo gate
        // existente (nenhum provedor, chatbot ou camada paralela). Uma geração por item por vez na réplica.
        services.AddSingleton<AegisScore.Infrastructure.Nist.NistAssistInFlight>();
        services.AddScoped<AegisScore.Application.Nist.INistAssistService, AegisScore.Infrastructure.Nist.NistAssistService>();
        // [AEGIS-AUDITOR-CONTEXT-01] Evidências e correlação KNIGHT × NIST por avaliação · rodada · escopo (leitura, sem IA).
        services.AddScoped<AegisScore.Application.Nist.INistEvidenceOverviewService, AegisScore.Infrastructure.Nist.NistEvidenceOverviewService>();

        // [AEGIS-MVP-EVIDENCE-FABRIC-01] Evidence Fabric de identidade: o ÚNICO ponto de aquisição real do Entra ID
        // (reusa o coletor do KNIGHT + transporte/credencial existentes) e de persistência do snapshot normalizado.
        // KNIGHT (assessment) e a rota de postura NIST convergem para cá — uma aquisição por operação lógica.
        services.AddScoped<IIdentityEvidenceService, IdentityEvidenceService>();

        // [AEGIS-ADM-01] ADM de identidade: reconciliação da aquisição, das entidades canônicas e dos vínculos
        // de origem. Scoped e sobre o MESMO DbContext do serviço de evidência — é o que permite que a aquisição
        // e o snapshot agregado entrem no mesmo SaveChanges, sem avaliação sustentada por registro inexistente.
        services.AddScoped<IIdentityAcquisitionStore, IdentityAcquisitionStore>();

        // [AEGIS-ADM-02] Histórico mensal e retenção operacional do ADM. A MANUTENÇÃO é scoped porque depende
        // do DbContextOptions (scoped por AddDbContext) — ela constrói os contextos à mão, tenant a tenant,
        // como os workers, e por isso NÃO recebe o DbContext da requisição. A leitura da série é scoped como
        // qualquer query tenant-scoped. A REMOÇÃO automática nasce desligada nas opções: expurgo tem de ser
        // uma decisão declarada, nunca efeito colateral de subir a aplicação.
        services.Configure<IdentityAdmMaintenanceOptions>(
            config.GetSection(IdentityAdmMaintenanceOptions.SectionName));
        services.AddScoped<IIdentityAdmMaintenanceService, IdentityAdmMaintenanceService>();
        services.AddScoped<IIdentityHistoryQuery, IdentityHistoryQuery>();

        // [AEGIS-AUD-035/036/037] Fotografia AUDITÁVEL de postura — publicação controlada do KNIGHT (o servidor
        // constrói pela autoridade knight-score-v1), leitura por tenant e comparação compatível. As fotografias
        // históricas aegis-score-v1 continuam legíveis e exportáveis, mas a API não publica novas. Scoped: usa o
        // DbContext (Global Query Filter + stamping fail-closed). A fotografia é APPEND-ONLY.
        // O resolvedor de mapeamentos é dependência do serviço (proveniência das fotografias históricas).
        services.AddScoped<INistSignalMapper, NistSignalMapper>();
        services.AddScoped<IPostureSnapshotService, PostureSnapshotService>();

        // [AEGIS-AUD-034] Exportação executiva da fotografia (PDF/CSV) — abstração pequena e focada. Carrega a
        // fotografia pelo Global Query Filter fail-closed, reverifica o ContentHash e renderiza. Somente leitura.
        services.AddScoped<IPostureSnapshotExporter, PostureSnapshotExporter>();

        // [AEGIS-MVP-PRODUCT-03] Jornada de remediação de um achado do KNIGHT (criar ação → responsável/prazo →
        // execução → validação com evidência). Scoped: usa o DbContext (Global Query Filter + stamping
        // fail-closed). NÃO escreve score, veredito, cobertura, ledger nem fotografia — concluir uma ação não
        // torna um achado conforme.
        services.AddScoped<IRemediationService, RemediationService>();

        // Inventário e proveniência como evidência organizacional do AEGIS NIST.
        services.AddScoped<IAssetSourceQuery, AssetSourceQuery>();
        services.TryAddSingleton(TimeProvider.System);

        // Motor consultivo: handler de criação de advisories por subcategoria NIST (escrita). Scoped: usa o
        // DbContext (stamping fail-closed do tenant) + o IAiAssessmentService para redigir o texto.
        services.AddScoped<IGenerateAdvisoryHandler, GenerateAdvisoryHandler>();

        // Teste de acesso das fontes KNIGHT registradas nos pacotes Microsoft/Google.
        services.AddScoped<IConnectorRegistry, ConnectorRegistry>();

        // Govern → Provider Pattern de ingestão de documentos: a fábrica resolve a estratégia de fonte
        // (SharePoint, Google Workspace…) por ConnectorProvider. Os providers concretos são registrados
        // nos pacotes de conector (ex.: AddMicrosoftConnectors → SharePointProvider); adicionar uma fonte
        // nova não toca aqui. Singleton sobre providers singletons — mesmo idioma do IConnectorRegistry.
        services.AddSingleton<IDocumentIntegrationFactory, DocumentIntegrationFactory>();

        // [AEGIS-MVP-PRODUCT-01] Gate do modo demonstrativo da ingestão documental. DESLIGADO por padrão: sem
        // esta chave, um provedor que se declara simulado não é resolvível pela fábrica e a sincronização
        // corporativa responde honestamente que não está disponível. A configuração é da INSTÂNCIA, não do
        // tenant — não existe caminho pelo qual um cliente ligue simulação para si.
        services.Configure<DocumentIntegrationOptions>(config.GetSection(DocumentIntegrationOptions.SectionName));

        // Cálculos puros (stateless): consolidado de maturidade do AEGIS NIST e risco.
        services.AddSingleton<MaturityScoringService>();
        services.AddSingleton<RiskScoringService>();

        // Identify (ID.RA) — Raio de Explosão: motor PURO (stateless, como os *ScoringService acima) +
        // orquestrador scoped que carrega o grafo do tenant, chama o motor e persiste o snapshot.
        services.AddSingleton<IBlastRadiusCalculator, BlastRadiusCalculator>();
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
        // [AEGIS-KNIGHT-MULTICLOUD-01] Sincronização do KNIGHT iniciada em Integrações: mesma família de fila.
        services.Configure<KnightSyncOptions>(config.GetSection(KnightSyncOptions.SectionName));
        services.AddSingleton<IKnightSyncQueue, DurableKnightSyncQueue>();
        services.AddScoped<IKnightSyncRequests, KnightSyncRequests>();

        return services;
    }
}
