using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using Microsoft.EntityFrameworkCore;

namespace AegisScore.Infrastructure.Knight;

/// <summary>
/// [AEGIS-KNIGHT-PERSISTENCE-01] Converte a falha de gravação da avaliação KNIGHT num diagnóstico SEGURO: SQLSTATE do banco e,
/// quando a causa é tamanho (22001), quais propriedades excederam o limite — só nomes e tamanhos, nunca o valor.
///
/// O limite vem do BANCO (information_schema), não só do modelo: a falha que importa é justamente a de um schema que não
/// acompanha o modelo (migration pendente), e aí o modelo diria "text" para a coluna que o banco ainda limita. A mensagem do
/// PostgreSQL diz o tipo excedido, mas não a coluna; comparar com o limite real é o que aponta a propriedade sem chute.
/// </summary>
internal static class KnightPersistenceDiagnostics
{
    /// <summary>SQLSTATE do PostgreSQL para "valor longo demais para o tipo".</summary>
    private const string StringDataRightTruncation = "22001";

    /// <summary>Até quantos campos o diagnóstico enumera.</summary>
    private const int MaxFields = 10;

    public static async Task<KnightPersistenceException> FromAsync(
        DbUpdateException ex, DbContext db, KnightSourceType source, string stage)
    {
        var sqlState = (ex.InnerException as DbException)?.SqlState;
        IReadOnlyList<KnightPersistenceField> fields = Array.Empty<KnightPersistenceField>();
        if (sqlState == StringDataRightTruncation)
        {
            try
            {
                fields = Oversized(db, await ColumnLimitsAsync(db));
            }
            catch (Exception)
            {
                // Diagnóstico é melhor esforço: sem o limite do banco, cai para o do modelo. Nunca troca a falha original.
                fields = Oversized(db, new Dictionary<(string, string), int>());
            }
        }
        return new KnightPersistenceException(source, stage, sqlState, fields, ex);
    }

    private static IReadOnlyList<KnightPersistenceField> Oversized(DbContext db, IReadOnlyDictionary<(string Table, string Column), int> dbLimits)
    {
        var fields = new List<KnightPersistenceField>();
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            var table = entry.Metadata.GetTableName();
            var indicator = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "IndicatorId")?.CurrentValue as string;
            foreach (var p in entry.Properties)
            {
                if (p.CurrentValue is not string value) continue;
                var limit = table is not null && dbLimits.TryGetValue((table, p.Metadata.GetColumnName()), out var real)
                    ? real
                    : p.Metadata.GetMaxLength();
                if (limit is { } max && value.Length > max)
                    fields.Add(new KnightPersistenceField(entry.Metadata.ClrType.Name, p.Metadata.Name, indicator, value.Length, max));
                if (fields.Count == MaxFields) return fields;
            }
        }
        return fields;
    }

    /// <summary>
    /// Limites REAIS das colunas de texto das tabelas envolvidas. Roda depois do rollback (uma transação abortada no
    /// PostgreSQL recusa qualquer comando), na mesma conexão; nomes de tabela vão como parâmetros.
    /// </summary>
    private static async Task<IReadOnlyDictionary<(string, string), int>> ColumnLimitsAsync(DbContext db)
    {
        if (db.Database.CurrentTransaction is { } tx) await tx.RollbackAsync(CancellationToken.None);

        var tables = db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Metadata.GetTableName()).OfType<string>().Distinct().ToList();
        var limits = new Dictionary<(string, string), int>();
        if (tables.Count == 0) return limits;

        await db.Database.OpenConnectionAsync(CancellationToken.None);
        try
        {
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            var names = new List<string>();
            for (var i = 0; i < tables.Count; i++)
            {
                var param = cmd.CreateParameter();
                param.ParameterName = "@t" + i;
                param.Value = tables[i];
                cmd.Parameters.Add(param);
                names.Add(param.ParameterName);
            }
            cmd.CommandText = "SELECT table_name, column_name, character_maximum_length FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND character_maximum_length IS NOT NULL "
                + $"AND table_name IN ({string.Join(", ", names)})";
            await using var reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
                limits[(reader.GetString(0), reader.GetString(1))] = Convert.ToInt32(reader.GetValue(2));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
        return limits;
    }
}
