using System.Text.RegularExpressions;
using MiniRag.Api.Models;

namespace MiniRag.Api.Services;

/// <summary>
/// Sliding-window chunker. Token counts are approximated as 4 characters per token, which is
/// close enough for English text with nomic-embed-text / qwen3 tokenizers and avoids a
/// tokenizer dependency. Chunks never cross page boundaries so citations stay accurate.
/// </summary>
public sealed partial class TextChunker
{
    public const int CharsPerToken = 4;

    private readonly int _maxChars;
    private readonly int _overlapChars;

    public TextChunker(int chunkSizeTokens, int overlapTokens)
    {
        if (chunkSizeTokens <= 0)
            throw new ArgumentException("Chunk size must be positive.", nameof(chunkSizeTokens));
        if (overlapTokens < 0 || overlapTokens >= chunkSizeTokens)
            throw new ArgumentException("Chunk overlap must be >= 0 and smaller than the chunk size.", nameof(overlapTokens));

        _maxChars = chunkSizeTokens * CharsPerToken;
        _overlapChars = overlapTokens * CharsPerToken;
    }

    public IReadOnlyList<TextChunk> Chunk(IReadOnlyList<PageText> pages)
    {
        var chunks = new List<TextChunk>();
        foreach (var page in pages)
        {
            foreach (var text in SplitPage(Normalize(page.Text)))
                chunks.Add(new TextChunk(chunks.Count, page.PageNumber, text));
        }
        return chunks;
    }

    private IEnumerable<string> SplitPage(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + _maxChars, text.Length);
            if (end < text.Length)
                end = FindBreak(text, start, end);

            var chunk = text[start..end].Trim();
            if (chunk.Length > 0)
                yield return chunk;

            if (end >= text.Length)
                yield break;

            start = NextStart(text, start, end);
        }
    }

    /// <summary>Best break position in the second half of the window: paragraph, sentence, then word.</summary>
    private static int FindBreak(string text, int start, int end)
    {
        var minEnd = start + (end - start) / 2;
        var window = text.AsSpan(minEnd, end - minEnd + 1); // includes text[end] so a boundary right at `end` counts

        var paragraph = window.LastIndexOf("\n\n");
        if (paragraph >= 0)
            return minEnd + paragraph;

        for (var i = window.Length - 2; i >= 0; i--)
        {
            if (window[i] is '.' or '?' or '!' && char.IsWhiteSpace(window[i + 1]))
                return minEnd + i + 1;
        }

        var space = window.LastIndexOfAny(' ', '\n');
        return space >= 0 ? minEnd + space : end; // no whitespace at all: hard cut
    }

    /// <summary>Step back by the overlap, then snap forward to the start of a word.</summary>
    private int NextStart(string text, int start, int end)
    {
        var next = end - _overlapChars;
        if (next <= start)
            return end;

        if (!char.IsWhiteSpace(text[next - 1]))
        {
            var ws = text.IndexOfAny([' ', '\n'], next, end - next);
            next = ws < 0 ? end : ws + 1;
        }
        while (next < text.Length && char.IsWhiteSpace(text[next]))
            next++;
        return next;
    }

    private static string Normalize(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = HorizontalWhitespace().Replace(text, " ");
        text = SpaceAroundNewline().Replace(text, "\n");
        text = ExtraNewlines().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"[ \t\f\v ]+")]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@" ?\n ?")]
    private static partial Regex SpaceAroundNewline();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraNewlines();
}
