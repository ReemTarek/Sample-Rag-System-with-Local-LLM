using System.Diagnostics;
using Microsoft.Extensions.Options;
using MiniRag.Api.Infrastructure;
using MiniRag.Api.Models;
using MiniRag.Api.Options;

namespace MiniRag.Api.Services;

/// <summary>Question → embedding → Qdrant top-K → Ollama chat → answer with citations.</summary>
public sealed class RagService(
    OllamaClient ollama,
    QdrantVectorStore store,
    VectorIndexInitializer initializer,
    IOptions<RagOptions> ragOptions,
    IOptions<OllamaOptions> ollamaOptions,
    ILogger<RagService> logger)
{
    public async Task<ChatResponseDto> AskAsync(string question, CancellationToken ct)
    {
        await initializer.EnsureReadyAsync(ct);
        var rag = ragOptions.Value;
        var stopwatch = Stopwatch.StartNew();

        var queryVector = (await ollama.EmbedAsync([ollamaOptions.Value.QueryPrefix + question], ct))[0];
        var hits = await store.SearchAsync(queryVector, rag.TopK, ct);
        var embedAndSearchMs = stopwatch.ElapsedMilliseconds;

        logger.LogInformation("Retrieved {Count} chunk(s) for \"{Question}\": scores [{Scores}]",
            hits.Count, question, string.Join(", ", hits.Select(h => h.Score.ToString("0.000"))));

        var relevant = hits.Where(h => h.Score >= rag.MinScore).ToList();
        if (relevant.Count == 0)
        {
            logger.LogInformation("No chunk above MinScore {MinScore}; answering without the LLM", rag.MinScore);
            return RagPrompt.Insufficient();
        }

        var raw = await ollama.ChatAsync(RagPrompt.SystemPrompt, RagPrompt.BuildUserPrompt(question, relevant), ct);
        logger.LogInformation("Answered in {Total} ms (embed+search {Search} ms, chat {Chat} ms)",
            stopwatch.ElapsedMilliseconds, embedAndSearchMs, stopwatch.ElapsedMilliseconds - embedAndSearchMs);

        return RagPrompt.ParseAnswer(raw, relevant);
    }
}
