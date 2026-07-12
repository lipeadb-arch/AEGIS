using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Controllers;

/// <summary>
/// GOVERN → Document Hub. Fluxo de ingestão: recebe o documento (upload ou integração) → enfileira
/// para leitura da IA → expõe o mapeamento NIST. Isolamento implícito (query filter + stamping
/// fail-closed); sem [FromHeader] de tenant.
/// </summary>
[ApiController]
[Route("api/v1/governance/documents")]
public class GovernanceDocumentsController : ControllerBase
{
    private readonly AegisScoreDbContext _db;
    private readonly IDocumentStorage _storage;
    private readonly IDocumentAnalysisQueue _queue;
    private readonly IPolicySyncTrigger _policySync;
    private readonly ITenantContext _tenant;

    public GovernanceDocumentsController(
        AegisScoreDbContext db, IDocumentStorage storage, IDocumentAnalysisQueue queue,
        IPolicySyncTrigger policySync, ITenantContext tenant)
    {
        _db = db;
        _storage = storage;
        _queue = queue;
        _policySync = policySync;
        _tenant = tenant;
    }

    /// <summary>Upload manual: grava o binário + hash SHA-256 e enfileira a leitura da IA.</summary>
    [HttpPost]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult<DocumentAcceptedDto>> Upload(
        IFormFile file, [FromForm] string title, [FromForm] GovernanceDocumentType type, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("Arquivo vazio.");

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var sha = Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();

        // Dedupe por hash (o query filter já escopa a checagem ao tenant ambiente). Fast-path que evita a
        // exceção no caso comum; a corrida (dois uploads simultâneos do mesmo arquivo passam ambos por este
        // AnyAsync antes de qualquer commit) é fechada pelo índice único no SaveChanges, abaixo.
        if (await _db.GovernanceDocuments.AnyAsync(d => d.Sha256 == sha, ct))
            return Conflict("Documento idêntico já ingerido (mesmo hash) neste cliente.");

        var doc = new GovernanceDocument
        {
            // Sem TenantId — carimbado no SaveChangesAsync (fail-closed).
            Title = string.IsNullOrWhiteSpace(title) ? file.FileName : title,
            Type = type,
            Source = DocumentSource.UploadManual,
            FileName = file.FileName,
            ContentType = file.ContentType,
            FileSizeBytes = file.Length,
            Sha256 = sha,
            AnalysisStatus = AiAnalysisStatus.Queued,
            AnalysisQueuedAt = DateTimeOffset.UtcNow,
        };
        _db.GovernanceDocuments.Add(doc);
        try
        {
            await _db.SaveChangesAsync(ct);   // carimba TenantId e materializa doc.Id
        }
        catch (DbUpdateException)
        {
            // Corrida perdida: outro upload gravou o mesmo hash entre o nosso AnyAsync e este INSERT. O índice
            // único (TenantId, Sha256) rejeitou a duplicata — idempotente, resolve no MESMO 409 da checagem prévia.
            return Conflict("Documento idêntico já ingerido (mesmo hash) neste cliente.");
        }

        doc.StorageUri = await _storage.SaveAsync(doc.TenantId, doc.Id, file.FileName, buffer, ct);
        await _db.SaveChangesAsync(ct);

        await _queue.EnqueueAsync(doc.Id, ct);
        return Accepted(new DocumentAcceptedDto(doc.Id, doc.AnalysisStatus.ToString()));
    }

    /// <summary>
    /// Registra um documento vindo de integração (SharePoint/Confluence). O conector de ingestão
    /// depois anexa o binário e chama <c>/reanalyze</c> para disparar a leitura da IA.
    /// </summary>
    [HttpPost("connect")]
    public async Task<ActionResult<IdResponse>> Connect(ConnectDocumentRequest req, CancellationToken ct)
    {
        var doc = new GovernanceDocument
        {
            Title = req.Title,
            Type = req.Type,
            Source = DocumentSource.Integracao,
            SourceReference = req.SourceReference,
            AnalysisStatus = AiAnalysisStatus.Pending,
        };
        _db.GovernanceDocuments.Add(doc);
        await _db.SaveChangesAsync(ct);
        return new IdResponse(doc.Id);
    }

