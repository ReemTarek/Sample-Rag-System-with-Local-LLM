using MiniRag.Api.Infrastructure;

namespace MiniRag.Tests;

public class OllamaClientTests
{
    private static readonly string[] Available = ["nomic-embed-text:latest", "qwen3:8b"];

    [Theory]
    [InlineData("nomic-embed-text", true)]          // implicit :latest
    [InlineData("nomic-embed-text:latest", true)]
    [InlineData("qwen3:8b", true)]
    [InlineData("QWEN3:8B", true)]
    [InlineData("qwen3", false)]                   // qwen3 means qwen3:latest, which is not pulled
    [InlineData("llama3", false)]
    public void HasModel_MatchesLatestTag(string model, bool expected)
    {
        Assert.Equal(expected, OllamaClient.HasModel(Available, model));
    }
}
