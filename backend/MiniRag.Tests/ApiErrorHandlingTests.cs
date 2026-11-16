using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MiniRag.Api.Controllers;
using MiniRag.Api.Infrastructure;
using MiniRag.Api.Models;
using MiniRag.Api.Services;

namespace MiniRag.Tests;

/// <summary>Failure behavior of the controllers and the initializer, against the fake Ollama/Qdrant.</summary>
public class ApiErrorHandlingTests : IDisposable
{
    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    private DocumentsController Documents() => new(
        _host.Get<DocumentRegistry>(), _host.Get<DocumentIndexingService>(),
        _host.Get<QdrantVectorStore>(), _host.Get<VectorIndexInitializer>());

    private async Task<DocumentDto> AddIndexedDocumentAsync()
    {
        await _host.Get<VectorIndexInitializer>().EnsureReadyAsync(CancellationToken.None);
        var registry = _host.Get<DocumentRegistry>();
        var indexing = _host.Get<DocumentIndexingService>();
        var doc = registry.Add("a.txt");
        registry.MarkIndexed(doc.Id, 1);
        Directory.CreateDirectory(indexing.UploadDirectory);
        await File.WriteAllTextAsync(indexing.FilePathFor(doc.Id, doc.FileName), "hello");
        return registry.Get(doc.Id)!;
    }

    // --- delete -----------------------------------------------------------------------------

    [Fact]
    public async Task Delete_QdrantFails_KeepsDocumentAndReturns502()
    {
        var doc = await AddIndexedDocumentAsync();
        _host.Backends.FailQdrantDelete = true;

        var result = await Documents().Delete(doc.Id, CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.NotNull(_host.Get<DocumentRegistry>().Get(doc.Id)); // still listed, so the user can retry
    }

    [Fact]
    public async Task Delete_FileInUse_StillSucceeds()
    {
        var doc = await AddIndexedDocumentAsync();
        var path = _host.Get<DocumentIndexingService>().FilePathFor(doc.Id, doc.FileName);
        await using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); // like the indexer

        var result = await Documents().Delete(doc.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(_host.Get<DocumentRegistry>().Get(doc.Id));
        Assert.Equal([doc.Id], _host.Backends.DeletedDocumentIds);
    }

    // --- upload -----------------------------------------------------------------------------

    [Fact]
    public async Task Upload_CopyFails_LeavesNoStuckDocument()
    {
        await using var broken = new ThrowingStream(length: 10);
        var file = new FormFile(broken, 0, 10, "file", "a.txt");

        await Assert.ThrowsAsync<IOException>(() => Documents().Upload(file, CancellationToken.None));

        Assert.Empty(_host.Get<DocumentRegistry>().All());
    }

    // --- timeouts ---------------------------------------------------------------------------

    [Fact]
    public async Task Chat_OllamaTimesOut_Returns504()
    {
        _host.Backends.TimeoutEmbedCallNumber = 2; // 1 = startup probe, 2 = the question embedding
        var controller = new ChatController(_host.Get<RagService>());

        var result = await controller.Ask(new ChatRequestDto("anything?"), CancellationToken.None);

        Assert.Equal(StatusCodes.Status504GatewayTimeout, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task Initializer_Timeout_BecomesNotReadyWithMessage()
    {
        _host.Backends.TimeoutOnTags = true;
        var initializer = _host.Get<VectorIndexInitializer>();

        var ex = await Assert.ThrowsAsync<ServiceNotReadyException>(() => initializer.EnsureReadyAsync(CancellationToken.None));

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(initializer.IsReady);
    }

    [Fact]
    public async Task Health_OllamaTimesOut_ReportsUnreachableInsteadOfFailing()
    {
        _host.Backends.TimeoutOnTags = true;
        var controller = new HealthController(
            _host.Get<OllamaClient>(), _host.Get<QdrantVectorStore>(), _host.Get<VectorIndexInitializer>());

        var result = await controller.Get(CancellationToken.None);

        var body = Assert.IsType<OkObjectResult>(result).Value!;
        Assert.Equal(false, body.GetType().GetProperty("ready")!.GetValue(body));
    }

    private sealed class ThrowingStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => 0; set { } } // FormFile positions the stream before reading
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("client disconnected");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            throw new IOException("client disconnected");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
