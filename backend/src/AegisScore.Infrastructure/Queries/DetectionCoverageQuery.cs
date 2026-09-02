using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Queries;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Autoridade ÚNICA de leitura da cobertura de detecção do tenant ambiente, sobre o
/// AegisScoreDbContext. Somente leitura, isolada pelo Global Query Filter (fail-closed): sem tenant, devolve
/// "não configurado". Resolve nome/hierarquia/táticas das técnicas pelo catálogo MITRE FIXADO (v17.1) — nunca por
/// IA — e NUNCA expõe configuração da integração, credencial, nome/texto de regra ou payload do fornecedor. O
/// conjunto por tenant é pequeno (técnicas agregadas), então a ordenação determinística em memória é barata e
/// portável (evita divergência de ORDER BY entre PostgreSQL e SQLite).
/// </summary>
public sealed class DetectionCoverageQuery : IDetectionCoverageQuery
{
    private const string ScoreDisclaimer =
        "As regras configuradas ajudam a enxergar a capacidade de detecção, mas não comprovam eficácia e não " +
        "alteram o AEGIS Score.";

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IMitreAttackCatalog _mitre;

    public DetectionCoverageQuery(AegisScoreDbContext db, ITenantContext tenant, IMitreAttackCatalog mitre)
    {
        _db = db;
        _tenant = tenant;
        _mitre = mitre;
    }

    public async Task<DetectionCoverageViewDto> GetAsync(CancellationToken ct = default)
    {
        // Fail-closed: sem tenant ambiente, nada é projetado.
        if (_tenant.TenantId is null)
            return Empty(DetectionCoverageViewState.NotConfigured);

        // Snapshot atual (Global Query Filter fail-closed). Materializa (bounded) e escolhe o mais recente em
        // memória — evita ORDER BY de DateTimeOffset no provedor (SQLite não o traduz de forma consistente).
        var snapshots = await _db.DetectionCoverageSnapshots.AsNoTracking()
            .Include(s => s.Techniques)
            .ToListAsync(ct);
        var snapshot = snapshots
            .OrderByDescending(s => s.LastAttemptAt)
            .FirstOrDefault();

        if (snapshot is null)
        {
            // Sem snapshot: distingue "nunca sincronizado" (há conector de SIEM) de "não configurado" (não há).
            var hasSiemConnector = await _db.Connectors.AsNoTracking()
                .AnyAsync(c => c.Capability == ConnectorCapability.Siem, ct);
            return Empty(hasSiemConnector
                ? DetectionCoverageViewState.NeverSynced
                : DetectionCoverageViewState.NotConfigured);
        }

        var hasStoredData = snapshot.CollectionState is DetectionCoverageCollectionState.Available
            or DetectionCoverageCollectionState.Partial;

        // Estado da VISÃO: a última tentativa manda; StoredCollectionState informa se há inventário preservado.
        var viewState = !hasStoredData
            ? (snapshot.LastAttemptState == DetectionCoverageCollectionState.Unavailable
                ? DetectionCoverageViewState.Unavailable
                : DetectionCoverageViewState.NeverSynced)
            : snapshot.LastAttemptState switch
            {
                DetectionCoverageCollectionState.Unavailable => DetectionCoverageViewState.Unavailable,
                _ when snapshot.CollectionState == DetectionCoverageCollectionState.Partial
                    => DetectionCoverageViewState.Partial,
                _ => DetectionCoverageViewState.Available,
            };

        var techniques = snapshot.Techniques
            .Select(ToTechniqueDto)
            .OrderBy(t => t.AttentionRank)
            .ThenBy(t => t.Dto.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Dto.TechniqueId, StringComparer.Ordinal)
            .Select(t => t.Dto)
            .ToList();

        var needingAttention = techniques.Count(t => t.NeedsAttention);

        var summary = new DetectionCoverageSummaryDto(
            ActiveRules: snapshot.TotalActiveRules,
            RulesWithMitre: snapshot.RulesWithMitre,
            RulesWithoutMitre: snapshot.RulesWithoutMitre,
            RulesInLiveMode: snapshot.RulesInLiveMode,
            RulesInNormalExecution: snapshot.RulesInNormalExecution,
            RulesInLimitedExecution: snapshot.RulesInLimitedExecution,
            RulesInPausedExecution: snapshot.RulesInPausedExecution,
            RulesInUnknownExecution: snapshot.RulesInUnknownExecution,
            RulesWithAlerting: snapshot.RulesWithAlerting,
            TechniquesObserved: snapshot.Techniques.Count,
            TechniquesNeedingAttention: needingAttention);

        var attackVersion = string.IsNullOrWhiteSpace(snapshot.AttackVersion) ? _mitre.AttackVersion : snapshot.AttackVersion;

        return new DetectionCoverageViewDto(
            State: viewState,
            Source: snapshot.Source,
            AttackVersion: attackVersion,
            AttackLabel: _mitre.DisplayLabel,
            StoredCollectionState: hasStoredData ? snapshot.CollectionState.ToString() : null,
            LastAttemptState: snapshot.LastAttemptState.ToString(),
            LastCollectionAt: snapshot.LastCollectionAt,
            LastAttemptAt: snapshot.LastAttemptAt,
            Summary: summary,
            Techniques: techniques,
            AffectsScore: false,
            ScoreDisclaimer: ScoreDisclaimer);
    }

