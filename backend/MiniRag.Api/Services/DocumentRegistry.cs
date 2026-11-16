using System.Collections.Concurrent;
using MiniRag.Api.Models;

namespace MiniRag.Api.Services;

/// <summary>
/// In-memory indexing status for uploaded documents. Indexed documents are reloaded from
/// Qdrant at startup; in-flight or failed uploads are lost on restart (acceptable for the MVP).
/// </summary>
public sealed class DocumentRegistry
{
    private readonly ConcurrentDictionary<string, DocumentDto> _documents = new();

    public DocumentDto Add(string fileName)
    {
        var doc = new DocumentDto(Guid.NewGuid().ToString("N"), fileName, DocumentStatus.Pending, 0, null,
            DateTimeOffset.UtcNow);
        _documents[doc.Id] = doc;
        return doc;
    }

    public DocumentDto? Get(string id) => _documents.GetValueOrDefault(id);

    public IReadOnlyList<DocumentDto> All() =>
        _documents.Values.OrderByDescending(d => d.UploadedAt).ThenBy(d => d.FileName).ToList();

    public void MarkIndexing(string id) => Update(id, d => d with { Status = DocumentStatus.Indexing });

    public void MarkIndexed(string id, int chunkCount) =>
        Update(id, d => d with { Status = DocumentStatus.Indexed, ChunkCount = chunkCount, Error = null });

    public void MarkFailed(string id, string error) =>
        Update(id, d => d with { Status = DocumentStatus.Failed, Error = error });

    public bool Remove(string id) => _documents.TryRemove(id, out _);

    public void LoadExisting(IEnumerable<StoredDocument> documents)
    {
        foreach (var d in documents)
            _documents.TryAdd(d.Id, new DocumentDto(d.Id, d.Name, DocumentStatus.Indexed, d.ChunkCount, null,
                DateTimeOffset.UtcNow));
    }

    private void Update(string id, Func<DocumentDto, DocumentDto> change)
    {
        // Ignore updates for documents deleted while indexing.
        while (_documents.TryGetValue(id, out var current))
        {
            if (_documents.TryUpdate(id, change(current), current))
                return;
        }
    }
}
