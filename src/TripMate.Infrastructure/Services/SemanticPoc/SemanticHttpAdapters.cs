using System.Net.Http.Json;
using System.Text.Json;

using TripMate.Application.Features.Tours.SemanticPoc;

namespace TripMate.Infrastructure.Services.SemanticPoc;

public sealed class HttpEmbeddingService(HttpClient client) : IEmbeddingService
{
    public async Task<float[]> EmbedAsync(string text, bool passage, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("embed", new { text, kind = passage ? "passage" : "query" }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken) ?? throw new InvalidDataException("Empty embedding response.");
        if (result.ModelVersion != SemanticModel.Version || !SemanticModel.IsValidVector(result.Vector))
        {
            throw new InvalidDataException("Unexpected model or vector.");
        }
        return result.Vector;
    }

    private sealed record EmbeddingResponse(float[] Vector, string ModelVersion);
}

/// <summary>Dedicated immutable-during-query fixture collection; caller supplies bounded timeout HttpClient.</summary>
public sealed class QdrantTourVectorSearch : ITourVectorSearch
{
    private readonly HttpClient _client;
    private readonly string _collection;

    public QdrantTourVectorSearch(HttpClient client, string collection)
    {
        if (!collection.StartsWith("tm70_poc_", StringComparison.Ordinal) || collection.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            throw new ArgumentException("Only an isolated tm70_poc_ collection is allowed.", nameof(collection));
        }
        _client = client;
        _collection = collection;
    }

    public async Task<IReadOnlyList<SemanticCandidate>> SearchAllAsync(float[] vector, CancellationToken cancellationToken)
    {
        await ValidateCollectionAsync(cancellationToken);
        using var response = await _client.PostAsJsonAsync($"collections/{_collection}/points/query", new
        {
            query = vector,
            limit = SemanticModel.MaxCorpus + 1,
            @params = new { exact = true },
            with_payload = true,
            with_vector = false,
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        try
        {
            return body.RootElement.GetProperty("result").GetProperty("points").EnumerateArray().Select(point => new SemanticCandidate(
                point.GetProperty("id").GetInt64(), point.GetProperty("score").GetSingle(),
                point.GetProperty("payload").GetProperty("fingerprint").GetString()!,
                point.GetProperty("payload").GetProperty("model_version").GetString()!)).ToArray();
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Malformed Qdrant response.", exception);
        }
    }
    private async Task ValidateCollectionAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync($"collections/{_collection}", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        try
        {
            var config = document.RootElement.GetProperty("result").GetProperty("config").GetProperty("params").GetProperty("vectors");
            if (config.GetProperty("size").GetInt32() != SemanticModel.Dimensions || config.GetProperty("distance").GetString() != "Cosine")
            {
                throw new InvalidDataException("Collection must use 384-dimensional cosine vectors.");
            }
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Malformed collection configuration.", exception);
        }
    }
}