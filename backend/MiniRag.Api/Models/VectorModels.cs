namespace MiniRag.Api.Models;

/// <summary>Metadata stored with each vector in Qdrant.</summary>
public sealed record ChunkPayload(string DocumentId, string DocumentName, int? PageNumber, int ChunkIndex, string Text);

public sealed record SearchHit(ChunkPayload Payload, double Score);

public sealed record StoredDocument(string Id, string Name, int ChunkCount);
