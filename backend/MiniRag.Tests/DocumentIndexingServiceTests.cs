using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiniRag.Api.Infrastructure;
using MiniRag.Api.Models;
using MiniRag.Api.Options;
using MiniRag.Api.Services;

namespace MiniRag.Tests;

/// <summary>Runs the real indexing pipeline against in-memory fakes of the Ollama and Qdrant HTTP APIs.</summary>
public class DocumentIndexingServiceTests : IDisposable
{
    private readonly string _uploadDir = Path.Combine(Path.GetTempPath(), "minirag-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeBackends _backends = new();

    public void Dispose()
    {
        if (Directory.Exists(_uploadDir))
            Directory.Delete(_uploadDir, recursive: true);
    }

    private (DocumentIndexingService Indexing, DocumentRegistry Registry) Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IOptions<OllamaOptions>>(Microsoft.Extensions.Options.Options.Create(new OllamaOptions()));
        services.AddSingleton<IOptions<QdrantOptions>>(Microsoft.Extensions.Options.Options.Create(new QdrantOptions()));
        services.AddSingleton<IOptions<RagOptions>>(Microsoft.Extensions.Options.Options.Create(
            // 10 tokens ≈ 40 chars per chunk, so a ~1500 char file gives well over one 16-chunk embedding batch.
            new RagOptions { ChunkSizeTokens = 10, ChunkOverlapTokens = 2, UploadPath = _uploadDir }));
        services.AddHttpClient<OllamaClient>(c => c.BaseAddress = new Uri("http://ollama.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => _backends.Ollama);
        services.AddHttpClient<QdrantVectorStore>(c => c.BaseAddress = new Uri("http://qdrant.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => _backends.Qdrant);
        services.AddSingleton<DocumentRegistry>();
        services.AddSingleton<DocumentTextExtractor>();
        services.AddSingleton<VectorIndexInitializer>();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddSingleton<DocumentIndexingService>();

        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<DocumentIndexingService>(), sp.GetRequiredService<DocumentRegistry>());
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }

    private async Task<DocumentDto> UploadAndIndexAsync(DocumentIndexingService indexing, DocumentRegistry registry)
    {
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
        var (indexing, registry) = Build();

        var doc = await UploadAndIndexAsync(indexing, registry);

        Assert.Equal(DocumentStatus.Indexed, doc.Status);
        Assert.True(doc.ChunkCount > 16, $"expected more than one embedding batch, got {doc.ChunkCount} chunks");
        Assert.Equal(doc.ChunkCount, _backends.StoredPoints);
        Assert.Empty(_backends.DeletedDocumentIds);
    }

    [Fact]
    public async Task Index_FailsAfterFirstBatch_RemovesAlreadyStoredVectors()
    {
        var (indexing, registry) = Build();
        _backends.FailEmbedCallNumber = 3; // 1 = startup probe, 2 = first batch (stored), 3 = second batch (fails)

        var doc = await UploadAndIndexAsync(indexing, registry);

        Assert.Equal(DocumentStatus.Failed, doc.Status);
        Assert.True(_backends.StoredPoints > 0, "the first batch should have been stored before the failure");
        Assert.Equal([doc.Id], _backends.DeletedDocumentIds);
    }
}

internal sealed class FakeLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}

/// <summary>Minimal in-memory stand-ins for the endpoints the indexing pipeline touches.</summary>
internal sealed class FakeBackends
{
    private int _embedCalls;
    private int _storedPoints;
    private readonly List<string> _deleted = [];

    public int FailEmbedCallNumber { get; set; }
    public int StoredPoints => _storedPoints;
    public IReadOnlyList<string> DeletedDocumentIds { get { lock (_deleted) return _deleted.ToList(); } }

    public HttpMessageHandler Ollama => new Handler(OllamaRequest);
    public HttpMessageHandler Qdrant => new Handler(QdrantRequest);

    private HttpResponseMessage OllamaRequest(HttpRequestMessage request, string body)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/tags")
            return Json("""{"models":[{"name":"nomic-embed-text:latest"},{"name":"qwen3:8b"}]}""");

        if (path == "/api/embed")
        {
            var call = Interlocked.Increment(ref _embedCalls);
            if (call == FailEmbedCallNumber)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };

            var inputs = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("input").GetArrayLength();
            var vectors = string.Join(",", Enumerable.Repeat("[0.1,0.2,0.3]", inputs));
            return Json($$"""{"embeddings":[{{vectors}}]}""");
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage QdrantRequest(HttpRequestMessage request, string body)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/collections/documents")
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (path == "/collections/documents/points/scroll")
            return Json("""{"result":{"points":[],"next_page_offset":null}}""");
        if (path == "/collections/documents/points" && request.Method == HttpMethod.Put)
        {
            Interlocked.Add(ref _storedPoints, System.Text.Json.JsonDocument.Parse(body).RootElement
                .GetProperty("points").GetArrayLength());
            return Json("""{"result":{"status":"completed"}}""");
        }
        if (path == "/collections/documents/points/delete")
        {
            var id = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("filter").GetProperty("must")[0]
                .GetProperty("match").GetProperty("value").GetString()!;
            lock (_deleted) _deleted.Add(id);
            return Json("""{"result":{"status":"completed"}}""");
        }
        return Json("""{"result":true}"""); // create collection / create index
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
    }
}
