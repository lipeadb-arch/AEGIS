using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AegisScore.Infrastructure.DataProtection;

/// <summary>
/// [AEGIS-AUD-053] Verificação de schema do key ring no boot — <b>sem executar DDL</b>.
///
/// A API deliberadamente NÃO cria a tabela do key ring. Enquanto o AEGIS-AUD-052 não retirar as
/// migrations da inicialização concorrente, acrescentar um segundo <c>MigrateAsync()</c> aqui apenas
/// dobraria a superfície da corrida entre réplicas. A migration do
/// <see cref="DataProtectionKeyDbContext"/> é aplicada por etapa própria de implantação; este guard
/// apenas CONSTATA o resultado e decide se o serviço pode subir.
///
/// Production falha rápido: sem a tabela, o key ring não persiste, e cada réplica passaria a cifrar
/// com chaves próprias e efêmeras — corrompendo em silêncio os segredos de conector.
/// </summary>
public static class DataProtectionSchemaGuard
{
    /// <summary>
    /// Confere se o schema do key ring está aplicado. Em Production, lança
    /// <see cref="InvalidOperationException"/>; fora dela, registra aviso e deixa o serviço subir para
    /// não travar o loop de desenvolvimento.
    /// </summary>
    /// <returns><c>true</c> se o schema está em dia.</returns>
    public static async Task<bool> EnsureAppliedAsync(
        DataProtectionKeyDbContext db,
        bool isProduction,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);

        // Sem tabela de histórico, o EF devolve TODAS as migrations como pendentes — que é exatamente
        // o desfecho desejado quando o banco ainda não recebeu a etapa de implantação.
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation(
                "Data Protection: schema do key ring verificado e em dia ({Table}).",
                DataProtectionKeyDbContext.MigrationsHistoryTableName);
            return true;
        }

        var message =
            $"Data Protection: o schema do key ring NÃO está aplicado (migrations pendentes: " +
            $"{string.Join(", ", pending)}). A API não cria esta tabela por decisão de projeto. " +
            "Aplique na etapa de implantação, fora do processo da API: " +
            "dotnet ef database update --context DataProtectionKeyDbContext " +
            "--project backend/src/AegisScore.Infrastructure --startup-project backend/src/AegisScore.Api";

        if (isProduction)
            throw new InvalidOperationException(message);

        logger.LogWarning("{Message}", message);
        return false;
    }
}