    private (DetectionCoverageTechniqueDto Dto, int AttentionRank) ToTechniqueDto(DetectionCoverageTechnique t)
    {
        var mitre = _mitre.GetTechnique(t.TechniqueId);
        var name = mitre?.Name ?? t.TechniqueId;   // fallback honesto: o ID persistido já foi validado no catálogo
        var isSub = mitre?.IsSubtechnique ?? t.TechniqueId.Contains('.');
        var parent = mitre?.ParentId
            ?? (t.TechniqueId.Contains('.') ? t.TechniqueId.Split('.')[0] : null);

        var tactics = (mitre?.TacticIds ?? Array.Empty<string>())
            .Select(id => _mitre.GetTactic(id))
            .Where(x => x is not null)
            .Select(x => new DetectionCoverageTacticDto(x!.Id, x.NamePt))
            .ToList();

        // Estado HONESTO derivado das TRÊS dimensões separadas. Uma técnica só aparece como "em execução (normal)"
        // se houver ≥1 regra NÃO arquivada (já garantido — arquivadas não entram), em live mode E com
        // executionState=DEFAULT (NormalExecutionRuleCount>0). live mode com LIMITED/PAUSED/desconhecido NÃO é
        // execução saudável. Nunca afirma que alertas foram produzidos — só se o alerting está habilitado.
        var (statusLabel, attentionRank) = DeriveStatus(t);

        var dto = new DetectionCoverageTechniqueDto(
            t.TechniqueId, name, isSub, parent, tactics,
            t.RuleCount, t.LiveRuleCount,
            t.NormalExecutionRuleCount, t.LimitedExecutionRuleCount,
            t.PausedExecutionRuleCount, t.UnknownExecutionRuleCount,
            t.AlertingRuleCount,
            statusLabel, NeedsAttention: attentionRank < 3);
        return (dto, attentionRank);
    }

    /// <summary>
    /// Rótulo HONESTO + rank de atenção (menor = mais atenção, ordena primeiro) a partir das contagens agregadas.
    /// Rank: 0 = nenhuma regra em live mode; 1 = em live mode mas nenhuma em execução normal (só limitadas/pausadas/
    /// desconhecidas); 2 = em execução normal, sem alerting habilitado; 3 = em execução normal e configurada p/ alertas.
    /// </summary>
    private static (string Label, int Rank) DeriveStatus(DetectionCoverageTechnique t)
    {
        if (t.LiveRuleCount == 0)
            return ("Live mode desabilitado", 0);

        if (t.NormalExecutionRuleCount > 0)
            return t.AlertingRuleCount > 0
                ? ("Em execução e configurada para alertas", 3)
                : ("Em execução; alertas não habilitados", 2);

        // Em live mode, porém NENHUMA em execução normal (todas limitadas/pausadas/desconhecidas).
        if (t.LimitedExecutionRuleCount > 0 || t.PausedExecutionRuleCount > 0)
            return ("Execução parcial: há regras limitadas ou pausadas", 1);
        return ("Estado de execução desconhecido", 1);
    }

    private DetectionCoverageViewDto Empty(DetectionCoverageViewState state) => new(
        State: state,
        Source: null,
        AttackVersion: _mitre.AttackVersion,
        AttackLabel: _mitre.DisplayLabel,
        StoredCollectionState: null,
        LastAttemptState: DetectionCoverageCollectionState.NeverCollected.ToString(),
        LastCollectionAt: null,
        LastAttemptAt: null,
        Summary: new DetectionCoverageSummaryDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        Techniques: Array.Empty<DetectionCoverageTechniqueDto>(),
        AffectsScore: false,
        ScoreDisclaimer: ScoreDisclaimer);
}
