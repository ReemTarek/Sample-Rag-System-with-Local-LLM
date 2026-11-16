using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiniRag.Api.Infrastructure;
using MiniRag.Api.Options;
using MiniRag.Api.Services;

namespace MiniRag.Tests;

/// <summary>The real service graph wired to in-memory fakes of the Ollama and Qdrant HTTP APIs.</summary>
internal sealed class TestHost : IDisposable
{
    public string UploadDir { get; } = Path.Combine(Path.GetTempPath(), "minirag-tests-" + Guid.NewGuid().ToString("N"));
    public FakeBackends Backends { get; } = new();
    public ServiceProvider Services { get; }

    public TestHost()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IOptions<OllamaOptions>>(Microsoft.Extensions.Options.Options.Create(new OllamaOptions()));
        services.AddSingleton<IOptions<QdrantOptions>>(Microsoft.Extensions.Options.Options.Create(new QdrantOptions()));
        services.AddSingleton<IOptions<RagOptions>>(Microsoft.Extensions.Options.Options.Create(
            // 10 tokens ≈ 40 chars per chunk, so a ~2400 char file gives well over one 16-chunk embedding batch.
            new RagOptions { ChunkSizeTokens = 10, ChunkOverlapTokens = 2, UploadPath = UploadDir, MinScore = 0.0 }));
        services.AddHttpClient<OllamaClient>(c => c.BaseAddress = new Uri("http://ollama.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => Backends.Ollama);
        services.AddHttpClient<QdrantVectorStore>(c => c.BaseAddress = new Uri("http://qdrant.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => Backends.Qdrant);
        services.AddSingleton<DocumentRegistry>();
        services.AddSingleton<DocumentTextExtractor>();
        services.AddSingleton<VectorIndexInitializer>();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddSingleton<DocumentIndexingService>();
        services.AddTransient<RagService>();
        Services = services.BuildServiceProvider();
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public void Dispose()
    {
        Services.Dispose();
        if (Directory.Exists(UploadDir))
            Directory.Delete(UploadDir, recursive: true);
    }
}

internal sealed class FakeLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}

/// <summary>Minimal in-memory stand-ins for the endpoints the app touches. Failures are switched on per test.</summary>
internal sealed class FakeBackends
{
    private int _embedCalls;
    private int _storedPoints;
    private readonly List<string> _deleted = [];

    /// <summary>1-based /api/embed call that returns HTTP 500 (call 1 is the startup probe).</summary>
    public int FailEmbedCallNumber { get; set; }

    /// <summary>1-based /api/embed call that behaves like an HttpClient timeout (TaskCanceledException).</summary>
    public int TimeoutEmbedCallNumber { get; set; }

    public bool TimeoutOnTags { get; set; }
    public bool FailQdrantDelete { get; set; }

    public int StoredPoints => _storedPoints;
    public IReadOnlyList<string> DeletedDocumentIds { get { lock (_deleted) return _deleted.ToList(); } }

    public HttpMessageHandler Ollama => new Handler(OllamaRequest);
    public HttpMessageHandler Qdrant => new Handler(QdrantRequest);

    private HttpResponseMessage OllamaRequest(HttpRequestMessage request, string body)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/tags")
        {
            if (TimeoutOnTags)
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
            return Json("""{"models":[{"name":"nomic-embed-text:latest"},{"name":"qwen3:8b"}]}""");
        }

        if (path == "/api/embed")
        {
            var call = Interlocked.Increment(ref _embedCalls);
            if (call == TimeoutEmbedCallNumber)
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
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
        if (path == "/collections/documents/points/search")
            return Json("""{"result":[{"score":0.9,"payload":{"documentId":"d","documentName":"a.txt","pageNumber":null,"chunkIndex":0,"text":"hello"}}]}""");
        if (path == "/collections/documents/points/delete")
        {
            if (FailQdrantDelete)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") };
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
