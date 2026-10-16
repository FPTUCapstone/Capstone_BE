using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.PointsOfInterest.ExternalReviews;

namespace TripMate.Infrastructure.ExternalReviews;

/// <summary>
/// Translates one review with the Gemini interactions API (same request shape as the itinerary
/// explanation provider). The model only translates; the caller labels the result as machine translated.
/// </summary>
public sealed class GeminiReviewTranslator(
    HttpClient httpClient,
    IOptions<ReviewTranslationOptions> options,
    IMemoryCache cache,
    ILogger<GeminiReviewTranslator> logger) : IReviewTranslator
{
    private const string SystemInstruction =
        "You translate traveler reviews. Translate the given title and text into the requested language. "
        + "Keep the meaning, tone and any numbers or names. Do not add, remove, summarize or comment. "
        + "If a field is empty, return an empty string for it.";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ReviewTranslationOptions _options = options.Value;

    public bool IsEnabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<Result<TranslatedReview>> TranslateAsync(
        string cacheKey,
        string? title,
        string? text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var key = $"review-translation:{targetLanguage}:{cacheKey}";
        if (cache.TryGetValue(key, out TranslatedReview? cached) && cached is not null)
        {
            return Result.Success(cached);
        }

        if (!IsEnabled)
        {
            return Unavailable();
        }

        var input = JsonSerializer.Serialize(new { targetLanguage, title = title ?? string.Empty, text = text ?? string.Empty });
        var payload = new InteractionRequest(
            _options.ModelName,
            input,
            SystemInstruction,
            Store: false,
            new ResponseFormat("text", "application/json", new JsonSchema(
                "object",
                new Dictionary<string, JsonSchema>
                {
                    ["title"] = new("string"),
                    ["text"] = new("string"),
                },
                ["title", "text"],
                false)));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 60)));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = JsonContent.Create(payload, options: SerializerOptions),
            };
            request.Headers.Add("x-goog-api-key", _options.ApiKey);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Review translation failed with status {StatusCode}.", (int)response.StatusCode);
                return Unavailable();
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var translated = Parse(body);
            if (translated is null)
            {
                logger.LogWarning("Review translation returned an unexpected response.");
                return Unavailable();
            }

            cache.Set(key, translated, TimeSpan.FromMinutes(Math.Clamp(_options.CacheMinutes, 1, 7 * 24 * 60)));
            return Result.Success(translated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Review translation timed out.");
            return Unavailable();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            logger.LogWarning("Review translation failed: {ExceptionType}.", exception.GetType().Name);
            return Unavailable();
        }
    }

    internal static TranslatedReview? Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "completed"
                || !root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var output = steps.EnumerateArray()
                .Where(step => step.TryGetProperty("type", out var type) && type.GetString() == "model_output")
                .SelectMany(step => step.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                    ? content.EnumerateArray()
                    : Enumerable.Empty<JsonElement>())
                .Where(content => content.TryGetProperty("type", out var type) && type.GetString() == "text")
                .Select(content => content.GetProperty("text").GetString())
                .FirstOrDefault(textValue => !string.IsNullOrWhiteSpace(textValue));
            if (output is null)
            {
                return null;
            }

            using var translated = JsonDocument.Parse(output);
            var translatedTitle = translated.RootElement.GetProperty("title").GetString();
            var translatedText = translated.RootElement.GetProperty("text").GetString();
            return new TranslatedReview(
                string.IsNullOrWhiteSpace(translatedTitle) ? null : translatedTitle.Trim(),
                string.IsNullOrWhiteSpace(translatedText) ? null : translatedText.Trim());
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Result<TranslatedReview> Unavailable() => Result.Failure<TranslatedReview>(
        ExternalReviewErrorCodes.TranslationUnavailable,
        "Review translation is not available right now.");

    private sealed record InteractionRequest(
        string Model,
        string Input,
        [property: JsonPropertyName("system_instruction")] string SystemInstruction,
        bool Store,
        [property: JsonPropertyName("response_format")] ResponseFormat ResponseFormat);

    private sealed record ResponseFormat(
        string Type,
        [property: JsonPropertyName("mime_type")] string MimeType,
        JsonSchema Schema);

    private sealed record JsonSchema(
        string Type,
        IReadOnlyDictionary<string, JsonSchema>? Properties = null,
        IReadOnlyCollection<string>? Required = null,
        [property: JsonPropertyName("additionalProperties")] bool? AdditionalProperties = null);
}