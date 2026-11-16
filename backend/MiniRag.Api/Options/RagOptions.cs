namespace MiniRag.Api.Options;

public sealed class RagOptions
{
    public const string Section = "Rag";

    public int ChunkSizeTokens { get; set; } = 700;
    public int ChunkOverlapTokens { get; set; } = 100;
    public int TopK { get; set; } = 5;

    /// <summary>Cosine similarity below which a retrieved chunk is ignored.</summary>
    public double MinScore { get; set; } = 0.45;

    public int MaxUploadMb { get; set; } = 50;
    public string UploadPath { get; set; } = "uploads";
}
