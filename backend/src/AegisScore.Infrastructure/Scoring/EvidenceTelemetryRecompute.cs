using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Scoring;

/// <summary>
/// [AEGIS-AUD-019 / AEGIS-MVP-SCORE-GUARD-SIEM-01] Autoridade ÚNICA do recomputo determinístico
/// RECOMPUTE-FROM-NEWEST GLOBAL: para um conjunto de subcategorias AFETADAS, escolhe — entre TODOS os
/// <c>EvidenceSignal</c> do tenant que mapeiam para cada uma (de QUALQUER conector, não só o que disparou um
/// lote) — a evidência determinística MAIS AUTORITATIVA e devolve o veredito que ela SUSTENTA (ou a AUSÊNCIA de
/// veredito). NÃO escreve no ledger: só computa. Quem aplica é o chamador — a projeção pós-ingestão
/// (<c>EvidenceIngestionExecutor</c>) e o reparo de estados legados compartilham este mesmo recomputo para que
/// jamais existam duas implementações de score capazes de divergir.
///
/// Opera sob o tenant do contexto (query filter fail-closed em <c>Signals</c> E <c>Connectors</c>). A capability
/// vem do <c>ConnectorConfig</c> de cada sinal e resolve o <see cref="SignalMapping"/> correspondente via a
/// autoridade determinística de mapeamento (<see cref="INistSignalMapper"/>). Precedência independente da ordem
/// do banco (ver <see cref="ScoredEvidence.IsMoreAuthoritativeThan"/>): evento antigo — mesmo de outro conector —
/// nunca sobrescreve evidência mais nova; empate EXATO de instante resolve pelo PIOR veredito (conservador). Um
/// sinal sem hint conhecido não sustenta veredito (o código fica AUSENTE do resultado — o chamador segue NotEvaluated).
/// </summary>
internal sealed class EvidenceTelemetryRecompute
{
    private readonly AegisScoreDbContext _db;
    private readonly INistSignalMapper _mapper;

    public EvidenceTelemetryRecompute(AegisScoreDbContext db, INistSignalMapper mapper)
    {
        _db = db;
        _mapper = mapper;
    }

    /// <summary>
    /// Para cada código afetado, o veredito determinístico sustentado pela evidência GLOBALMENTE mais autoritativa,
    /// ou AUSENTE do dicionário quando nenhum sinal com hint conhecido o sustenta (fail-closed — nunca inventa estado).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, EvidenceVerdict>> ComputeAsync(
        IReadOnlyCollection<string> affectedCodes, CancellationToken ct)
    {
        var result = new Dictionary<string, EvidenceVerdict>(StringComparer.Ordinal);
        if (affectedCodes.Count == 0) return result;

        // TODOS os sinais do tenant (query filter fail-closed em Signals E Connectors), cada um com a capability
        // do SEU conector — a evidência de um controle é global, não por conector. Sem IgnoreQueryFilters.
        var signals = await (
            from s in _db.Signals
            join c in _db.Connectors on s.ConnectorConfigId equals c.Id
            select new PersistedSignal(
                s.Id, s.SignalKey, s.NumericValue, s.Severity, s.Unit, s.CollectedAt, c.Capability))
            .ToListAsync(ct);
        if (signals.Count == 0) return result;

        // Re-mapa por CAPABILITY (a resolução depende dela): uma resolução do mapper por capability distinta.
        var byCapability = new Dictionary<ConnectorCapability, IReadOnlyDictionary<string, SignalMappingResolution>>();
        foreach (var cap in signals.Select(s => s.Capability).Distinct())
        {
            var keys = signals.Where(s => s.Capability == cap)
                .Select(s => (s.SignalKey ?? "").Trim()).Distinct().ToList();
            byCapability[cap] = await _mapper.ResolveAsync(cap, keys, ct);
        }

        foreach (var code in affectedCodes)
        {
            ScoredEvidence? best = null;
            foreach (var s in signals)
            {
                var key = (s.SignalKey ?? "").Trim();
                if (!byCapability[s.Capability].TryGetValue(key, out var r) || !r.SubcategoryCodes.Contains(code))
                    continue;
                var verdict = EvidenceSignalEvaluator.Evaluate(r.ScoringHint, s.NumericValue, s.Severity, s.Unit);
                if (verdict is null) continue;

                var candidate = new ScoredEvidence(verdict, s.CollectedAt, key, s.Id);
                if (best is null || candidate.IsMoreAuthoritativeThan(best))
                    best = candidate;
            }
            if (best is not null) result[code] = best.Verdict;
        }
        return result;
    }

    /// <summary>Projeção leve de um sinal persistido (com a capability do seu conector), para o recompute global.</summary>
    private sealed record PersistedSignal(
        Guid Id, string SignalKey, double? NumericValue, int? Severity, string? Unit,
        DateTimeOffset CollectedAt, ConnectorCapability Capability);

    /// <summary>
    /// Evidência já avaliada, candidata a autoridade de um controle. Precedência DETERMINÍSTICA e independente da
    /// ordem do banco: (1) <c>CollectedAt</c> mais recente vence; (2) empate EXATO → PIOR veredito de forma
    /// conservadora (NonCompliant &gt; Mitigated &gt; Compliant, para nunca inflar o score num empate); (3) ainda
    /// empatado → chave e depois Id estáveis. Nenhum critério depende da ordem de leitura das linhas.
    /// </summary>
    private sealed record ScoredEvidence(EvidenceVerdict Verdict, DateTimeOffset CollectedAt, string SignalKey, Guid Id)
    {
        public bool IsMoreAuthoritativeThan(ScoredEvidence other)
        {
            if (CollectedAt != other.CollectedAt) return CollectedAt > other.CollectedAt;

            var rank = ConservativeRank(Verdict.Status);
            var otherRank = ConservativeRank(other.Verdict.Status);
            if (rank != otherRank) return rank < otherRank;   // menor rank = pior veredito = vence o empate exato

            var byKey = string.CompareOrdinal(SignalKey, other.SignalKey);
            return byKey != 0 ? byKey > 0 : Id.CompareTo(other.Id) > 0;
        }

        /// <summary>Rank de conservadorismo: 0 = pior (mais penaliza o score) → vence o empate EXATO de CollectedAt.</summary>
        private static int ConservativeRank(ControlStatus status) => status switch
        {
            ControlStatus.NonCompliant          => 0,
            ControlStatus.MitigatedByThirdParty => 1,
            ControlStatus.Compliant             => 2,
            _                                   => 3,
        };
    }
}
