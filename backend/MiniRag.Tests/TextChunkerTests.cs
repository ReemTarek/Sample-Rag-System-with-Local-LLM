using MiniRag.Api.Models;
using MiniRag.Api.Services;

namespace MiniRag.Tests;

public class TextChunkerTests
{
    [Fact]
    public void Chunk_ShortText_ReturnsSingleChunkWithPage()
    {
        var chunker = new TextChunker(chunkSizeTokens: 700, overlapTokens: 100);

        var chunks = chunker.Chunk([new PageText(3, "Hello world.")]);

        var chunk = Assert.Single(chunks);
        Assert.Equal(0, chunk.ChunkIndex);
        Assert.Equal(3, chunk.PageNumber);
        Assert.Equal("Hello world.", chunk.Text);
    }

    [Fact]
    public void Chunk_LongText_RespectsSizeAndOverlap()
    {
        // 10 tokens ≈ 40 chars per chunk, 2 tokens ≈ 8 chars overlap.
        var chunker = new TextChunker(chunkSizeTokens: 10, overlapTokens: 2);
        var words = string.Join(' ', Enumerable.Range(1, 40).Select(i => $"w{i:00}")); // 159 chars

        var chunks = chunker.Chunk([new PageText(null, words)]);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 40, $"chunk too long: {c.Text.Length}"));
        for (var i = 0; i < chunks.Count - 1; i++)
        {
            // Overlap: the next chunk starts with a word that already appeared in the current chunk.
            var firstWordOfNext = chunks[i + 1].Text.Split(' ')[0];
            Assert.Contains(firstWordOfNext, chunks[i].Text.Split(' '));
        }
        // No content lost: every word appears in some chunk.
        Assert.All(words.Split(' '), w => Assert.Contains(chunks, c => c.Text.Split(' ').Contains(w)));
    }

    [Fact]
    public void Chunk_NeverSpansPages()
    {
        var chunker = new TextChunker(chunkSizeTokens: 10, overlapTokens: 2);
        var page1 = string.Join(' ', Enumerable.Repeat("alpha", 12));
        var page2 = string.Join(' ', Enumerable.Repeat("bravo", 12));

        var chunks = chunker.Chunk([new PageText(1, page1), new PageText(2, page2)]);

        Assert.All(chunks.Where(c => c.PageNumber == 1), c => Assert.DoesNotContain("bravo", c.Text));
        Assert.All(chunks.Where(c => c.PageNumber == 2), c => Assert.DoesNotContain("alpha", c.Text));
        Assert.Contains(chunks, c => c.PageNumber == 1);
        Assert.Contains(chunks, c => c.PageNumber == 2);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.ChunkIndex));
    }

    [Fact]
    public void Chunk_WhitespaceOnlyPage_Skipped()
    {
        var chunker = new TextChunker(700, 100);

        var chunks = chunker.Chunk([new PageText(1, "   \n\t ")]);

        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunk_PrefersSentenceBoundary()
    {
        // 10 tokens = 40 chars; the window ends mid-word, but a sentence ends at char 31.
        var chunker = new TextChunker(chunkSizeTokens: 10, overlapTokens: 1);
        var text = "The quick brown fox jumps over. The lazy dog sleeps all day long.";

        var chunks = chunker.Chunk([new PageText(null, text)]);

        Assert.Equal("The quick brown fox jumps over.", chunks[0].Text);
    }

    [Fact]
    public void Ctor_OverlapNotLessThanSize_Throws()
    {
        Assert.Throws<ArgumentException>(() => new TextChunker(100, 100));
    }
}
