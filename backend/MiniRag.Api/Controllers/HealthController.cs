using Microsoft.AspNetCore.Mvc;
using MiniRag.Api.Infrastructure;
using MiniRag.Api.Services;

namespace MiniRag.Api.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController(OllamaClient ollama, QdrantVectorStore store, VectorIndexInitializer initializer)
    : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var ollamaReachable = false;
        var chatAvailable = false;
        var embedAvailable = false;
        try
        {
            var models = await ollama.ListModelsAsync(ct);
            ollamaReachable = true;
            chatAvailable = OllamaClient.HasModel(models, ollama.ChatModel);
            embedAvailable = OllamaClient.HasModel(models, ollama.EmbeddingModel);
        }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // HttpClient timeout

        // Also (re)tries initialization, so this endpoint doubles as "is the pipeline usable now?".
        try { await initializer.EnsureReadyAsync(ct); }
        catch (ServiceNotReadyException) { }

        return Ok(new
        {
            ready = initializer.IsReady,
            error = initializer.LastError,
            ollama = new
            {
                reachable = ollamaReachable,
                chatModel = ollama.ChatModel,
                chatModelAvailable = chatAvailable,
                embeddingModel = ollama.EmbeddingModel,
                embeddingModelAvailable = embedAvailable,
                embeddingDimension = initializer.EmbeddingDimension,
            },
            qdrant = new { reachable = initializer.IsReady, collection = store.CollectionName },
        });
    }
}
