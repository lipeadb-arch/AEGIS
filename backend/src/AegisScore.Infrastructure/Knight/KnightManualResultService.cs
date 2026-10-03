using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Reference;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisScore.Infrastructure.Knight;

/// <summary>
/// [AEGIS-KNIGHT-CLOSURE-01] Registro e leitura dos resultados manuais estruturados. Scoped (DbContext com filtro de tenant
/// e carimbo fail-closed). NÃO escreve nota, veredito, cobertura automatizada nem fotografia: a fotografia CONGELA os
/// resultados vigentes no momento da publicação, à parte.
/// </summary>
public sealed class KnightManualResultService : IKnightManualResultService
{
    private const int MinJustification = 10;
    private const int MaxJustification = 2000;
    private const int MaxName = 200;
    private const int MaxEvidence = 500;

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public KnightManualResultService(AegisScoreDbContext db, ITenantContext tenant, TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
    }

    public async Task<IReadOnlyList<KnightManualResultView>> CurrentAsync(CancellationToken ct = default)
    {
        RequireTenant();
        var rows = await _db.KnightManualAssessments.AsNoTracking().ToListAsync(ct);
        var now = _clock.GetUtcNow();
        return rows
            .GroupBy(r => r.ReferenceKey, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.RecordedAt).ThenByDescending(r => r.Id).First())
            .Where(r => r.Result != KnightManualResult.Withdrawn)
            .OrderBy(r => r.ReferenceKey, StringComparer.Ordinal)
            .Select(r => View(r, now))
            .ToList();
    }

    public async Task<IReadOnlyList<KnightManualResultView>> HistoryAsync(string referenceKey, CancellationToken ct = default)
    {
        RequireTenant();
        var key = (referenceKey ?? "").Trim();
        var rows = await _db.KnightManualAssessments.AsNoTracking().Where(r => r.ReferenceKey == key).ToListAsync(ct);
        var now = _clock.GetUtcNow();
        return rows.OrderByDescending(r => r.RecordedAt).ThenByDescending(r => r.Id).Select(r => View(r, now)).ToList();
    }

    public async Task<KnightManualResultView> RecordAsync(RecordKnightManualResultCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant();
        ArgumentNullException.ThrowIfNull(command);
        var now = _clock.GetUtcNow();

        var key = (command.ReferenceKey ?? "").Trim();
        var status = KnightReferenceCatalog.Coverage().Controls.FirstOrDefault(c => string.Equals(c.Control.Key, key, StringComparison.Ordinal))
            ?? throw new KnightManualResultValidationException("Controle de referência desconhecido.");
        if (!KnightManualResults.Eligible(status.Disposition))
            throw new KnightManualResultValidationException(
                $"Este controle é {KnightReferenceCatalog.DispositionLabel(status.Disposition).ToLowerInvariant()}: o resultado vem da avaliação automatizada. "
                + "Só controles de verificação manual, sem método publicado ou que exigem acesso que o conector não tem recebem resultado manual.");

        if (!KnightManualResults.TryParse(command.Result, out var result))
            throw new KnightManualResultValidationException("Resultado inválido: use conforme, não conforme, não se aplica ou retirado.");

        var justification = Clean(command.Justification);
        if (justification.Length < MinJustification)
            throw new KnightManualResultValidationException($"Informe a justificativa (ao menos {MinJustification} caracteres): o que foi verificado e como.");
        if (justification.Length > MaxJustification)
            throw new KnightManualResultValidationException($"Justificativa acima de {MaxJustification} caracteres.");

        var responsible = Clean(command.ResponsibleName);
        if (responsible.Length == 0) throw new KnightManualResultValidationException("Informe o responsável pelo resultado.");
        if (responsible.Length > MaxName) throw new KnightManualResultValidationException($"Responsável acima de {MaxName} caracteres.");

        var evidence = Clean(command.EvidenceReference);
        if (evidence.Length > MaxEvidence) throw new KnightManualResultValidationException($"Referência de evidência acima de {MaxEvidence} caracteres.");

        string? docTitle = null, docHash = null;
        if (command.EvidenceDocumentId is { } docId)
        {
            var doc = await _db.GovernanceDocuments.AsNoTracking().Where(d => d.Id == docId)
                .Select(d => new { d.Title, d.Sha256 }).FirstOrDefaultAsync(ct)
                ?? throw new KnightManualResultValidationException("Documento de evidência não encontrado na Central de Governança deste tenant.");
            docTitle = Clean(doc.Title);
            if (docTitle.Length > 300) docTitle = docTitle[..300];
            docHash = doc.Sha256;
        }

        if (result != KnightManualResult.Withdrawn && evidence.Length == 0 && command.EvidenceDocumentId is null)
            throw new KnightManualResultValidationException(
                "Vincule a evidência: a referência (chamado, documento, registro) ou um documento da Central de Governança.");

        if (command.ValidUntil is { } until && until < DateOnly.FromDateTime(now.UtcDateTime))
            throw new KnightManualResultValidationException("A validade não pode estar no passado.");

        if (result == KnightManualResult.Withdrawn)
        {
            var current = (await CurrentAsync(ct)).FirstOrDefault(r => r.ReferenceKey == key);
            if (current is null) throw new KnightManualResultValidationException("Não há resultado vigente para retirar neste controle.");
        }

        // O registro mais recente é o vigente: o carimbo é estritamente crescente por controle, para que dois registros no
        // mesmo instante (substituição ou retirada imediata) nunca deixem a ordem por conta do identificador.
        var previous = await _db.KnightManualAssessments.AsNoTracking().Where(r => r.ReferenceKey == key).Select(r => r.RecordedAt).ToListAsync(ct);
        var recordedAt = previous.Count > 0 && previous.Max() is var last && last >= now ? last.AddMilliseconds(1) : now;

        var row = new KnightManualAssessment
        {
            ReferenceKey = key,
            Result = result,
            Justification = justification,
            ResponsibleName = responsible,
            EvidenceReference = evidence.Length == 0 ? null : evidence,
            EvidenceDocumentId = command.EvidenceDocumentId,
            EvidenceDocumentTitle = docTitle,
            EvidenceDocumentSha256 = docHash,
            ValidUntil = result == KnightManualResult.Withdrawn ? null : command.ValidUntil,
            ReferenceDisposition = status.Disposition.ToString(),
            CatalogVersion = KnightCatalog.Version,
            RecordedByAccountId = actor?.AccountId,
            RecordedByName = Clean(actor?.DisplayName) is { Length: > 0 } n ? (n.Length > MaxName ? n[..MaxName] : n) : "",
            RecordedAt = recordedAt,
        };
        _db.KnightManualAssessments.Add(row);
        await _db.SaveChangesAsync(ct);
        return View(row, now);
    }

    private void RequireTenant()
    {
        if (_tenant.TenantId is not Guid)
            throw new TenantSecurityException("Resultado manual sem tenant resolvido no contexto (fail-closed).");
    }

    /// <summary>Texto de uma linha só com caracteres imprimíveis (quebras de linha da justificativa preservadas).</summary>
    private static string Clean(string? text) =>
        new string((text ?? "").Where(ch => ch == '\n' || !char.IsControl(ch)).ToArray()).Trim();

    private static KnightManualResultView View(KnightManualAssessment r, DateTimeOffset now) => new(
        r.Id, r.ReferenceKey, r.Result, KnightManualResults.ResultLabel(r.Result), r.Justification, r.ResponsibleName,
        r.EvidenceReference, r.EvidenceDocumentId, r.EvidenceDocumentTitle, r.EvidenceDocumentSha256, r.ValidUntil,
        KnightManualResults.IsExpired(r.ValidUntil, now), r.ReferenceDisposition, r.CatalogVersion, r.RecordedByName, r.RecordedAt);
}
