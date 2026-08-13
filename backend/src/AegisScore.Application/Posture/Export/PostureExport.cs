using System;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Application.Posture.Export;

// [AEGIS-AUD-034 (+ parte de AEGIS-AUD-040)] Exportação executiva da fotografia IMUTÁVEL de postura. O relatório
// (PDF executivo e CSV completo) é derivado EXCLUSIVAMENTE da PostureSnapshot publicada e selecionada pelo id — o
// snapshot é a autoridade. NUNCA se recalcula a postura atual, nunca se consulta o ledger para reconstruir números
// e nunca se aceita score/cobertura/contagem/conteúdo vindos do cliente. Antes de gerar o arquivo, a integridade é
// reverificada recomputando o ContentHash (PostureSnapshotHasher). O tenant é SEMPRE implícito no contexto
// autenticado; a fotografia é lida pelo Global Query Filter fail-closed (sem IgnoreQueryFilters).

/// <summary>Formato de exportação suportado. Qualquer outro valor é recusado (400) antes de tocar a fotografia.</summary>
public enum PostureExportFormat
{
    /// <summary>Relatório executivo em PDF (pt-BR), determinístico e apresentável.</summary>
    Pdf = 0,

    /// <summary>Dados completos em CSV (UTF-8 com BOM, delimitador ';', protegido contra CSV/Formula Injection).</summary>
    Csv = 1,
}

/// <summary>Parsing tolerante e fechado do parâmetro <c>format</c> — só <c>pdf</c>/<c>csv</c> (case-insensitive).</summary>
public static class PostureExportFormats
{
    public static bool TryParse(string? value, out PostureExportFormat format)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "pdf": format = PostureExportFormat.Pdf; return true;
            case "csv": format = PostureExportFormat.Csv; return true;
            default: format = default; return false;
        }
    }
}

/// <summary>
/// Arquivo pronto para download: o conteúdo binário, o <see cref="ContentType"/> seguro e um <see cref="FileName"/>
/// já sanitizado (ASCII, sem separador de caminho). Não referencia nada persistido — a exportação não grava nada.
/// </summary>
/// <param name="Content">Bytes do arquivo (PDF ou CSV com BOM).</param>
/// <param name="ContentType">Media type do download (ex.: <c>application/pdf</c>, <c>text/csv; charset=utf-8</c>).</param>
/// <param name="FileName">Nome de arquivo seguro para o cabeçalho Content-Disposition.</param>
public sealed record PostureExportResult(byte[] Content, string ContentType, string FileName);

/// <summary>
/// Integridade da fotografia falhou: o <see cref="Domain.PostureSnapshot.ContentHash"/> recomputado diverge do
/// gravado. A exportação é ABORTADA (o controller responde 409) — nunca se emite um relatório sobre conteúdo
/// possivelmente adulterado. A mensagem é sanitizada; não expõe o conteúdo nem os dois hashes.
/// </summary>
public sealed class PostureSnapshotIntegrityException : Exception
{
    public PostureSnapshotIntegrityException(string message) : base(message) { }
}

/// <summary>
/// Abstração PEQUENA e FOCADA de exportação da fotografia auditável de postura. Uma implementação: carrega a
/// fotografia do tenant ambiente (query filter fail-closed), reverifica o hash e renderiza o formato pedido. Não é
/// um framework de relatórios — sem templates configuráveis, filas, armazenamento ou geração assíncrona.
/// </summary>
public interface IPostureSnapshotExporter
{
    /// <summary>
    /// Exporta a fotografia <paramref name="snapshotId"/> no <paramref name="format"/> indicado.
    /// Devolve <c>null</c> quando a fotografia não existe ou é de outro tenant (o controller responde 404).
    /// Lança <see cref="PostureSnapshotIntegrityException"/> quando o hash não confere (409).
    /// </summary>
    Task<PostureExportResult?> ExportAsync(Guid snapshotId, PostureExportFormat format, CancellationToken ct = default);
}
