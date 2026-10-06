using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Nist;
using AegisScore.Application.Remediation;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static AegisScore.Infrastructure.Nist.NistJourneySupport;

namespace AegisScore.Infrastructure.Nist;

/// <summary>
/// [AEGIS-NIST-JOURNEY-02] Importação CSV de trabalho da jornada NIST, com PRÉVIA antes de gravar.
///
/// Regras (as mesmas na prévia e na aplicação):
/// <list type="bullet">
/// <item>o arquivo é de UMA avaliação · rodada · escopo — linha com rodada ou escopo diferentes é erro;</item>
/// <item>código desconhecido, nível fora de 1–5, "não se aplica" sem justificativa, código repetido → erro da linha;</item>
/// <item>registro existente exige a versão vigente; versão diferente → conflito (concorrência preservada);</item>
/// <item>células vazias MANTÊM o valor atual: a importação nunca apaga campos, registros nem a avaliação inteira;</item>
/// <item>o conteúdo importado fica "aguardando confirmação humana" e fora das médias até ser confirmado na tela;</item>
/// <item>aplicação tudo-ou-nada: com erro ou conflito nada é gravado; o token da prévia amarra arquivo e versões.</item>
/// </list>
/// </summary>
public sealed class NistImportService : INistImportService
{
    internal const int MaxRows = 500;
    internal const int MaxChars = 2_000_000;

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;
    private readonly IControlLanguageCatalog? _language;

