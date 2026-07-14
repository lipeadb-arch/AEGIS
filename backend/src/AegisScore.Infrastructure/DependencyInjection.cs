using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Advisories;
using AegisScore.Application.Queries;
using AegisScore.Application.RiskAssessment;
using AegisScore.Application.Scoring;
using AegisScore.Application.Services;
using AegisScore.Infrastructure.Advisories;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Auth;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Documents;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using AegisScore.Infrastructure.RiskAssessment;
using AegisScore.Infrastructure.Scoring;

namespace AegisScore.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers persistence, the AI engine, the connector registry and scoring services.</summary>
    public static IServiceCollection AddAegisScoreInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AegisScoreDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("AegisScore")));

        // Autenticação: JWT de acesso + refresh tokens com rotação (RTR). Opções da seção "Jwt".
        services.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();   // stateless
        services.AddSingleton<IJwtTokenService, JwtTokenService>();       // stateless
        services.AddScoped<IAuthService, AuthService>();                  // usa o DbContext (scoped)

        // [Médio 6/Baixo] Encriptação server-side dos segredos de conector (Data Protection). Depende
        // de IDataProtectionProvider, registrado por AddDataProtection() no composition root (Program).
        services.AddSingleton<IConnectorSecretProtector, ConnectorSecretProtector>();

        // AI engine (swappable). Bound from the "Ai" config section.
        services.Configure<AiOptions>(config.GetSection("Ai"));

        // Fail-open para DEV/demo: sem Ai:ApiKey usa o Stub (respostas canned, sem tokens e sem
        // rede); com a chave presente (ex.: via 'dotnet user-secrets') usa o motor real (Claude).
        if (string.IsNullOrWhiteSpace(config["Ai:ApiKey"]))
            services.AddSingleton<IAiAssessmentService, StubAssessmentService>();
        else
            services.AddHttpClient<IAiAssessmentService, ClaudeAssessmentService>();

        // Aegis Score — avaliador de conformidade por IA: telemetria bruta → veredito NIST CSF 2.0 →
        // upsert do TenantControlState. ILLMClient é o seam de transporte (mockável nos testes).
        services.Configure<AegisAiOptions>(config.GetSection(AegisAiOptions.SectionName));

        // Fail-open (espelha o padrão do IAiAssessmentService acima): sem AegisAi:ApiKey usa o stub
        // determinístico (sem rede nem tokens — a demo nunca quebra por ausência de chave); com a chave
        // presente (via 'dotnet user-secrets') engata o motor real Gemini 1.5 Flash (HttpClient tipado).
        if (string.IsNullOrWhiteSpace(config[$"{AegisAiOptions.SectionName}:ApiKey"]))
            services.AddSingleton<ILLMClient, StubLlmClient>();
        else
            services.AddHttpClient<ILLMClient, GeminiLlmClient>();
        // Escritor ÚNICO do ledger de conformidade (upsert idempotente + regra de scoring). Compartilhado
        // pelo motor de telemetria e pela ponte do Govern — nenhuma das duas fontes reimplementa scoring.
        services.AddScoped<IControlStateWriter, ControlStateWriter>();
        services.AddScoped<IAegisAiEvaluatorService, AegisAiEvaluatorService>();

        // Superfície de ingestão passiva de telemetria (webhook EDR/SIEM) — o CHAMADOR do EvaluateAsync.
        // Orquestração fina: normaliza o sinal, resolve o tenant e delega ao motor (fonte Telemetry).
        services.AddScoped<ITelemetryIngestionService, TelemetryIngestionService>();

        // Aegis Score — consultas de leitura do HUD (Score Atual em tempo real + série temporal + KPI
        // de pendências). Scoped: usam o DbContext e, com ele, o Global Query Filter fail-closed do tenant.
        services.AddScoped<ICurrentScoreQuery, CurrentScoreQuery>();
        services.AddScoped<ITenantScoreTrendQuery, TenantScoreTrendQuery>();
        services.AddScoped<IGetPendingControlsQuery, PendingControlsQuery>();
        services.AddScoped<IControlStateDashboardQuery, ControlStateDashboardQuery>();

        // Aegis Score — motor consultivo: handler de criação de advisories (escrita). Scoped: usa o
        // DbContext (stamping fail-closed do tenant) + o IAiAssessmentService para redigir o texto.
        services.AddScoped<IGenerateAdvisoryHandler, GenerateAdvisoryHandler>();

        // Connector registry resolves every IEvidenceConnector registered in DI.
        services.AddSingleton<IConnectorRegistry, ConnectorRegistry>();

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
        services.AddSingleton<IDocumentAnalysisQueue, ChannelDocumentAnalysisQueue>();

        // Govern: gatilho de sincronização de políticas sob demanda (canal em memória). Singleton para que
        // o controller (produtor) e o PolicyIngestionWorker (consumidor) compartilhem a MESMA instância.
        services.AddSingleton<IPolicySyncTrigger, ChannelPolicySyncTrigger>();

        return services;
    }
}
