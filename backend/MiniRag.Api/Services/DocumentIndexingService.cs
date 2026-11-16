using System.Diagnostics;
using Microsoft.Extensions.Options;
using MiniRag.Api.Infrastructure;
using MiniRag.Api.Models;
using MiniRag.Api.Options;

namespace MiniRag.Api.Services;

/// <summary>Extract → chunk → embed → store, run in the background after upload. One document at a time.</summary>
public sealed class DocumentIndexingService(
    IServiceScopeFactory scopeFactory,
    DocumentRegistry registry,
    VectorIndexInitializer initializer,
    IOptions<RagOptions> ragOptions,
    IOptions<OllamaOptions> ollamaOptions,
    IHostApplicationLifetime lifetime,
    ILogger<DocumentIndexingService> logger)
{
    private const int EmbeddingBatchSize = 16;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public string UploadDirectory { get; } = Path.GetFullPath(ragOptions.Value.UploadPath);

    public string FilePathFor(string documentId, string fileName) =>
        Path.Combine(UploadDirectory, documentId + Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>Fire-and-forget: status is reported through the <see cref="DocumentRegistry"/>.</summary>
    public void QueueIndexing(string documentId, string fileName) =>
        _ = Task.Run(() => IndexAsync(documentId, fileName, lifetime.ApplicationStopping));

    private async Task IndexAsync(string documentId, string fileName, CancellationToken ct)
    {
        await _oneAtATime.WaitAsync(ct);
        try
        {
            if (registry.Get(documentId) is null)
                return; // deleted while queued

            registry.MarkIndexing(documentId);
            var stopwatch = Stopwatch.StartNew();
            await initializer.EnsureReadyAsync(ct);

            using var scope = scopeFactory.CreateScope();
            var extractor = scope.ServiceProvider.GetRequiredService<DocumentTextExtractor>();
            var ollama = scope.ServiceProvider.GetRequiredService<OllamaClient>();
            var store = scope.ServiceProvider.GetRequiredService<QdrantVectorStore>();

            IReadOnlyList<PageText> pages;
            await using (var file = File.OpenRead(FilePathFor(documentId, fileName)))
                pages = extractor.Extract(file, fileName);

            var rag = ragOptions.Value;
            var chunks = new TextChunker(rag.ChunkSizeTokens, rag.ChunkOverlapTokens).Chunk(pages);
            if (chunks.Count == 0)
            {
                registry.MarkFailed(documentId,
                    "No extractable text (the file is empty, or a scanned PDF without a text layer).");
                return;
            }

            var prefix = ollamaOptions.Value.DocumentPrefix;
            foreach (var batch in chunks.Chunk(EmbeddingBatchSize))
            {
                var vectors = await ollama.EmbedAsync(batch.Select(c => prefix + c.Text).ToList(), ct);
                var points = batch.Select((c, i) =>
                    (new ChunkPayload(documentId, fileName, c.PageNumber, c.ChunkIndex, c.Text), vectors[i])).ToList();
                await store.UpsertAsync(points, ct);
            }

            if (registry.Get(documentId) is null)
            {
                // Deleted while indexing: remove what we just stored.
                await store.DeleteDocumentAsync(documentId, ct);
                return;
            }

            registry.MarkIndexed(documentId, chunks.Count);
            logger.LogInformation("Indexed {File}: {Pages} page(s), {Chunks} chunk(s) in {Elapsed} ms",
                fileName, pages.Count, chunks.Count, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            registry.MarkFailed(documentId, "Indexing cancelled because the server is shutting down.");
            await RemovePartialVectorsAsync(documentId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Indexing {File} failed", fileName);
            registry.MarkFailed(documentId, ex.Message);
            await RemovePartialVectorsAsync(documentId);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// Batches are stored as they are embedded, so a failure part-way leaves some chunks in Qdrant. Remove
    /// them, otherwise a "Failed" document stays searchable and reappears as "Indexed" after a restart.
    /// Best effort: if Qdrant itself is the problem the cleanup fails too, and we only log it.
    /// </summary>
    private async Task RemovePartialVectorsAsync(string documentId)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<QdrantVectorStore>();
            await store.DeleteDocumentAsync(documentId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove partially indexed vectors for document {DocumentId}", documentId);
        }
    }
}