    public NistImportService(AegisScoreDbContext db, ITenantContext tenant, TimeProvider clock, IControlLanguageCatalog? language = null)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
        _language = language;
    }

    public async Task<NistCsvFile> TemplateAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        var rows = Ordered(ctx).Select(x =>
        {
            var e = ctx.Evaluation(x.Sub.Id);
            return new NistWorkingCsv.Row(x.Sub.Code, Title(x.Sub), x.Fn, x.Cat, NistLabels.State(StateOf(e, ctx.EvidenceCount(x.Sub.Code))),
                e?.CurrentLevel, e?.TargetLevel, e?.NotApplicable ?? false, e?.Rationale, e?.CurrentComments, e?.TargetComments,
                e?.Gaps, e?.RiskImpact, e?.ImprovementGuidance, e?.OwnerName, e?.Version ?? 0);
        });
        var bytes = NistWorkingCsv.Write(ctx.Assessment.Name, ctx.Cycle.Name, ScopeName(ctx.Scope), rows);
        var stamp = _clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return new NistCsvFile(bytes, $"aegis-nist-trabalho-{cycleId.ToString("N")[..8]}-{scopeId.ToString("N")[..8]}-{stamp}.csv");
    }

    public async Task<NistImportPreview> PreviewAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string csv, string? fileName, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        var plan = Plan(ctx, csv);
        return ToPreview(plan, fileName);
    }

    public async Task<NistImportResult> ApplyAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, string csv, string? fileName, string expectedToken, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        EnsureOpen(ctx.Cycle);
        var plan = Plan(ctx, csv);
        var preview = ToPreview(plan, fileName);
        if (!string.Equals(preview.Token, (expectedToken ?? "").Trim(), StringComparison.Ordinal))
            throw new NistAssessmentConflictException(
                "O arquivo ou as avaliações mudaram desde a prévia. Nada foi gravado: gere a prévia novamente.");
        if (preview.FileErrors.Count > 0 || preview.Errors > 0)
            throw new NistAssessmentValidationException("A prévia tem erros. Corrija o arquivo: nada foi gravado.");
        if (preview.Conflicts > 0)
            throw new NistAssessmentConflictException("Há linhas com versão desatualizada. Nada foi gravado: baixe o CSV de trabalho atual e refaça as alterações.");

        var now = _clock.GetUtcNow();
        var actorName = string.IsNullOrWhiteSpace(actor.DisplayName) ? "autor não identificado" : actor.DisplayName.Trim();
        var safeFile = string.IsNullOrWhiteSpace(fileName) ? "arquivo CSV" : $"\"{Truncate(fileName.Trim(), 120)}\"";
        int created = 0, updated = 0;
        foreach (var item in plan.Items.Where(i => i.Action is NistImportActions.Create or NistImportActions.Update))
        {
            var sub = item.Sub!;
            var note = Truncate($"Importado de {safeFile} (linha {item.Line}) por {actorName} em {Date(now)} — aguarda confirmação humana na tela.", 500);
            SubcategoryEvaluation eval;
            if (item.Action == NistImportActions.Create)
            {
                eval = new SubcategoryEvaluation { AssessmentScopeId = scopeId, CycleId = cycleId, SubcategoryId = sub.Id, CreatedAt = now, EvaluatedBy = EvaluatedBy.Analyst };
                _db.Evaluations.Add(eval);
                created++;
            }
            else
            {
                eval = await _db.Evaluations.FirstAsync(e => e.Id == item.ExistingId, ct);
                if (eval.Version != item.ExpectedVersion)
                    throw new NistAssessmentConflictException("Uma avaliação mudou durante a importação. Nada foi gravado: gere a prévia novamente.");
                updated++;
            }

            var r = item.Result!;
            eval.NotApplicable = r.NotApplicable;
            eval.CurrentLevel = r.Current;
            eval.CurrentScore = r.Current;
            eval.TargetLevel = r.Target;
            eval.TargetScore = r.Target;
            eval.Rationale = r.Rationale;
            eval.CurrentComments = r.CurrentComments;
            eval.TargetComments = r.TargetComments;
            eval.Gaps = r.Gaps;
            eval.RiskImpact = r.Risk;
            eval.ImprovementGuidance = r.Guidance;
            if (r.OwnerChanged)
            {
                // Texto do CSV é texto: nunca vira vínculo com usuário (e retira um vínculo anterior, dito na prévia).
                eval.OwnerName = r.Owner;
                eval.OwnerUserId = null;
                eval.OwnerIsExternal = false;
                eval.OwnerContact = null;
            }
            // Conteúdo importado NÃO é revisão humana: sai das médias até alguém confirmar na tela.
            eval.ContentOrigin = NistContentOrigin.Imported;
            eval.OriginNote = note;
            eval.ReviewedById = null;
            eval.ReviewedByName = null;
            eval.ReviewedAt = null;
            eval.Confidence = null;
            eval.Version += 1;
            Audit(_db, actor, now, ctx.Assessment.Id, cycleId, scopeId, sub.Code, "Import", eval.Id,
                item.Action == NistImportActions.Create ? "Created" : "Updated",
                $"Importação CSV ({safeFile}, linha {item.Line}): {sub.Code} {(item.Action == NistImportActions.Create ? "criada" : "alterada")} como rascunho a confirmar.",
                item.Changes);
        }

        Audit(_db, actor, now, ctx.Assessment.Id, cycleId, scopeId, null, "Import", null, "Applied",
            $"Importação CSV aplicada ({safeFile}): {created} criada(s), {updated} alterada(s), {plan.Items.Count(i => i.Action == NistImportActions.Unchanged)} sem alteração. Conteúdo aguarda confirmação humana.");
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new NistAssessmentConflictException("Uma avaliação foi alterada por outra pessoa durante a importação. Nada foi gravado: gere a prévia novamente.");
        }
        catch (DbUpdateException)
        {
            throw new NistAssessmentConflictException("Outra pessoa registrou uma das subcategorias durante a importação. Nada foi gravado: gere a prévia novamente.");
        }
        _db.ChangeTracker.Clear();
        return new NistImportResult(created, updated, plan.Items.Count(i => i.Action == NistImportActions.Unchanged), preview.Items);
    }

    // =============================================================================================
    //  Plano da importação (puro sobre o contexto carregado)
    // =============================================================================================

    private sealed record Values(int? Current, int? Target, bool NotApplicable, string? Rationale, string? CurrentComments, string? TargetComments,
        string? Gaps, string? Risk, string? Guidance, string? Owner, bool OwnerChanged);

    private sealed class PlanItem
    {
        public int Line { get; init; }
        public string? Code { get; init; }
        public string? Title { get; init; }
        public NistSubcategory? Sub { get; set; }
        public string Action { get; set; } = NistImportActions.Error;
        public List<string> Messages { get; } = new();
        public List<NistFieldChange> Changes { get; } = new();
        public Guid? ExistingId { get; set; }
        public int ExpectedVersion { get; set; }
        public Values? Result { get; set; }
    }

    private sealed record ImportPlan(List<PlanItem> Items, IReadOnlyList<string> FileErrors, string Token);

    private ImportPlan Plan(NistScopeContext ctx, string csv)
    {
        if ((csv ?? "").Length > MaxChars)
            return new ImportPlan(new List<PlanItem>(), new[] { $"Arquivo grande demais (limite de {MaxChars / 1_000_000} MB)." }, Token(ctx, csv ?? "", Array.Empty<PlanItem>()));
        var file = NistWorkingCsv.Parse(csv ?? "", MaxRows);
        var items = new List<PlanItem>();
        if (file.Errors.Count > 0 && file.Headers.Count == 0)
            return new ImportPlan(items, file.Errors, Token(ctx, csv ?? "", items));

        int Col(string name) => file.Headers.ToList().FindIndex(h => NistWorkingCsv.Same(h, name));
        var cols = NistWorkingCsv.Headers.ToDictionary(h => h, Col);
        string? Cell(IReadOnlyList<string> cells, string col) =>
            cols[col] is var i && i >= 0 && i < cells.Count ? (string.IsNullOrWhiteSpace(cells[i]) ? null : cells[i]) : null;

        var subsByCode = ctx.AllSubcategories.ToDictionary(s => s.Code, StringComparer.Ordinal);
        var codeCount = file.Rows
            .Select(r => (Cell(r.Cells, NistWorkingCsv.ColCode) ?? "").Trim().ToUpperInvariant())
            .Where(c => c.Length > 0).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());

        if (file.Errors.Count == 0)
        foreach (var (line, cells) in file.Rows)
        {
            var code = (Cell(cells, NistWorkingCsv.ColCode) ?? "").Trim().ToUpperInvariant();
            var item = new PlanItem { Line = line, Code = code.Length == 0 ? null : code, Title = Cell(cells, NistWorkingCsv.ColTitle) };
            items.Add(item);

            // ---- Identificação ----
            if (code.Length == 0) { item.Messages.Add("Código da subcategoria ausente."); continue; }
            if (!subsByCode.TryGetValue(code, out var sub)) { item.Messages.Add($"Código \"{code}\" não existe no catálogo da avaliação."); continue; }
            item.Sub = sub;
            if (codeCount[code] > 1) { item.Messages.Add($"Código {code} aparece mais de uma vez no arquivo."); continue; }
            var cycleName = Cell(cells, NistWorkingCsv.ColCycle);
            if (cycleName is null || !string.Equals(cycleName.Trim(), ctx.Cycle.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                item.Messages.Add($"Rodada \"{cycleName ?? "(vazia)"}\" diferente da rodada de destino \"{ctx.Cycle.Name}\".");
            var scopeName = Cell(cells, NistWorkingCsv.ColScope);
            if (scopeName is null || !string.Equals(scopeName.Trim(), ScopeName(ctx.Scope).Trim(), StringComparison.OrdinalIgnoreCase))
                item.Messages.Add($"Escopo \"{scopeName ?? "(vazio)"}\" diferente do escopo de destino \"{ScopeName(ctx.Scope)}\".");
            if (Cell(cells, NistWorkingCsv.ColAssessment) is { } an && !string.Equals(an.Trim(), ctx.Assessment.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                item.Messages.Add($"Avaliação \"{an}\" diferente da avaliação de destino \"{ctx.Assessment.Name}\".");
            if (item.Title is { } t && !string.Equals(t.Trim(), Title(sub).Trim(), StringComparison.Ordinal) && !string.Equals(t.Trim(), code, StringComparison.Ordinal))
                item.Messages.Add($"Aviso: o título no arquivo difere do catálogo (\"{Title(sub)}\"); a linha é identificada pelo código.");

            // ---- Valores ----
            int? ParseLevel(string col, string label)
            {
                var raw = Cell(cells, col);
                if (raw is null) return null;
                if (int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v is >= 1 and <= 5) return v;
                item.Messages.Add($"{label} \"{raw}\" fora da escala: use um inteiro de 1 a 5 (ou deixe vazio para manter).");
                return null;
            }
            var curRaw = ParseLevel(NistWorkingCsv.ColCurrent, "Atual");
            var tgtRaw = ParseLevel(NistWorkingCsv.ColTarget, "Alvo");
            bool? naRaw = null;
            if (Cell(cells, NistWorkingCsv.ColNotApplicable) is { } naText)
            {
                var n = NistWorkingCsv.Normalize(naText);
                if (n is "sim" or "s" or "yes" or "true" or "1") naRaw = true;
                else if (n is "nao" or "n" or "no" or "false" or "0") naRaw = false;
                else item.Messages.Add($"\"Não se aplica\" deve ser Sim ou Não (veio \"{naText}\").");
            }
            string? Text(string col, int max, string label)
            {
                var v = Cell(cells, col)?.Trim();
                if (v is not null && v.Length > max) { item.Messages.Add($"{label} passa de {max} caracteres."); return null; }
                return v;
            }
            var rationale = Text(NistWorkingCsv.ColRationale, MaxLongText, "A justificativa");
            var cc = Text(NistWorkingCsv.ColCurrentComments, MaxLongText, "A observação da situação atual");
            var tc = Text(NistWorkingCsv.ColTargetComments, MaxLongText, "A observação do alvo");
            var gaps = Text(NistWorkingCsv.ColGaps, MaxLongText, "As lacunas");
            var risk = Text(NistWorkingCsv.ColRisk, MaxDescription, "O risco/impacto");
            var guidance = Text(NistWorkingCsv.ColGuidance, MaxLongText, "A orientação de melhoria");
            var owner = Text(NistWorkingCsv.ColOwner, MaxName, "O responsável");

            var versionRaw = Cell(cells, NistWorkingCsv.ColVersion);
            int version = 0;
            if (versionRaw is not null && !int.TryParse(versionRaw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out version))
                item.Messages.Add($"Versão \"{versionRaw}\" inválida.");

            var existing = ctx.Evaluation(sub.Id);
            if (existing is null)
            {
                if (version != 0) { item.Action = NistImportActions.Conflict; item.Messages.Add($"Versão {version} informada, mas não há registro desta subcategoria na rodada (foi removido ou é de outra rodada)."); }
            }
            else
            {
                item.ExistingId = existing.Id;
                item.ExpectedVersion = existing.Version;
                if (versionRaw is null) item.Messages.Add($"Informe a versão do registro existente (vigente: {existing.Version}). Baixe o CSV de trabalho atual.");
                else if (version != existing.Version)
                {
                    item.Action = NistImportActions.Conflict;
                    item.Messages.Add($"Versão {version} desatualizada: a vigente é {existing.Version} (alguém alterou depois da exportação).");
                }
            }

            if (item.Messages.Any(m => !m.StartsWith("Aviso:", StringComparison.Ordinal)))
            {
                if (item.Action != NistImportActions.Conflict) item.Action = NistImportActions.Error;
                continue;
            }

            // Vazio = manter. "Não se aplica = Sim" retira os níveis.
            var na = naRaw ?? existing?.NotApplicable ?? false;
            var result = new Values(
                na ? null : curRaw ?? existing?.CurrentLevel,
                na ? null : tgtRaw ?? existing?.TargetLevel,
                na,
                rationale ?? existing?.Rationale,
                cc ?? existing?.CurrentComments,
                tc ?? existing?.TargetComments,
                gaps ?? existing?.Gaps,
                risk ?? existing?.RiskImpact,
                guidance ?? existing?.ImprovementGuidance,
                owner ?? existing?.OwnerName,
                owner is not null && !string.Equals(owner, existing?.OwnerName, StringComparison.Ordinal));
            if (na && (curRaw is not null || tgtRaw is not null))
            {
                item.Action = NistImportActions.Error;
                item.Messages.Add("Um resultado que não se aplica não recebe atual nem alvo.");
                continue;
            }
            if (na && (result.Rationale ?? "").Length < MinJustification)
            {
                item.Action = NistImportActions.Error;
                item.Messages.Add("\"Não se aplica\" exige justificativa de pelo menos 10 caracteres.");
                continue;
            }

            Diff(item.Changes, "notApplicable", "não se aplica", existing is null ? null : existing.NotApplicable ? "sim" : "não", result.NotApplicable ? "sim" : "não");
            Diff(item.Changes, "currentLevel", "situação atual", Level(existing?.CurrentLevel), Level(result.Current));
            Diff(item.Changes, "targetLevel", "alvo", Level(existing?.TargetLevel), Level(result.Target));
            Diff(item.Changes, "rationale", "justificativa", existing?.Rationale, result.Rationale);
            Diff(item.Changes, "currentComments", "observações da situação atual", existing?.CurrentComments, result.CurrentComments);
            Diff(item.Changes, "targetComments", "observações do alvo", existing?.TargetComments, result.TargetComments);
            Diff(item.Changes, "gaps", "lacunas", existing?.Gaps, result.Gaps);
            Diff(item.Changes, "riskImpact", "risco/impacto", existing?.RiskImpact, result.Risk);
            Diff(item.Changes, "improvementGuidance", "orientação de melhoria", existing?.ImprovementGuidance, result.Guidance);
            if (result.OwnerChanged)
                Diff(item.Changes, "owner", "responsável pela prática",
                    existing is null ? null : DescribePerson(existing.OwnerUserId, existing.OwnerName, existing.OwnerIsExternal, existing.OwnerContact), result.Owner);

            var hasContent = result.NotApplicable || result.Current is not null || result.Target is not null
                || new[] { result.Rationale, result.CurrentComments, result.TargetComments, result.Gaps, result.Risk, result.Guidance, result.Owner }.Any(x => x is not null);
            if (item.Changes.Count == 0 || existing is null && !hasContent)
            {
                item.Action = NistImportActions.Unchanged;
                continue;
            }
            if (existing is { HumanConfirmed: true })
                item.Messages.Add("Aviso: a avaliação confirmada desta subcategoria passará a rascunho importado e sairá das médias até nova confirmação na tela.");
            if (existing?.OwnerUserId is not null && result.OwnerChanged)
                item.Messages.Add("Aviso: o responsável vinculado a um usuário será substituído pelo texto do arquivo (texto nunca vira vínculo).");
            item.Action = existing is null ? NistImportActions.Create : NistImportActions.Update;
            item.Result = result;
        }

        return new ImportPlan(items, file.Errors, Token(ctx, csv ?? "", items));
    }

    private static NistImportPreview ToPreview(ImportPlan plan, string? fileName)
    {
        var views = plan.Items.Select(i => new NistImportRowView(i.Line, i.Code, i.Sub is null ? i.Title : i.Title ?? i.Code, i.Action, i.Messages, i.Changes)).ToList();
        var errors = plan.Items.Count(i => i.Action == NistImportActions.Error);
        var conflicts = plan.Items.Count(i => i.Action == NistImportActions.Conflict);
        var writes = plan.Items.Count(i => i.Action is NistImportActions.Create or NistImportActions.Update);
        return new NistImportPreview(plan.Token, fileName, plan.Items.Count,
            plan.Items.Count(i => i.Action == NistImportActions.Create),
            plan.Items.Count(i => i.Action == NistImportActions.Update),
            plan.Items.Count(i => i.Action == NistImportActions.Unchanged),
            errors, conflicts,
            plan.FileErrors.Count == 0 && errors == 0 && conflicts == 0 && writes > 0,
            views, plan.FileErrors, NistWorkingCsv.UpdateRule);
    }

    /// <summary>
    /// Token da prévia: arquivo + destino (avaliação · rodada · escopo) + versão vigente de cada registro tocado. Qualquer
    /// mudança entre prévia e aplicação invalida o token.
    /// </summary>
    private static string Token(NistScopeContext ctx, string csv, IEnumerable<PlanItem> items)
    {
        var sb = new StringBuilder();
        sb.Append(ctx.Assessment.Id.ToString("N")).Append('|').Append(ctx.Cycle.Id.ToString("N")).Append('|').Append(ctx.Scope.Id.ToString("N")).Append('|');
        sb.Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv))));
        foreach (var e in ctx.Evaluations.Values.OrderBy(e => e.SubcategoryId))
            sb.Append('|').Append(e.SubcategoryId.ToString("N")).Append(':').Append(e.Version.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private IEnumerable<(NistSubcategory Sub, string Fn, string Cat)> Ordered(NistScopeContext ctx) =>
        ctx.Catalog.Functions.SelectMany(f => f.Categories.Select(c => (f, c)))
            .OrderBy(x => Array.IndexOf(FunctionOrder, x.f.Code)).ThenBy(x => CategoryRank(x.c.Code))
            .SelectMany(x => x.c.Subcategories.OrderBy(s => s.Code, StringComparer.Ordinal).Select(s => (s, x.f.Code, x.c.Code)));

    private string Title(NistSubcategory s) => _language?.Get(s.Code)?.Title ?? s.Code;
}
