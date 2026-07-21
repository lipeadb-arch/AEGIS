using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AegisScore.Infrastructure.Persistence;

/// <summary>
/// [AEGIS-AUD-053] Contexto DEDICADO do key ring do ASP.NET Core Data Protection.
///
/// Vive separado do <see cref="AegisScoreDbContext"/> de propósito, por dois motivos verificáveis:
///
/// 1. <b>Snapshot preservado.</b> Acrescentar <c>IDataProtectionKeyContext</c> ao contexto do domínio
///    obrigaria a regerar o <c>AegisScoreDbContextModelSnapshot</c>, mudando o <c>ProductVersion</c>
///    de 8.0.6 para 10.0.10 — exatamente o que o AEGIS-TECH-001 preservou de propósito. Aqui o
///    snapshot e as 17 migrations históricas do domínio ficam intocados.
/// 2. <b>Domínio de falha isolado.</b> O key ring é infraestrutura de plataforma. Acoplá-lo ao ciclo
///    de migration do domínio o colocaria sob a corrida de startup ainda aberta no AEGIS-AUD-052.
///
/// ⚠️ NÃO é o argumento "query filter/tenant stamping": <see cref="DataProtectionKey"/> não é
/// <c>ITenantOwned</c>, então nem o stamping (que itera apenas <c>Entries&lt;ITenantOwned&gt;</c>) nem
/// os filtros globais (aplicados entidade a entidade) jamais a alcançariam. Os testes provam isso.
///
/// Compartilha a connection string <c>ConnectionStrings:AegisScore</c> — mesmo banco, tabela de
/// histórico PRÓPRIA (<see cref="MigrationsHistoryTableName"/>), senão os dois contextos passariam a
/// enxergar as migrations um do outro como desconhecidas.
/// </summary>
public sealed class DataProtectionKeyDbContext : DbContext, IDataProtectionKeyContext
{
    /// <summary>
    /// Tabela de histórico exclusiva deste contexto. O padrão do EF é <c>__EFMigrationsHistory</c>;
    /// mantê-lo faria este contexto e o do domínio disputarem o mesmo registro no MESMO banco.
    /// </summary>
    public const string MigrationsHistoryTableName = "__EFMigrationsHistory_DataProtection";

    public DataProtectionKeyDbContext(DbContextOptions<DataProtectionKeyDbContext> options)
        : base(options)
    {
    }

    /// <summary>Key ring gerenciado pelo framework. O AEGIS nunca lê nem escreve nesta tabela à mão.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
}
