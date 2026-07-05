using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Documents;

/// <summary>Armazena documentos no disco local sob uma raiz configurável (dev/on-prem).</summary>
public sealed class LocalDocumentStorage : IDocumentStorage
{
    private readonly string _root;
    public LocalDocumentStorage(string root) => _root = root;

    public async Task<string> SaveAsync(Guid tenantId, Guid documentId, string fileName, Stream content, CancellationToken ct)
    {
        var dir = Path.Combine(_root, tenantId.ToString("N"));
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, $"{documentId:N}{Path.GetExtension(fileName)}");
        await using var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None);
        content.Position = 0;
        await content.CopyToAsync(fs, ct);
        return full;
    }

    public Task<Stream> OpenAsync(string storageUri, CancellationToken ct)
        => Task.FromResult<Stream>(new FileStream(storageUri, FileMode.Open, FileAccess.Read, FileShare.Read));

    public Task DeleteAsync(string storageUri, CancellationToken ct)
    {
        if (File.Exists(storageUri)) File.Delete(storageUri);
        return Task.CompletedTask;
    }
}
