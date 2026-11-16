using MiniRag.Api.Infrastructure;

namespace MiniRag.Api.Services;

/// <summary>
/// Verifies the Ollama models are pulled, detects the embedding dimension with a probe request,
/// ensures the Qdrant collection exists with that dimension, and reloads indexed documents.
/// Runs at startup and is retried lazily on the next request if Ollama/Qdrant were down.
/// </summary>
public sealed class VectorIndexInitializer(
    IServiceScopeFactory scopeFactory,
    DocumentRegistry registry,
    ILogger<VectorIndexInitializer> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public int? EmbeddingDimension { get; private set; }
    public string? LastError { get; private set; }
    public bool IsReady => EmbeddingDimension is not null;

    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (IsReady)
            return;

        await _lock.WaitAsync(ct);
        try
        {
            if (IsReady)
                return;

            using var scope = scopeFactory.CreateScope();
            var ollama = scope.ServiceProvider.GetRequiredService<OllamaClient>();
            var store = scope.ServiceProvider.GetRequiredService<QdrantVectorStore>();

            var models = await ollama.ListModelsAsync(ct);
            var missing = new[] { ollama.EmbeddingModel, ollama.ChatModel }
                .Where(m => !OllamaClient.HasModel(models, m))
                .ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Ollama model(s) not found: {string.Join(", ", missing)}. " +
                    $"Pull them first: {string.Join(" && ", missing.Select(m => $"ollama pull {m}"))}");

            var probe = await ollama.EmbedAsync(["dimension probe"], ct);
            var dimension = probe[0].Length;
            logger.LogInformation("Embedding model {Model} produces {Dimension}-dimensional vectors",
                ollama.EmbeddingModel, dimension);

            await store.EnsureCollectionAsync(dimension, ct);
            var existing = await store.ListDocumentsAsync(ct);
            registry.LoadExisting(existing);
            logger.LogInformation("Qdrant collection {Collection} ready with {Count} indexed document(s)",
                store.CollectionName, existing.Count);

            EmbeddingDimension = dimension;
            LastError = null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) // a cancelled caller still propagates
        {
            LastError = ex switch
            {
                HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } =>
                    $"Cannot connect to a backing service: {ex.Message} Is Ollama running and Qdrant started (docker compose up -d)?",
                OperationCanceledException =>
                    "Timed out waiting for Ollama or Qdrant. Is the model still loading? Try again, or raise Ollama:TimeoutSeconds.",
                _ => ex.Message,
            };
            throw new ServiceNotReadyException(LastError, ex);
        }
        finally
        {
            _lock.Release();
        }
    }
}

public sealed class ServiceNotReadyException(string message, Exception inner) : Exception(message, inner);

/// <summary>Runs the initializer once at startup; failures are logged, not fatal.</summary>
public sealed class StartupInitializer(VectorIndexInitializer initializer, ILogger<StartupInitializer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await initializer.EnsureReadyAsync(stoppingToken);
        }
        catch (ServiceNotReadyException ex)
        {
            logger.LogError("RAG pipeline not ready: {Error}. It will be retried on the next request.", ex.Message);
        }
    }
}
