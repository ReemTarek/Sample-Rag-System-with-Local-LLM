namespace MiniRag.Api.Models;

/// <summary>Text of one page. PageNumber is null for formats without reliable pages (TXT, DOCX).</summary>
public sealed record PageText(int? PageNumber, string Text);

public sealed record TextChunk(int ChunkIndex, int? PageNumber, string Text);
