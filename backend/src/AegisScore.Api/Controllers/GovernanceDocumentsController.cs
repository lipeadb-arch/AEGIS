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
    private readonly IPolicySyncQueue _policySync;
    private readonly ITenantContext _tenant;

    public GovernanceDocumentsController(
        AegisScoreDbContext db, IDocumentStorage storage,
        IPolicySyncQueue policySync, ITenantContext tenant)
    {
        _db = db;
        _storage = storage;
        _policySync = policySync;
        _tenant = tenant;
    }

    /// <summary>
    /// Upload manual: grava o binário + hash SHA-256 e deixa o documento em <c>Queued</c> — que já É a entrada
    /// na fila DURÁVEL de análise (AEGIS-AUD-050). O <c>DocumentAnalysisWorker</c> o adquire do banco; não há
    /// canal em memória para publicar.
    /// </summary>
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

        // Grava o binário e persiste o StorageUri: só então o documento fica ELEGÍVEL à aquisição durável
        // (a fila exige StorageUri IS NOT NULL). Persistir Queued É enfileirar — sem publicação em canal.
        doc.StorageUri = await _storage.SaveAsync(doc.TenantId, doc.Id, file.FileName, buffer, ct);
        await _db.SaveChangesAsync(ct);

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
    /// Gatilho MANUAL de sincronização das políticas (Govern) para usuários executivos: PERSISTE uma
    /// solicitação DURÁVEL de sync do tenant autenticado (AEGIS-AUD-050) e devolve 202. O
    /// <c>PolicyIngestionWorker</c> a adquire do banco com lease atômico e puxa as fontes externas
    /// (SharePoint, Google…) em background. Acompanhe o resultado por <c>GET /governance/documents</c>.
    /// </summary>
    /// <response code="202">Solicitação de sync persistida com sucesso; será processada em background pelo worker.</response>
    [HttpPost("sync")]
    public async Task<ActionResult<PolicySyncAcceptedDto>> Sync(CancellationToken ct)
    {
        // Tenant SEMPRE do contexto resolvido (claim tenant_id do JWT), NUNCA de input do cliente — o
        // mesmo guard fail-closed do resto da escrita. Sob [Authorize] + TenantConsistencyMiddleware, null
        // aqui é anômalo: tratado como evento de segurança, não como erro de validação.
        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException(
                "Sync de políticas sem tenant resolvido no contexto (fail-closed).");

        // O 202 só sai DEPOIS de a solicitação estar duravelmente salva (idempotente por tenant). Se a
        // persistência falhar, o await propaga e o cliente recebe erro — nunca um 202 sobre trabalho perdido.
        await _policySync.EnqueueAsync(tenantId, ct);

        return Accepted(new PolicySyncAcceptedDto(
            tenantId, "Queued",
            "Sincronização de políticas registrada; acompanhe em GET /api/v1/governance/documents."));
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

    /// <summary>
    /// Re-enfileira a leitura da IA (após anexar binário de integração ou reprocessar). Devolve o documento a
    /// <c>Queued</c> e ZERA o estado de lease/retry anterior — isso já É a entrada na fila DURÁVEL de análise
    /// (AEGIS-AUD-050), que o <c>DocumentAnalysisWorker</c> adquire do banco. Sem canal para publicar.
    /// </summary>
    [HttpPost("{id:guid}/reanalyze")]
    public async Task<ActionResult<DocumentAcceptedDto>> Reanalyze(Guid id, CancellationToken ct)
    {
        var doc = await _db.GovernanceDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return NotFound();
        if (doc.StorageUri is null) return BadRequest("Documento sem binário armazenado para ler.");

        doc.AnalysisStatus = AiAnalysisStatus.Queued;
        doc.AnalysisQueuedAt = DateTimeOffset.UtcNow;
        doc.AnalysisError = null;
        // Zera o lease/retry de uma execução anterior, para o worker adquirir o documento limpo.
        doc.AnalysisLeaseId = null;
        doc.AnalysisLeaseExpiresAt = null;
        doc.AnalysisAttempts = 0;
        doc.AnalysisNextAttemptAt = null;
        await _db.SaveChangesAsync(ct);

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
