using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MiniRag.Api.Options;

namespace MiniRag.Api.Infrastructure;

/// <summary>Thin client over the Ollama REST API (https://github.com/ollama/ollama/blob/main/docs/api.md).</summary>
public sealed class OllamaClient(HttpClient http, IOptions<OllamaOptions> options)
{
    private readonly OllamaOptions _options = options.Value;

    public string ChatModel => _options.ChatModel;
    public string EmbeddingModel => _options.EmbeddingModel;

    /// <summary>Model names are case-insensitive and a missing tag means ":latest".</summary>
    public static bool HasModel(IEnumerable<string> available, string model)
    {
        static string Canonical(string name) =>
            (name.Contains(':') ? name : name + ":latest").ToLowerInvariant();

        var wanted = Canonical(model);
        return available.Any(a => Canonical(a) == wanted);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        var response = await http.GetFromJsonAsync<TagsResponse>("api/tags", ct);
        return response?.Models.Select(m => m.Name).ToList() ?? [];
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var request = new EmbedRequest(_options.EmbeddingModel, inputs);
        using var response = await http.PostAsJsonAsync("api/embed", request, ct);
        await EnsureSuccessAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct)
                   ?? throw new InvalidOperationException("Empty embedding response from Ollama.");
        if (body.Embeddings.Length != inputs.Count)
            throw new InvalidOperationException(
                $"Ollama returned {body.Embeddings.Length} embeddings for {inputs.Count} inputs.");
        return body.Embeddings;
    }

    public async Task<string> ChatAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var request = new ChatRequest(
            _options.ChatModel,
            [new ChatMessage("system", systemPrompt), new ChatMessage("user", userPrompt)],
            Stream: false,
            Think: _options.EnableThinking,
            Options: new ChatOptions(_options.Temperature));

        using var response = await http.PostAsJsonAsync("api/chat", request, ct);
        await EnsureSuccessAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(ct);
        return body?.Message?.Content ?? string.Empty;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var error = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Ollama returned {(int)response.StatusCode}: {error}");
    }

    private sealed record TagsResponse([property: JsonPropertyName("models")] List<TagModel> Models);
    private sealed record TagModel([property: JsonPropertyName("name")] string Name);

    private sealed record EmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

    private sealed record EmbedResponse([property: JsonPropertyName("embeddings")] float[][] Embeddings);

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("think")] bool Think,
        [property: JsonPropertyName("options")] ChatOptions Options);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatOptions([property: JsonPropertyName("temperature")] double Temperature);

    private sealed record ChatResponse([property: JsonPropertyName("message")] ChatMessage? Message);
}
