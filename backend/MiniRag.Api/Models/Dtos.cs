using System.Text.Json.Serialization;

namespace MiniRag.Api.Models;

[JsonConverter(typeof(JsonStringEnumConverter<DocumentStatus>))]
public enum DocumentStatus { Pending, Indexing, Indexed, Failed }

public sealed record DocumentDto(
    string Id, string FileName, DocumentStatus Status, int ChunkCount, string? Error, DateTimeOffset UploadedAt);

public sealed record ChatRequestDto(string? Question);

public sealed record CitationDto(
    int SourceNumber, string DocumentName, int? PageNumber, int ChunkIndex, double Score, string Snippet);

public sealed record ChatResponseDto(string Answer, bool InsufficientContext, IReadOnlyList<CitationDto> Citations);
