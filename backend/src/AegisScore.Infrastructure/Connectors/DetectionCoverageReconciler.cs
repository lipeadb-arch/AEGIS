using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AppCoverage = AegisScore.Application.Abstractions.DetectionCoverageSnapshot;

namespace AegisScore.Infrastructure.Connectors;

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Reconcilia a fotografia PROVIDER-NEUTRAL de cobertura de detecção no snapshot
/// persistido por (tenant, conector), sob o tenant proprietário (contexto NOVO). Regras de substituição HONESTAS:
///  • coleta COMPLETA → substitui ATOMICAMENTE o snapshot anterior (numa transação: apaga o antigo, cascata nos
///    filhos, e insere o novo — sem colisão do índice único (tenant, conector) nem por técnica);
///  • falha TOTAL (Unavailable) → PRESERVA o último snapshot e só registra a tentativa falha; sem snapshot anterior,
///    grava um placeholder honesto (nada coletado ainda), nunca fingindo inventário;
///  • parcial que rebaixaria um snapshot COMPLETO → preserva o completo e só registra a tentativa parcial;
///  • primeira coleta parcial (sem completo anterior) → grava o piso, marcado Partial (nunca "completo");
///  • fingerprint idêntico + mesmo estado → só atualiza a recência (impede write desnecessário dos filhos).
///
/// NUNCA cria EvidenceSignal, NUNCA toca TenantControlState/score/NIST, NUNCA persiste nome/texto/conteúdo de regra.
/// </summary>
public sealed class DetectionCoverageReconciler
{
    private readonly AegisScoreDbContext _db;
    private readonly ILogger? _log;

    public DetectionCoverageReconciler(AegisScoreDbContext db, ILogger? log = null)
    {
        _db = db;
        _log = log;
    }

