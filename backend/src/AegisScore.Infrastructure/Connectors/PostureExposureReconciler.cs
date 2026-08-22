using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Connectors;

/// <summary>
/// [AEGIS-MVP-POSTURE-02] Reconcilia uma coleta de exposições (<see cref="PostureFindingCollection"/>) no
/// ledger de postura (<see cref="PostureExposureFinding"/>), sob o tenant PROPRIETÁRIO (o
/// <see cref="AegisScoreDbContext"/> recebido já é tenant-scoped: query filter fail-closed + stamping do
/// SaveChanges). O ADAPTADOR nunca escreve no banco — quem persiste é este reconciliador, chamado pelo
/// <see cref="EvidenceIngestionExecutor"/>.
///
/// Invariantes:
///  • chave natural (Tenant, ConnectorConfig, ExternalId) — índice único no banco;
///  • primeira detecção CRIA (FirstSeenAt = agora);
///  • nova coleta ATUALIZA e PRESERVA FirstSeenAt; controle com gap mantém/reabre Open (ResolvedAt = null);
///  • controle antes ABERTO que sumiu do conjunto NUMA COLETA COMPLETA vira Resolved (ResolvedAt = agora) —
///    nunca exclusão silenciosa; coleta incompleta NÃO resolve por omissão;
///  • idempotente: reexecução com os mesmos dados não duplica nem oscila estado.
/// </summary>
public sealed class PostureExposureReconciler
{
    private const string UniqueIndexName = "UX_PostureExposureFinding_Natural";

    private readonly AegisScoreDbContext _db;
    private readonly ILogger? _log;

    public PostureExposureReconciler(AegisScoreDbContext db, ILogger? log = null)
    {
        _db = db;
        _log = log;
    }

    public async Task ReconcileAsync(
        Guid connectorId, PostureFindingCollection collection, CancellationToken ct)
    {
        // Uma passada de recuperação para a corrida de INSERÇÃO concorrente (dois syncs do MESMO conector
        // inserindo o MESMO ExternalId): o perdedor viola o índice único; recarregamos e reaplicamos como UPDATE.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await _db.PostureExposureFindings
                .Where(f => f.ConnectorConfigId == connectorId)
                .ToListAsync(ct);
            var byExternalId = existing
                .GroupBy(f => f.ExternalId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var now = DateTimeOffset.UtcNow;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var f in collection.Findings)
            {
                if (string.IsNullOrWhiteSpace(f.ExternalId)) continue;
                seen.Add(f.ExternalId);

                if (byExternalId.TryGetValue(f.ExternalId, out var row))
                {
                    Apply(row, f);
                    row.LastSeenAt = now;
                    // Visto de novo com gap → mantém/REABRE Open (limpa a resolução anterior).
                    row.LifecycleState = PostureExposureState.Open;
                    row.ResolvedAt = null;
                }
                else
                {
                    var created = new PostureExposureFinding
                    {
                        ConnectorConfigId = connectorId,
                        ExternalId = f.ExternalId,
                        FirstSeenAt = now,
                        LastSeenAt = now,
                        LifecycleState = PostureExposureState.Open,
                        ResolvedAt = null,
                        // TenantId carimbado pelo SaveChanges (fail-closed) contra o tenant do contexto.
                    };
                    Apply(created, f);
                    _db.PostureExposureFindings.Add(created);
                }
            }

            // RESOLUÇÃO só em coleta COMPLETA: um controle antes aberto que não veio nesta coleta deixou de ter
            // gap → Resolved. Coleta incompleta (fotografia ausente/erro parcial) NUNCA resolve por omissão.
            if (collection.IsComplete)
            {
                foreach (var row in existing)
                {
                    if (seen.Contains(row.ExternalId)) continue;
                    if (row.LifecycleState == PostureExposureState.Resolved) continue;   // já resolvido: idempotente
                    row.LifecycleState = PostureExposureState.Resolved;
                    row.ResolvedAt = now;
                    row.LastSeenAt = row.LastSeenAt;   // LastSeenAt preserva a última vez que FOI observado com gap
                }
            }

            try
            {
                await _db.SaveChangesAsync(ct);
                _log?.LogInformation(
                    "Exposições de postura reconciliadas para o conector {ConnectorId}: {Findings} com gap, coleta {Complete}.",
                    connectorId, collection.Findings.Count, collection.IsComplete ? "completa" : "parcial");
                return;
            }
            catch (DbUpdateException ex) when (attempt == 0 && IsNaturalKeyRace(ex))
            {
                // Corrida de inserção concorrente: descarta o tracking e refaz UMA passada — as linhas vencedoras
                // agora existem e viram UPDATE (idempotente). Uma segunda passada basta.
                foreach (var entry in _db.ChangeTracker.Entries<PostureExposureFinding>().ToList())
                    entry.State = EntityState.Detached;
                _log?.LogInformation(
                    "Corrida de inserção nas exposições do conector {ConnectorId} — recarregando e reaplicando.",
                    connectorId);
            }
        }

        throw new InvalidOperationException(
            "Reconciliação de exposições falhou após recuperar a corrida de inserção.");
    }

    /// <summary>Copia os campos NORMALIZADOS do finding coletado para a linha persistida (sem tocar o ciclo de vida).</summary>
    private static void Apply(PostureExposureFinding row, PostureFinding f)
    {
        row.Title = f.Title;
        row.Category = f.Category;
        row.Service = f.Service;
        row.ActionType = f.ActionType;
        row.CurrentScore = f.CurrentScore;
        row.MaxScore = f.MaxScore;
        row.Gap = f.Gap;
        row.SourceRank = f.SourceRank;
        row.Tier = f.Tier;
        row.ImplementationCost = f.ImplementationCost;
        row.UserImpact = f.UserImpact;
        row.Remediation = f.Remediation;
        row.RemediationImpact = f.RemediationImpact;
        row.Threats = f.Threats.ToList();
        row.SourceState = f.SourceState;
    }

    /// <summary>Reconhece SÓ a violação do índice único natural — a corrida de inserção. Outra falha propaga.</summary>
    private static bool IsNaturalKeyRace(DbUpdateException ex)
    {
        if (ex.InnerException is PostgresException pg)
            return pg.SqlState == PostgresErrorCodes.UniqueViolation
                && string.Equals(pg.ConstraintName, UniqueIndexName, StringComparison.Ordinal);

        var inner = ex.InnerException;
        return inner is not null
            && inner.GetType().Name == "SqliteException"
            && inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
            && inner.Message.Contains("PostureExposureFinding", StringComparison.OrdinalIgnoreCase);
    }
}
