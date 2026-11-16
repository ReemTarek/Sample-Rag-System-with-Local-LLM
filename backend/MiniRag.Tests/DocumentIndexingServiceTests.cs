using System.Text;
using MiniRag.Api.Models;
using MiniRag.Api.Services;

namespace MiniRag.Tests;

/// <summary>Runs the real indexing pipeline against in-memory fakes of the Ollama and Qdrant HTTP APIs.</summary>
public class DocumentIndexingServiceTests : IDisposable
{
    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }

    private async Task<DocumentDto> UploadAndIndexAsync()
    {
        var indexing = _host.Get<DocumentIndexingService>();
        var registry = _host.Get<DocumentRegistry>();
        var doc = registry.Add("big.txt");
        Directory.CreateDirectory(indexing.UploadDirectory);
        var text = string.Join(' ', Enumerable.Range(1, 300).Select(i => $"word{i:000}")); // ~2400 chars
        await File.WriteAllTextAsync(indexing.FilePathFor(doc.Id, doc.FileName), text, Encoding.UTF8);

        indexing.QueueIndexing(doc.Id, doc.FileName);
        await WaitForAsync(() => registry.Get(doc.Id)!.Status is DocumentStatus.Indexed or DocumentStatus.Failed);
        return registry.Get(doc.Id)!;
    }

    [Fact]
    public async Task Index_Success_MarksIndexedAndKeepsVectors()
    {
        var doc = await UploadAndIndexAsync();

        Assert.Equal(DocumentStatus.Indexed, doc.Status);
        Assert.True(doc.ChunkCount > 16, $"expected more than one embedding batch, got {doc.ChunkCount} chunks");
        Assert.Equal(doc.ChunkCount, _host.Backends.StoredPoints);
        Assert.Empty(_host.Backends.DeletedDocumentIds);
    }

    [Fact]
    public async Task Index_FailsAfterFirstBatch_RemovesAlreadyStoredVectors()
    {
        _host.Backends.FailEmbedCallNumber = 3; // 1 = startup probe, 2 = first batch (stored), 3 = second batch (fails)

        var doc = await UploadAndIndexAsync();

        Assert.Equal(DocumentStatus.Failed, doc.Status);
        Assert.True(_host.Backends.StoredPoints > 0, "the first batch should have been stored before the failure");
        Assert.Equal([doc.Id], _host.Backends.DeletedDocumentIds);
    }
}
