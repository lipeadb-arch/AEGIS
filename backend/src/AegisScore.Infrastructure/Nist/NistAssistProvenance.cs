using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Nist;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static AegisScore.Infrastructure.Nist.NistJourneySupport;

namespace AegisScore.Infrastructure.Nist;

/// <summary>
/// [AEGIS-NIST-AI-ASSIST-01] Procedência do conteúdo assistido: a INCORPORAÇÃO (validação da geração no momento da gravação,
/// registro append-only e entrada na trilha) e a LEITURA (que campos vigentes ainda são o texto incorporado). Gerar não grava
/// nada; incorporar acontece só pelos fluxos normais de gravação, com a versão-base do rascunho da pessoa.
/// </summary>
internal static class NistAssistProvenance
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal sealed record FieldRecord(string Field, string Label, string? SuggestedHash, string IncorporatedHash, bool Edited);

    /// <summary>Um campo levado ao registro: o nome, o rótulo e o texto efetivamente gravado.</summary>
    internal sealed record Incorporated(string Field, string Label, string? Text);

    internal static readonly IReadOnlyDictionary<string, string> EvaluationFields = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["rationale"] = "justificativa",
        ["gaps"] = "lacunas observadas",
        ["riskImpact"] = "risco ou impacto",
        ["improvementGuidance"] = "orientação de melhoria",
        ["currentLevel"] = "nível da situação atual",
    };

    /// <summary>SHA-256 do texto normalizado (quebras de linha e espaços das pontas) — o que conta como "o mesmo texto".</summary>
    internal static string Hash(string? text)
    {
        var t = (text ?? "").Replace("\r\n", "\n").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(t))).ToLowerInvariant();
    }

    /// <summary>
    /// Confere a geração antes de incorporar: existe NESTE tenant (filtro global), é do mesmo tipo, avaliação, rodada, escopo,
    /// subcategoria e achado; e o contexto em que foi gerada ainda é o atual — senão 409, a menos que a pessoa declare tê-la
    /// revisado diante do contexto atual. A versão-base do rascunho continua sendo conferida pelo fluxo de gravação.
    /// </summary>
    internal static async Task<(NistAiAssistance Generation, bool Stale)> ResolveAsync(
        AegisScoreDbContext db, NistAssistanceReference reference, NistAssistKind kind, Guid assessmentId, Guid cycleId, Guid scopeId,
        string? code, Guid? findingId, Func<NistAiAssistance, Task<string>> currentFingerprint, CancellationToken ct)
    {
        var g = await db.NistAiAssistances.AsNoTracking().FirstOrDefaultAsync(x => x.Id == reference.AssistanceId, ct)
                ?? throw new NistAssessmentNotFoundException("A sugestão informada não existe neste ambiente.");
        if (g.Kind != kind || g.AssessmentId != assessmentId || g.CycleId != cycleId || g.AssessmentScopeId != scopeId
            || (code is not null && !string.Equals(g.SubcategoryCode, code, StringComparison.Ordinal))
            || (findingId is not null && g.FindingId != findingId))
            throw new NistAssessmentValidationException(
                "A sugestão informada é de outro contexto (avaliação, rodada, escopo, subcategoria ou achado) e não pode ser incorporada aqui.");
        var stale = !string.Equals(await currentFingerprint(g), g.ContextFingerprint, StringComparison.Ordinal);
        if (stale && !reference.AcknowledgeStale)
            throw new NistAssistStaleException(
                "A sugestão foi gerada sobre um contexto que mudou depois (avaliação, evidências, procedimentos, achados ou planos). " +
                "Gere outra sugestão ou confirme que revisou o texto diante do estado atual antes de incorporar. Nada foi gravado.");
        return (g, stale);
    }

    /// <summary>Campos pedidos precisam existir no tipo de registro e ter texto aplicável na geração.</summary>
    internal static IReadOnlyList<string> CheckFields(NistAssistanceReference reference, IEnumerable<string> allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        var fields = (reference.Fields ?? Array.Empty<string>()).Select(f => (f ?? "").Trim()).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (fields.Count == 0) throw new NistAssessmentValidationException("Diga quais campos vieram da sugestão.");
        if (fields.FirstOrDefault(f => !set.Contains(f)) is { } bad)
            throw new NistAssessmentValidationException($"O campo \"{bad}\" não recebe conteúdo assistido neste registro.");
        return fields;
    }

    /// <summary>Cada campo pedido precisa ter texto aplicável na geração (nível ausente ou descartado não se incorpora).</summary>
    internal static void RequireApplicable(NistAiAssistance g, IEnumerable<string> fields)
    {
        var applicable = ApplicableOf(g);
        if (fields.FirstOrDefault(f => !applicable.ContainsKey(f)) is { } missing)
            throw new NistAssessmentValidationException($"A sugestão não traz conteúdo para o campo \"{missing}\": nada foi gravado.");
    }

    internal static IReadOnlyDictionary<string, string> ApplicableOf(NistAiAssistance g)
    {
        if (string.IsNullOrWhiteSpace(g.ApplicableJson)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(g.ApplicableJson, Json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Registra a incorporação (append-only) e a entrada na trilha — vão no MESMO SaveChanges da gravação.</summary>
    internal static void Record(
        AegisScoreDbContext db, NistAiAssistance g, bool staleAcknowledged, NistAssistTarget target, Guid targetId,
        IReadOnlyList<Incorporated> fields, RemediationActor actor, DateTimeOffset now, string? suggestedOverride = null)
    {
        var applicable = ApplicableOf(g);
        var records = fields.Select(f =>
        {
            var suggested = suggestedOverride ?? (applicable.TryGetValue(f.Field, out var s) ? s : null);
            var incorporated = Hash(f.Text);
            var suggestedHash = suggested is null ? null : Hash(suggested);
            return new FieldRecord(f.Field, f.Label, suggestedHash, incorporated, suggestedHash != incorporated);
        }).ToList();

        db.NistAiIncorporations.Add(new NistAiIncorporation
        {
            AssessmentId = g.AssessmentId,
            CycleId = g.CycleId,
            AssessmentScopeId = g.AssessmentScopeId,
            SubcategoryCode = g.SubcategoryCode,
            AssistanceId = g.Id,
            TargetKind = target,
            TargetId = targetId,
            FieldsJson = JsonSerializer.Serialize(records, Json),
            StaleAcknowledged = staleAcknowledged,
            IncorporatedByAccountId = actor.AccountId,
            IncorporatedByName = string.IsNullOrWhiteSpace(actor.DisplayName) ? null : Truncate(actor.DisplayName.Trim(), MaxName),
            IncorporatedAt = now,
            CreatedAt = now,
        });
        var changes = records.Select(r => new NistFieldChange("assisted:" + r.Field, r.Label, null,
            r.Edited ? "sugestão editada pela pessoa" : "sugestão aceita sem edição")).ToList();
        Audit(db, actor, now, g.AssessmentId, g.CycleId, g.AssessmentScopeId, g.SubcategoryCode, "Assistance", g.Id, "Incorporated",
            $"Conteúdo da sugestão {(g.Mode == NistAssistEngineMode.Real ? "da IA" : "SIMULADA")} de {Date(g.GeneratedAt)} incorporado por decisão da pessoa " +
            $"({string.Join(", ", records.Select(r => r.Label))})" + (staleAcknowledged ? " — sugestão desatualizada, revisada pela pessoa." : ".") +
            " Incorporar não confirma avaliação, não aprova revisão nem conclui plano.", changes);
    }

    // =============================================================================================
    //  Leitura
    // =============================================================================================

    /// <summary>Incorporações de uma rodada e escopo, com o que se sabe de cada geração (modo, data, quem pediu).</summary>
    internal sealed class Lookup
    {
        private readonly List<(NistAiIncorporation Row, IReadOnlyList<FieldRecord> Fields)> _rows;
        private readonly Dictionary<Guid, NistAiAssistance> _generations;

        public Lookup(List<(NistAiIncorporation, IReadOnlyList<FieldRecord>)> rows, Dictionary<Guid, NistAiAssistance> generations)
        {
            _rows = rows;
            _generations = generations;
        }

        public static readonly Lookup Empty = new(new(), new());

        /// <summary>
        /// Campos do registro cujo texto VIGENTE ainda é o incorporado (a última incorporação de cada campo). Se a pessoa
        /// reescreveu o campo depois, ele deixa de ser apresentado como assistido.
        /// </summary>
        public IReadOnlyList<NistAssistedFieldView> For(NistAssistTarget target, Guid targetId, IReadOnlyDictionary<string, string?> current)
        {
            var result = new List<NistAssistedFieldView>();
            var latest = _rows.Where(r => r.Row.TargetKind == target && r.Row.TargetId == targetId)
                .OrderByDescending(r => r.Row.IncorporatedAt.UtcTicks).ThenByDescending(r => r.Row.Id).ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (row, fields) in latest)
                foreach (var f in fields)
                {
                    if (!seen.Add(f.Field)) continue;
                    if (!current.TryGetValue(f.Field, out var text) || Hash(text) != f.IncorporatedHash) continue;
                    _generations.TryGetValue(row.AssistanceId, out var g);
                    result.Add(new NistAssistedFieldView(f.Field, f.Label, row.AssistanceId,
                        g?.Mode.ToString() ?? "Simulated", g?.GeneratedAt ?? row.IncorporatedAt, g?.RequestedByName,
                        row.IncorporatedByName, row.IncorporatedAt, f.Edited, row.StaleAcknowledged));
                }
            return result.OrderBy(f => f.Field, StringComparer.Ordinal).ToList();
        }
    }

    internal static async Task<Lookup> LoadAsync(AegisScoreDbContext db, Guid cycleId, Guid scopeId, CancellationToken ct)
    {
        var rows = await db.NistAiIncorporations.AsNoTracking()
            .Where(i => i.CycleId == cycleId && i.AssessmentScopeId == scopeId).ToListAsync(ct);
        if (rows.Count == 0) return Lookup.Empty;
        var ids = rows.Select(r => r.AssistanceId).Distinct().ToList();
        var generations = await db.NistAiAssistances.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        return new Lookup(rows.Select(r => (r, Fields(r.FieldsJson))).ToList(), generations);
    }

    /// <summary>Incorporações que apontam para os registros indicados (achados e planos de várias rodadas).</summary>
    internal static async Task<Lookup> LoadForTargetsAsync(AegisScoreDbContext db, IReadOnlyCollection<Guid> targetIds, CancellationToken ct)
    {
        if (targetIds.Count == 0) return Lookup.Empty;
        var ids = targetIds.ToList();
        var rows = await db.NistAiIncorporations.AsNoTracking().Where(i => ids.Contains(i.TargetId)).ToListAsync(ct);
        if (rows.Count == 0) return Lookup.Empty;
        var generationIds = rows.Select(r => r.AssistanceId).Distinct().ToList();
        var generations = await db.NistAiAssistances.AsNoTracking().Where(a => generationIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        return new Lookup(rows.Select(r => (r, Fields(r.FieldsJson))).ToList(), generations);
    }

    internal static IReadOnlyList<FieldRecord> Fields(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<FieldRecord>>(json, Json) ?? new List<FieldRecord>();
        }
        catch (JsonException)
        {
            return Array.Empty<FieldRecord>();
        }
    }

    internal static IReadOnlyDictionary<string, string?> EvaluationTexts(SubcategoryEvaluation e) => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["rationale"] = e.Rationale,
        ["gaps"] = e.Gaps,
        ["riskImpact"] = e.RiskImpact,
        ["improvementGuidance"] = e.ImprovementGuidance,
        ["currentLevel"] = e.CurrentLevel?.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    internal static NistAssistedFieldView? ProcedureField(Lookup lookup, NistTestProcedure p) =>
        lookup.For(NistAssistTarget.Procedure, p.Id, new Dictionary<string, string?>(StringComparer.Ordinal) { ["procedure"] = p.Procedure }).FirstOrDefault();

    internal static IReadOnlyList<NistAssistedFieldView> FindingFields(Lookup lookup, NistFinding f, ActionPlanView? plan) =>
        lookup.For(NistAssistTarget.Finding, f.Id, new Dictionary<string, string?>(StringComparer.Ordinal) { ["recommendation"] = f.Recommendation })
            .Concat(plan is null ? Array.Empty<NistAssistedFieldView>()
                : lookup.For(NistAssistTarget.Plan, plan.Id, new Dictionary<string, string?>(StringComparer.Ordinal) { ["proposedAction"] = plan.ProposedAction }))
            .ToList();

    internal static NistReportAssistance? Report(IReadOnlyList<NistAssistedFieldView> fields) =>
        fields.Count == 0 ? null : new NistReportAssistance(fields.Select(f => new NistReportAssistedField(
            f.Field, f.Label, f.AssistanceId, f.Mode, NistReportCanonical.Micro(f.GeneratedAt), f.IncorporatedByName,
            NistReportCanonical.Micro(f.IncorporatedAt), f.Edited, f.StaleAcknowledged)).ToList());
}
