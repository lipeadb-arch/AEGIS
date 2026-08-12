using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Posture.Export;

/// <summary>
/// [AEGIS-AUD-034 (+ parte de AEGIS-AUD-040)] Exportador da fotografia auditável de postura. A fotografia
/// selecionada pelo id é a AUTORIDADE ÚNICA do relatório: PDF e CSV derivam EXCLUSIVAMENTE dela. Não recalcula a
/// postura atual, não consulta o ledger e não aceita números do cliente. O tenant é implícito — a leitura passa
/// pelo Global Query Filter fail-closed do <see cref="AegisScoreDbContext"/> (sem <c>IgnoreQueryFilters</c>), de
/// modo que a fotografia de outro tenant simplesmente não é encontrada (→ 404). A exportação é somente leitura:
/// não grava, não altera a fotografia. Antes de renderizar, o <see cref="PostureSnapshotHasher"/> RECOMPUTA e
/// valida o <see cref="PostureSnapshot.ContentHash"/>; divergência aborta com <see cref="PostureSnapshotIntegrityException"/>.
/// </summary>
public sealed class PostureSnapshotExporter : IPostureSnapshotExporter
{
    private readonly AegisScoreDbContext _db;

    public PostureSnapshotExporter(AegisScoreDbContext db) => _db = db;

    public async Task<PostureExportResult?> ExportAsync(
        Guid snapshotId, PostureExportFormat format, CancellationToken ct = default)
    {
        // Leitura tenant-scoped (query filter fail-closed) com os itens congelados. AsNoTracking: exportar não escreve.
        var snapshot = await _db.PostureSnapshots.AsNoTracking()
            .Include(s => s.Controls)
            .Include(s => s.Indicators)
            .FirstOrDefaultAsync(s => s.Id == snapshotId, ct);

        if (snapshot is null)
            return null;   // inexistente ou de outro tenant → o controller responde 404

        // Integridade REVERIFICADA: o relatório só sai sobre conteúdo íntegro. Mensagem sanitizada (sem hashes/conteúdo).
        if (!PostureSnapshotHasher.Verify(snapshot))
            throw new PostureSnapshotIntegrityException(
                "A integridade da fotografia não pôde ser confirmada (hash divergente); a exportação foi bloqueada.");

        return format switch
        {
            PostureExportFormat.Pdf => new PostureExportResult(
                PostureSnapshotPdfWriter.Write(snapshot), "application/pdf", FileName(snapshot, "pdf")),
            PostureExportFormat.Csv => new PostureExportResult(
                PostureSnapshotCsvWriter.Write(snapshot), "text/csv; charset=utf-8", FileName(snapshot, "csv")),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Formato de exportação desconhecido."),
        };
    }

    /// <summary>Nome de arquivo SEGURO (ASCII, sem separador de caminho): instrumento + id curto + instante da captura (UTC).</summary>
    private static string FileName(PostureSnapshot s, string extension)
    {
        var instrument = s.Type == PostureSnapshotType.Knight ? "knight" : "aegis-nist";
        var shortId = s.Id.ToString("N")[..8];
        var stamp = s.CapturedAt.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return $"aegis-postura-{instrument}-{shortId}-{stamp}.{extension}";
    }
}
