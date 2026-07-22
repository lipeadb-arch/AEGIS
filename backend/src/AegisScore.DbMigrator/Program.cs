using AegisScore.Application.Abstractions;
using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AegisScore.DbMigrator;

/// <summary>
/// [AEGIS-AUD-052] Prepara o banco antes de a API subir.
///
/// Este processo é o ÚNICO autorizado a aplicar migrations e semear dados obrigatórios. A API passou a
/// apenas verificar (<see cref="SchemaReadinessGuard"/>). Antes, toda réplica migrava e semeava no
/// boot: o seed rodava fora de qualquer lock, e duas réplicas simultâneas podiam inserir dois
/// catálogos completos, quebrando o boot de forma permanente.
///
/// Console puro: sem servidor HTTP, sem hosted services, sem controllers. A composição de serviços é
/// montada à mão, com apenas o necessário — não há host genérico que possa iniciar algo por engano.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = MigratorOptions.Parse(args);

        if (options.ShowHelp)
        {
            Console.WriteLine(MigratorOptions.HelpText);
            return MigratorExitCode.Success;
        }

        if (options.Error is not null)
        {
            Console.Error.WriteLine($"Configuração inválida: {options.Error}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(MigratorOptions.HelpText);
            return MigratorExitCode.InvalidConfiguration;
        }

        using var loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            // O EF em Information despeja cada comando SQL, incluindo o INSERT do catálogo inteiro —
            // milhares de linhas que afogam o desfecho real e poluiriam o log de um job de deploy.
            // Warning preserva o que importa (falhas) sem o ruído.
            .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning)
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));
        var log = loggerFactory.CreateLogger("AegisScore.DbMigrator");

        var environment = options.Environment
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";

        var configuration = BuildConfiguration(environment);

        var connectionString = configuration.GetConnectionString("AegisScore");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            log.LogError(
                "ConnectionStrings:AegisScore não está configurada para o ambiente '{Environment}'. " +
                "Defina por variável de ambiente, user-secrets ou secret manager — nunca por argumento.",
                environment);
            return MigratorExitCode.InvalidConfiguration;
        }

        // Só metadados não sensíveis vão para o log. A connection string nunca é registrada.
        string database, host;
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            database = builder.Database ?? "(não informado)";
            host = builder.Host ?? "(não informado)";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ConnectionStrings:AegisScore está malformada.");
            return MigratorExitCode.InvalidConfiguration;
        }

        log.LogInformation(
            "AegisScore.DbMigrator | ambiente={Environment} | host={Host} | database={Database} | " +
            "modo={Mode}", environment, host, database,
            options.VerifyOnly ? "verify-only" : options.SkipSeed ? "migrate+verify" : "migrate+seed+verify");

        await using var services = BuildServices(connectionString, loggerFactory);

        // Conexão DEDICADA do lock: precisa continuar aberta durante toda a sequência, então não pode
        // sair do pool que o EF usa para migrar e semear.
        await using var lockConnection = new NpgsqlConnection(connectionString);
        try
        {
            await lockConnection.OpenAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
        {
            log.LogError(ex, "Banco inacessível em {Host}/{Database}.", host, database);
            return MigratorExitCode.DatabaseUnreachable;
        }

        var advisoryLock = await PostgresAdvisoryLock.TryAcquireAsync(
            lockConnection, TimeSpan.FromSeconds(options.LockTimeoutSeconds), log);

        if (advisoryLock is null)
            return MigratorExitCode.LockNotAcquired;

        try
        {
            return await RunSequenceAsync(services, configuration, options, log);
        }
        finally
        {
            // Liberação garantida mesmo em exceção: o lock nunca sobrevive ao processo.
            await advisoryLock.DisposeAsync();
        }
    }

    /// <summary>
    /// Sequência: migrations → seed → verificação. Cada falha interrompe as etapas seguintes — não
    /// existe sucesso parcial.
    /// </summary>
    private static async Task<int> RunSequenceAsync(
        ServiceProvider services, IConfiguration configuration, MigratorOptions options, ILogger log)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisScoreDbContext>();
        var keyRing = scope.ServiceProvider.GetRequiredService<DataProtectionKeyDbContext>();

        if (!options.VerifyOnly)
        {
            try
            {
                // MigrateAsync NÃO pode ser envolvido em transação explícita (limitação documentada do
                // EF Core 9+); a segurança vem do advisory lock externo e da idempotência.
                var pendingMain = (await db.Database.GetPendingMigrationsAsync()).ToList();
                log.LogInformation("AegisScoreDbContext: {Count} migration(s) pendente(s).", pendingMain.Count);
                await db.Database.MigrateAsync();

                var pendingKeyRing = (await keyRing.Database.GetPendingMigrationsAsync()).ToList();
                log.LogInformation("DataProtectionKeyDbContext: {Count} migration(s) pendente(s).", pendingKeyRing.Count);
                await keyRing.Database.MigrateAsync();
            }
            catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
            {
                log.LogError(ex, "Banco ficou inacessível durante as migrations.");
                return MigratorExitCode.DatabaseUnreachable;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Falha ao aplicar migrations.");
                return MigratorExitCode.MigrationFailure;
            }

            if (!options.SkipSeed)
            {
                try
                {
                    var catalogPath = ResolveDataPath(configuration, "Seed:CatalogPath", "nist_csf_2_0_catalog.json");
                    await FrameworkSeeder.SeedAsync(db, catalogPath);
                    log.LogInformation("Catálogo NIST CSF 2.0 verificado/semeado.");

                    var rulesPath = ResolveDataPath(configuration, "Seed:RulesPath", "aegis_assessment_rules.json");
                    await FrameworkSeeder.SeedAssessmentRulesAsync(db, rulesPath);
                    log.LogInformation("Regras de avaliação verificadas/semeadas.");
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Falha ao semear catálogo ou regras.");
                    return MigratorExitCode.SeedFailure;
                }
            }
            else
            {
                log.LogWarning("--skip-seed: catálogo e regras NÃO foram semeados nesta execução.");
            }
        }

        // Mesma verificação que a API executa no boot — o migrator não aprova o que a API recusaria.
        SchemaReadinessResult verification;
        try
        {
            verification = await SchemaReadinessGuard.CheckAsync(db, keyRing);
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
        {
            log.LogError(ex, "Banco ficou inacessível durante a verificação final.");
            return MigratorExitCode.DatabaseUnreachable;
        }

        if (!verification.IsReady)
        {
            log.LogError("Verificação final REPROVADA: {Problems}", verification.Describe());
            return MigratorExitCode.VerificationFailure;
        }

        log.LogInformation("Verificação final aprovada. Banco pronto para a API.");
        return MigratorExitCode.Success;
    }

    private static IConfiguration BuildConfiguration(string environment)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true);

        // User-secrets apenas em Development, com o MESMO UserSecretsId da API: o operador não precisa
        // duplicar a connection string em outro cofre para rodar o migrator localmente.
        if (string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
            builder.AddUserSecrets(typeof(Program).Assembly, optional: true);

        return builder.AddEnvironmentVariables().Build();
    }

    private static ServiceProvider BuildServices(string connectionString, ILoggerFactory loggerFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddLogging();

        // Reference data (catálogo, regras) não é ITenantOwned: não sofre query filter nem stamping.
        // Ainda assim o DbContext exige um ITenantContext no construtor, e o migrator opera fora de
        // qualquer request HTTP — daí o contexto de sistema, sem tenant.
        services.AddSingleton<ITenantContext>(new SystemTenantContext(null));

        services.AddDbContext<AegisScoreDbContext>(o => o.UseNpgsql(connectionString));
        services.AddDbContext<DataProtectionKeyDbContext>(o =>
            o.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(DataProtectionKeyDbContext.MigrationsHistoryTableName)));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Resolve o caminho de um JSON de seed. Mesmo contrato de configuração da API
    /// (<c>Seed:CatalogPath</c>/<c>Seed:RulesPath</c>); por padrão, o <c>Data/</c> ao lado do binário,
    /// para onde os arquivos são linkados no build e no publish.
    /// </summary>
    private static string ResolveDataPath(IConfiguration configuration, string key, string fileName)
    {
        var configured = configuration[key];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return Path.Combine(AppContext.BaseDirectory, "Data", fileName);
    }
}
