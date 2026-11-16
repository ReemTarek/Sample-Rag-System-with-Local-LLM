using MiniRag.Api.Models;
using MiniRag.Api.Services;

namespace MiniRag.Tests;

public class RagPromptTests
{
    private static readonly IReadOnlyList<SearchHit> Sources =
    [
        new(new ChunkPayload("d1", "handbook.pdf", 2, 4, "Stipend is 450 EUR."), 0.81),
        new(new ChunkPayload("d1", "handbook.pdf", 3, 7, "Vacation is 27 days."), 0.77),
        new(new ChunkPayload("d2", "notes.txt", null, 0, "Unrelated text."), 0.52),
    ];

    [Fact]
    public void BuildUserPrompt_NumbersSourcesWithDocumentAndPage()
    {
        var prompt = RagPrompt.BuildUserPrompt("How many vacation days?", Sources);

        Assert.Contains("[1] (handbook.pdf, page 2)\nStipend is 450 EUR.", prompt);
        Assert.Contains("[2] (handbook.pdf, page 3)\nVacation is 27 days.", prompt);
        Assert.Contains("[3] (notes.txt)\nUnrelated text.", prompt);
        Assert.EndsWith("Question: How many vacation days?", prompt);
    }

    [Fact]
    public void ParseAnswer_CitesOnlyReferencedSources()
    {
        var result = RagPrompt.ParseAnswer("Employees get 27 vacation days [2].", Sources);

        Assert.False(result.InsufficientContext);
        Assert.Equal("Employees get 27 vacation days [2].", result.Answer);
        var citation = Assert.Single(result.Citations);
        Assert.Equal(2, citation.SourceNumber);
        Assert.Equal("handbook.pdf", citation.DocumentName);
        Assert.Equal(3, citation.PageNumber);
        Assert.Equal(7, citation.ChunkIndex);
        Assert.Equal(0.77, citation.Score);
        Assert.Equal("Vacation is 27 days.", citation.Snippet);
    }

    [Theory]
    [InlineData("A [1, 3].")]
    [InlineData("A [1][3].")]
    [InlineData("A [1] and [3] and again [1].")]
    public void ParseAnswer_HandlesCitationFormats(string answer)
    {
        var result = RagPrompt.ParseAnswer(answer, Sources);

        Assert.Equal([1, 3], result.Citations.Select(c => c.SourceNumber));
    }

    [Fact]
    public void ParseAnswer_IgnoresOutOfRangeMarkers()
    {
        var result = RagPrompt.ParseAnswer("See [9] and [2].", Sources);

        Assert.Equal([2], result.Citations.Select(c => c.SourceNumber));
    }

    [Fact]
    public void ParseAnswer_NoMarkers_CitesAllSources()
    {
        var result = RagPrompt.ParseAnswer("Employees get 27 vacation days.", Sources);

        Assert.Equal([1, 2, 3], result.Citations.Select(c => c.SourceNumber));
    }

    [Fact]
    public void ParseAnswer_InsufficientSentence_ReturnsNoCitations()
    {
        var raw = "I'm sorry. The uploaded documents do not contain sufficient information to answer this question.";

        var result = RagPrompt.ParseAnswer(raw, Sources);

        Assert.True(result.InsufficientContext);
        Assert.Equal(RagPrompt.InsufficientAnswer, result.Answer);
        Assert.Empty(result.Citations);
    }

    [Fact]
    public void ParseAnswer_StripsThinkBlock()
    {
        var result = RagPrompt.ParseAnswer("<think>\nLet me look at source 2.\n</think>\n\n27 days [2].", Sources);

        Assert.Equal("27 days [2].", result.Answer);
    }

    [Fact]
    public void Insufficient_HasExactSentenceAndNoCitations()
    {
        var result = RagPrompt.Insufficient();

        Assert.True(result.InsufficientContext);
        Assert.Equal("The uploaded documents do not contain sufficient information to answer this question.", result.Answer);
        Assert.Empty(result.Citations);
    }
}
