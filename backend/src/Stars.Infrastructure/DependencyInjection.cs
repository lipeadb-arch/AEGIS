using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stars.Application.Abstractions;
using Stars.Application.Scoring;
using Stars.Infrastructure.Ai;
using Stars.Infrastructure.Connectors;
using Stars.Infrastructure.Persistence;

namespace Stars.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers persistence, the AI engine, the connector registry and scoring services.</summary>
    public static IServiceCollection AddStarsInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<StarsDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Stars")));

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
