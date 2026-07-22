using AegisScore.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AegisScore.Infrastructure.DataProtection;

/// <summary>
/// [AEGIS-AUD-053] Composição do Data Protection: discriminator estável, key ring persistido no
/// PostgreSQL compartilhado e envelope das chaves em repouso.
///
/// Substitui o antigo <c>AddDataProtection()</c> nu, cujo comportamento padrão dependia do ambiente:
/// key ring local à instância (ou efêmero, em host sem perfil gravável) e chaves sem envelope fora do
/// Windows. Isso inviabilizava scale-out e tornava o ciphertext dos conectores não portável.
/// </summary>
public static class DataProtectionServiceCollectionExtensions
{
    /// <summary>
    /// Registra o Data Protection do AEGIS. Falha no boot (fail-fast) diante de qualquer configuração
    /// que produziria proteção silenciosamente frágil — ver <see cref="DataProtectionPlan.Resolve"/>.
    /// </summary>
    public static IServiceCollection AddAegisDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = configuration.GetSection(DataProtectionOptions.SectionName)
            .Get<DataProtectionOptions>() ?? new DataProtectionOptions();

        // Opções tipadas também ficam disponíveis por IOptions, para diagnóstico e testes de borda.
        services.Configure<DataProtectionOptions>(
            configuration.GetSection(DataProtectionOptions.SectionName));

        var plan = DataProtectionPlan.Resolve(
            options, environment.EnvironmentName, environment.IsProduction());

        var builder = services.AddDataProtection().SetApplicationName(plan.ApplicationName);

        if (plan.PersistToDbContext)
        {
            var connectionString = configuration.GetConnectionString("AegisScore");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException(
                    "ConnectionStrings:AegisScore é obrigatória para persistir o key ring do Data " +
                    "Protection. O contexto dedicado reutiliza a MESMA connection string do domínio.");

            // Contexto DEDICADO, com tabela de histórico própria: as migrations do domínio e as do key
            // ring convivem no mesmo banco sem se invalidarem mutuamente.
            services.AddDbContext<DataProtectionKeyDbContext>(o =>
                o.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable(DataProtectionKeyDbContext.MigrationsHistoryTableName)));

            builder.PersistKeysToDbContext<DataProtectionKeyDbContext>();
        }

        if (plan.Certificate is not null)
            builder.ProtectKeysWithCertificate(plan.Certificate);
        else if (plan.UseDpapi && OperatingSystem.IsWindows())
            builder.ProtectKeysWithDpapi();

        return services;
    }
}
