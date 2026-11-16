using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MiniRag.Api.Models;
using MiniRag.Api.Options;

namespace MiniRag.Api.Infrastructure;

/// <summary>Qdrant REST API client (https://api.qdrant.tech/api-reference).</summary>
public sealed class QdrantVectorStore(HttpClient http, IOptions<QdrantOptions> options)
{
    private readonly string _collection = options.Value.Collection;

    private string CollectionPath => $"collections/{Uri.EscapeDataString(_collection)}";

    public string CollectionName => _collection;

    /// <summary>Creates the collection if missing; fails if it exists with a different vector size.</summary>
    public async Task EnsureCollectionAsync(int dimension, CancellationToken ct)
    {
        using var existing = await http.GetAsync(CollectionPath, ct);
        if (existing.IsSuccessStatusCode)
        {
            var json = await existing.Content.ReadFromJsonAsync<JsonNode>(ct);
            var size = json?["result"]?["config"]?["params"]?["vectors"]?["size"]?.GetValue<int>();
            if (size != dimension)
                throw new InvalidOperationException(
                    $"Qdrant collection '{_collection}' has vector size {size}, but the embedding model produces " +
                    $"{dimension}. Delete the collection (or set Qdrant:Collection to a new name) and re-upload documents.");
            return;
        }
        if (existing.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(existing, ct);

        await SendAsync(HttpMethod.Put, CollectionPath,
            new { vectors = new { size = dimension, distance = "Cosine" } }, ct);
        await SendAsync(HttpMethod.Put, $"{CollectionPath}/index?wait=true",
            new { field_name = "documentId", field_schema = "keyword" }, ct);
    }

    public async Task UpsertAsync(IReadOnlyList<(ChunkPayload Payload, float[] Vector)> points, CancellationToken ct)
    {
        var body = new
        {
            points = points.Select(p => new
            {
                id = Guid.NewGuid(),
                vector = p.Vector,
                payload = ToPayload(p.Payload),
            }),
        };
        await SendAsync(HttpMethod.Put, $"{CollectionPath}/points?wait=true", body, ct);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int topK, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, $"{CollectionPath}/points/search",
            new { vector, limit = topK, with_payload = true }, ct);

        return json["result"]!.AsArray()
            .Select(p => new SearchHit(FromPayload(p!["payload"]!), p["score"]!.GetValue<double>()))
            .ToList();
    }

    public Task DeleteDocumentAsync(string documentId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{CollectionPath}/points/delete?wait=true",
            new { filter = DocumentFilter(documentId) }, ct);

    /// <summary>Scrolls all points (payload only) and groups them by document.</summary>
    public async Task<IReadOnlyList<StoredDocument>> ListDocumentsAsync(CancellationToken ct)
    {
        var counts = new Dictionary<string, (string Name, int Count)>();
        JsonNode? offset = null;
        do
        {
            var json = await SendAsync(HttpMethod.Post, $"{CollectionPath}/points/scroll", new
            {
                limit = 1000,
                offset,
                with_payload = new[] { "documentId", "documentName" },
                with_vector = false,
            }, ct);

            foreach (var point in json["result"]!["points"]!.AsArray())
            {
                var id = point!["payload"]!["documentId"]!.GetValue<string>();
                var name = point["payload"]!["documentName"]!.GetValue<string>();
                counts[id] = (name, counts.TryGetValue(id, out var c) ? c.Count + 1 : 1);
            }
            offset = json["result"]!["next_page_offset"]?.DeepClone();
        } while (offset is not null);

        return counts.Select(kv => new StoredDocument(kv.Key, kv.Value.Name, kv.Value.Count)).ToList();
    }

    private static object DocumentFilter(string documentId) =>
        new { must = new[] { new { key = "documentId", match = new { value = documentId } } } };

    private static Dictionary<string, object?> ToPayload(ChunkPayload p) => new()
    {
        ["documentId"] = p.DocumentId,
        ["documentName"] = p.DocumentName,
        ["pageNumber"] = p.PageNumber,
        ["chunkIndex"] = p.ChunkIndex,
        ["text"] = p.Text,
    };

    private static ChunkPayload FromPayload(JsonNode p) => new(
        p["documentId"]!.GetValue<string>(),
        p["documentName"]!.GetValue<string>(),
        p["pageNumber"]?.GetValue<int>(),
        p["chunkIndex"]!.GetValue<int>(),
        p["text"]!.GetValue<string>());

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<JsonNode>(ct) ?? new JsonObject();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var error = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Qdrant returned {(int)response.StatusCode}: {error}");
    }
}
