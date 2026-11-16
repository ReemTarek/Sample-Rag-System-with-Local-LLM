using MiniRag.Api.Models;
using MiniRag.Api.Services;

namespace MiniRag.Tests;

public class DocumentRegistryTests
{
    [Fact]
    public void Add_StartsPending()
    {
        var registry = new DocumentRegistry();

        var doc = registry.Add("handbook.pdf");

        Assert.Equal(DocumentStatus.Pending, doc.Status);
        Assert.Equal("handbook.pdf", doc.FileName);
        Assert.Equal(0, doc.ChunkCount);
        Assert.Same(doc, registry.Get(doc.Id));
    }

    [Fact]
    public void MarkIndexed_SetsChunkCount()
    {
        var registry = new DocumentRegistry();
        var doc = registry.Add("a.txt");

        registry.MarkIndexing(doc.Id);
        Assert.Equal(DocumentStatus.Indexing, registry.Get(doc.Id)!.Status);
        registry.MarkIndexed(doc.Id, 12);

        Assert.Equal(DocumentStatus.Indexed, registry.Get(doc.Id)!.Status);
        Assert.Equal(12, registry.Get(doc.Id)!.ChunkCount);
    }

    [Fact]
    public void MarkFailed_RecordsError()
    {
        var registry = new DocumentRegistry();
        var doc = registry.Add("a.pdf");

        registry.MarkFailed(doc.Id, "No extractable text");

        Assert.Equal(DocumentStatus.Failed, registry.Get(doc.Id)!.Status);
        Assert.Equal("No extractable text", registry.Get(doc.Id)!.Error);
    }

    [Fact]
    public void LoadExisting_AddsIndexedDocuments()
    {
        var registry = new DocumentRegistry();

        registry.LoadExisting([new StoredDocument("abc", "old.pdf", 5)]);

        var doc = Assert.Single(registry.All());
        Assert.Equal("abc", doc.Id);
        Assert.Equal(DocumentStatus.Indexed, doc.Status);
        Assert.Equal(5, doc.ChunkCount);
    }

    [Fact]
    public void Remove_DeletesDocument()
    {
        var registry = new DocumentRegistry();
        var doc = registry.Add("a.txt");

        Assert.True(registry.Remove(doc.Id));
        Assert.Null(registry.Get(doc.Id));
        Assert.False(registry.Remove(doc.Id));
    }
}