    public async Task ReconcileAsync(Guid connectorId, AppCoverage incoming, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await _db.DetectionCoverageSnapshots
            .Include(s => s.Techniques)
            .FirstOrDefaultAsync(s => s.ConnectorConfigId == connectorId, ct);

        // (1) Falha TOTAL: nunca sobrescreve os dados — só registra a tentativa falha.
        if (incoming.State == DetectionCoverageCollectionState.Unavailable)
        {
            if (existing is null)
            {
                _db.DetectionCoverageSnapshots.Add(new DetectionCoverageSnapshot
                {
                    ConnectorConfigId = connectorId,
                    Source = incoming.Source,
                    AttackVersion = incoming.AttackVersion,
                    CollectionState = DetectionCoverageCollectionState.NeverCollected,
                    LastAttemptState = DetectionCoverageCollectionState.Unavailable,
                    LastAttemptAt = now,
                    LastCollectionAt = null,
                    Fingerprint = "",
                });
            }
            else
            {
                existing.Source = incoming.Source;
                existing.LastAttemptState = DetectionCoverageCollectionState.Unavailable;
                existing.LastAttemptAt = now;
                // CollectionState / totais / técnicas / LastCollectionAt / Fingerprint PRESERVADOS.
            }
            await _db.SaveChangesAsync(ct);
            _log?.LogInformation("Cobertura de detecção do conector {ConnectorId}: tentativa indisponível registrada (snapshot preservado).", connectorId);
            return;
        }

        var newFingerprint = Fingerprint(incoming);
        var newState = incoming.State;   // Available ou Partial (Unavailable já tratado acima)

        // (2) Parcial que rebaixaria um COMPLETO → preserva o completo, registra a tentativa parcial.
        if (newState == DetectionCoverageCollectionState.Partial
            && existing is not null
            && existing.CollectionState == DetectionCoverageCollectionState.Available)
        {
            existing.Source = incoming.Source;
            existing.LastAttemptState = DetectionCoverageCollectionState.Partial;
            existing.LastAttemptAt = now;
            await _db.SaveChangesAsync(ct);
            _log?.LogInformation("Cobertura de detecção do conector {ConnectorId}: coleta parcial preservou o snapshot completo anterior.", connectorId);
            return;
        }

        // (3) Fingerprint idêntico + mesmo estado → só atualiza a recência (nada mudou nos dados).
        if (existing is not null
            && existing.CollectionState == newState
            && string.Equals(existing.Fingerprint, newFingerprint, StringComparison.Ordinal))
        {
            existing.LastAttemptState = newState;
            existing.LastAttemptAt = now;
            existing.LastCollectionAt = now;
            await _db.SaveChangesAsync(ct);
            return;
        }

        // (4) Substituição ATÔMICA: numa transação, apaga o snapshot anterior (cascata nos filhos) e insere o novo.
        // Duas gravações dentro da mesma transação garantem que o DELETE do índice único (tenant, conector) seja
        // liberado ANTES do INSERT — sem depender da ordem de operações de um único SaveChanges (portátil).
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (existing is not null)
        {
            _db.DetectionCoverageSnapshots.Remove(existing);   // cascade → remove as técnicas filhas
            await _db.SaveChangesAsync(ct);
        }

        var snapshot = new DetectionCoverageSnapshot
        {
            ConnectorConfigId = connectorId,
            Source = incoming.Source,
            AttackVersion = incoming.AttackVersion,
            CollectionState = newState,
            LastAttemptState = newState,
            LastAttemptAt = now,
            LastCollectionAt = now,
            TotalActiveRules = incoming.TotalActiveRules,
            RulesWithMitre = incoming.RulesWithMitre,
            RulesWithoutMitre = incoming.RulesWithoutMitre,
            RulesInLiveMode = incoming.RulesInLiveMode,
            RulesInNormalExecution = incoming.RulesInNormalExecution,
            RulesInLimitedExecution = incoming.RulesInLimitedExecution,
            RulesInPausedExecution = incoming.RulesInPausedExecution,
            RulesInUnknownExecution = incoming.RulesInUnknownExecution,
            RulesWithAlerting = incoming.RulesWithAlerting,
            TechniquesObserved = incoming.Techniques.Count,
            Fingerprint = newFingerprint,
        };
        foreach (var t in incoming.Techniques)
        {
            snapshot.Techniques.Add(new DetectionCoverageTechnique
            {
                TechniqueId = t.TechniqueId,
                RuleCount = t.RuleCount,
                LiveRuleCount = t.LiveRuleCount,
                NormalExecutionRuleCount = t.NormalExecutionRuleCount,
                LimitedExecutionRuleCount = t.LimitedExecutionRuleCount,
                PausedExecutionRuleCount = t.PausedExecutionRuleCount,
                UnknownExecutionRuleCount = t.UnknownExecutionRuleCount,
                AlertingRuleCount = t.AlertingRuleCount,
            });
        }
        _db.DetectionCoverageSnapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
        _log?.LogInformation(
            "Cobertura de detecção do conector {ConnectorId}: snapshot {State} gravado ({Rules} regras ativas, {Techniques} técnicas).",
            connectorId, newState, incoming.TotalActiveRules, incoming.Techniques.Count);
    }

    /// <summary>
    /// Fingerprint SHA-256 dos DADOS (versão + totais + técnicas ordenadas por ID com suas contagens). Independe da
    /// ordem de chegada; impede reescrever os filhos quando nada mudou. NÃO inclui o estado (comparado à parte).
    /// </summary>
    private static string Fingerprint(AppCoverage s)
    {
        var sb = new StringBuilder();
        sb.Append(s.AttackVersion).Append('|')
          .Append(s.TotalActiveRules).Append('|').Append(s.RulesWithMitre).Append('|')
          .Append(s.RulesWithoutMitre).Append('|').Append(s.RulesInLiveMode).Append('|')
          .Append(s.RulesInNormalExecution).Append('|').Append(s.RulesInLimitedExecution).Append('|')
          .Append(s.RulesInPausedExecution).Append('|').Append(s.RulesInUnknownExecution).Append('|')
          .Append(s.RulesWithAlerting).Append('|').Append(s.Techniques.Count).Append(';');
        foreach (var t in s.Techniques.OrderBy(x => x.TechniqueId, StringComparer.Ordinal))
            sb.Append(t.TechniqueId).Append(':')
              .Append(t.RuleCount).Append(',').Append(t.LiveRuleCount).Append(',')
              .Append(t.NormalExecutionRuleCount).Append(',').Append(t.LimitedExecutionRuleCount).Append(',')
              .Append(t.PausedExecutionRuleCount).Append(',').Append(t.UnknownExecutionRuleCount).Append(',')
              .Append(t.AlertingRuleCount).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
