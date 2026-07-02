using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Scoring;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers persistence, the AI engine, the connector registry and scoring services.</summary>
    public static IServiceCollection AddAegisScoreInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AegisScoreDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("AegisScore")));

        // AI engine (swappable). Bound from the "Ai" config section.
        services.Configure<AiOptions>(config.GetSection("Ai"));
        services.AddHttpClient<IAiAssessmentService, ClaudeAssessmentService>();

        // Connector registry resolves every IEvidenceConnector registered in DI.
        services.AddSingleton<IConnectorRegistry, ConnectorRegistry>();

        // Pure scoring logic (stateless).
        services.AddSingleton<MaturityScoringService>();
        services.AddSingleton<RiskScoringService>();
        services.AddSingleton<IcrScoringService>();

        return services;
    }
}
