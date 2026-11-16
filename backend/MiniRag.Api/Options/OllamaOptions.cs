namespace MiniRag.Api.Options;

public sealed class OllamaOptions
{
    public const string Section = "Ollama";

    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string ChatModel { get; set; } = "qwen3:8b";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>qwen3 "thinking" mode. Off by default: much faster, and RAG answers rarely benefit.</summary>
    public bool EnableThinking { get; set; }

    /// <summary>nomic-embed-text expects task prefixes; set both to empty for models that don't.</summary>
    public string DocumentPrefix { get; set; } = "search_document: ";
    public string QueryPrefix { get; set; } = "search_query: ";

    public double Temperature { get; set; } = 0.1;
    public int TimeoutSeconds { get; set; } = 300;
}
