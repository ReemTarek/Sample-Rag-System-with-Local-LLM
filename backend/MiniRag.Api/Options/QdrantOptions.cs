namespace MiniRag.Api.Options;

public sealed class QdrantOptions
{
    public const string Section = "Qdrant";

    public string Url { get; set; } = "http://localhost:6333";
    public string Collection { get; set; } = "documents";
}