    /// <summary>
    /// Gatilho MANUAL de sincronização das políticas (Govern) para usuários executivos: enfileira o tenant
    /// do usuário autenticado para o <c>PolicyIngestionWorker</c> puxar as fontes externas (SharePoint,
    /// Google…) AGORA, sem esperar o ciclo do timer. Assíncrono por design — publica no canal e devolve
    /// 202 na hora; a ingestão roda em background. Acompanhe o resultado por <c>GET /governance/documents</c>.
    /// </summary>
    /// <response code="202">Sincronização enfileirada; será processada em background pelo worker.</response>
    [HttpPost("sync")]
    public async Task<ActionResult<PolicySyncAcceptedDto>> Sync(CancellationToken ct)
    {
        // Tenant SEMPRE do contexto resolvido (claim tenant_id do JWT), NUNCA de input do cliente — o
        // mesmo guard fail-closed do resto da escrita. Sob [Authorize] + TenantConsistencyMiddleware, null
        // aqui é anômalo: tratado como evento de segurança, não como erro de validação.
        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException(
                "Sync de políticas sem tenant resolvido no contexto (fail-closed).");

        // Publica e retorna imediatamente — o request não espera o fetch/registro das políticas.
        await _policySync.RequestSyncAsync(tenantId, ct);

        return Accepted(new PolicySyncAcceptedDto(
            tenantId, "Queued",
            "Sincronização de políticas agendada; acompanhe em GET /api/v1/governance/documents."));
    }

    /// <summary>Lista os documentos do tenant (filtros por tipo e status de leitura da IA).</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<GovernanceDocumentDto>>> List(
        [FromQuery] GovernanceDocumentType? type,
        [FromQuery] AiAnalysisStatus? analysisStatus,
        CancellationToken ct)
    {
        var q = _db.GovernanceDocuments.Include(d => d.ControlMappings).AsNoTracking().AsQueryable();
        if (type is { } t) q = q.Where(d => d.Type == t);
        if (analysisStatus is { } s) q = q.Where(d => d.AnalysisStatus == s);

        var docs = await q.OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
        return docs.Select(ToDto).ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<GovernanceDocumentDto>> Get(Guid id, CancellationToken ct)
    {
        var doc = await _db.GovernanceDocuments.Include(d => d.ControlMappings).AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct);
        return doc is null ? NotFound() : ToDto(doc);
    }

    /// <summary>Re-enfileira a leitura da IA (após anexar binário de integração ou reprocessar).</summary>
    [HttpPost("{id:guid}/reanalyze")]
    public async Task<ActionResult<DocumentAcceptedDto>> Reanalyze(Guid id, CancellationToken ct)
    {
        var doc = await _db.GovernanceDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return NotFound();
        if (doc.StorageUri is null) return BadRequest("Documento sem binário armazenado para ler.");

        doc.AnalysisStatus = AiAnalysisStatus.Queued;
        doc.AnalysisQueuedAt = DateTimeOffset.UtcNow;
        doc.AnalysisError = null;
        await _db.SaveChangesAsync(ct);

        await _queue.EnqueueAsync(doc.Id, ct);
        return Accepted(new DocumentAcceptedDto(doc.Id, doc.AnalysisStatus.ToString()));
    }

    /// <summary>Human-in-the-loop: analista confirma/ajusta um mapeamento sugerido pela IA.</summary>
    [HttpPut("{id:guid}/mappings/{code}")]
    public async Task<IActionResult> ConfirmMapping(Guid id, string code, ConfirmMappingRequest req, CancellationToken ct)
    {
        var mapping = await _db.DocumentControlMappings
            .FirstOrDefaultAsync(m => m.GovernanceDocumentId == id && m.SubcategoryCode == code, ct);
        if (mapping is null) return NotFound();

        mapping.AnalystConfirmed = req.Confirmed;
        if (req.Confidence is { } c) mapping.Confidence = c;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var doc = await _db.GovernanceDocuments.Include(d => d.ControlMappings)
            .FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return NotFound();

        if (doc.StorageUri is not null) await _storage.DeleteAsync(doc.StorageUri, ct);
        _db.DocumentControlMappings.RemoveRange(doc.ControlMappings);
        _db.GovernanceDocuments.Remove(doc);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static GovernanceDocumentDto ToDto(GovernanceDocument d) => new(
        d.Id, d.Title, d.Type.ToString(), d.Source.ToString(), d.SourceReference,
        d.FileName, d.ContentType, d.FileSizeBytes, d.Sha256, d.DocumentDate,
        d.Status.ToString(), d.AnalysisStatus.ToString(), d.AnalysisSummary, d.AnalysisError, d.AnalyzedAt,
        d.ControlMappings.OrderByDescending(m => m.Confidence)
            .Select(m => new DocumentMappingDto(m.SubcategoryCode, m.Confidence, m.Evidence, m.AnalystConfirmed))
            .ToList());
}
