using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Infrastructure.Ai;

namespace TripMate.Infrastructure.AiRanking;

public sealed class HttpPoiRankingProvider(
    HttpClient httpClient,
    IOptions<PoiRankingProviderOptions> options,
    IAiProviderBudget? budget = null) : IPoiRankingProvider
{
    private const string SystemInstruction = """
        You are a semantic POI relevance scorer.

        Evaluate each candidate only against the supplied traveler preference tokens and candidate semantic metadata.
        Return one result per supplied POI.
        aiScore is semantic relevance from 0 to 1.
        reason is an optional concise semantic explanation.
        Do not perform itinerary scheduling.
        Do not infer or invent user identity.
        Do not use budget, distance, coordinates, behavior history or local TripMate scores because they are intentionally unavailable.
        """;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly GeminiJsonSchema ResponseSchema = CreateResponseSchema();

    private readonly HttpClient _httpClient = httpClient
        ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly PoiRankingProviderOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));
    private readonly IAiProviderBudget? _budget = budget;

    public async Task<Result<PoiRankingResult>> RankAsync(
        PoiRankingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string semanticInput = JsonSerializer.Serialize(
            new SemanticRankingInput(
                request.Context.PreferenceTokens.ToArray(),
                request.Candidates.Select(candidate => new SemanticRankingCandidate(
                    candidate.PoiId,
                    candidate.Name,
                    candidate.CategoryName,
                    candidate.TagNames.ToArray())).ToArray()),
            SerializerOptions);
        var payload = new GeminiInteractionRequest(
            _options.ModelName,
            semanticInput,
            SystemInstruction,
            Store: false,
            new GeminiResponseFormat(
                "text",
                "application/json",
                ResponseSchema));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };
        httpRequest.Headers.Add("x-goog-api-key", _options.ApiKey);

        IDisposable? permit = null;
        if (_budget is not null && !_budget.TryAcquire(AiProviderNames.Ranking, out permit))
        {
            return Result.Failure<PoiRankingResult>(
                PoiRankingProviderErrorCodes.Quota,
                "The local POI ranking provider budget is unavailable.");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return NetworkFailure();
        }
        catch (IOException)
        {
            return NetworkFailure();
        }
        finally
        {
            permit?.Dispose();
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return MapHttpFailure(response.StatusCode);
            }

            string responseBody;
            try
            {
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException)
            {
                return NetworkFailure();
            }
            catch (IOException)
            {
                return NetworkFailure();
            }

            GeminiInteractionResponse? interaction;
            try
            {
                interaction = JsonSerializer.Deserialize<GeminiInteractionResponse>(
                    responseBody,
                    SerializerOptions);
            }
            catch (JsonException)
            {
                return InvalidResponseFailure();
            }

            if (interaction?.Status != "completed" || interaction.Steps is null)
            {
                return InvalidResponseFailure();
            }

            string[] outputTexts = interaction.Steps
                .Where(step => step.Type == "model_output")
                .SelectMany(step => step.Content ?? [])
                .Where(content => content.Type == "text" && !string.IsNullOrWhiteSpace(content.Text))
                .Select(content => content.Text!)
                .ToArray();
            if (outputTexts.Length != 1)
            {
                return InvalidResponseFailure();
            }

            StructuredRankingResult? structured;
            try
            {
                structured = JsonSerializer.Deserialize<StructuredRankingResult>(
                    outputTexts[0],
                    SerializerOptions);
            }
            catch (JsonException)
            {
                return InvalidResponseFailure();
            }

            if (structured?.Ranked is null
                || structured.Ranked.Any(item =>
                    item is null || item.PoiId is null || item.AiScore is null))
            {
                return InvalidResponseFailure();
            }

            return Result.Success(new PoiRankingResult(
                structured!.Ranked!
                    .Select(item => new PoiRankingItem(
                        item!.PoiId!.Value,
                        item.AiScore!.Value,
                        item.Reason))
                    .ToArray()));
        }
    }

    private static Result<PoiRankingResult> NetworkFailure() =>
        Result.Failure<PoiRankingResult>(
            PoiRankingProviderErrorCodes.Network,
            "The POI ranking provider could not be reached.");

    private static Result<PoiRankingResult> InvalidResponseFailure() =>
        Result.Failure<PoiRankingResult>(
            PoiRankingProviderErrorCodes.InvalidResponse,
            "The POI ranking provider returned an invalid response.");

    private static Result<PoiRankingResult> MapHttpFailure(HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            return Result.Failure<PoiRankingResult>(
                PoiRankingProviderErrorCodes.Quota,
                "The POI ranking provider quota was exceeded.");
        }

        int numericStatus = (int)statusCode;
        if (numericStatus is >= 500 and <= 599)
        {
            return Result.Failure<PoiRankingResult>(
                PoiRankingProviderErrorCodes.ServerError,
                "The POI ranking provider returned a server error.");
        }

        return Result.Failure<PoiRankingResult>(
            PoiRankingProviderErrorCodes.InvalidResponse,
            "The POI ranking provider rejected the request.");
    }

    private static GeminiJsonSchema CreateResponseSchema()
    {
        var itemSchema = new GeminiJsonSchema(
            "object",
            new Dictionary<string, GeminiJsonSchema>
            {
                ["poiId"] = new("integer"),
                ["aiScore"] = new("number"),
                ["reason"] = new("string"),
            },
            Required: ["poiId", "aiScore"]);
        var rankedSchema = new GeminiJsonSchema("array", Items: itemSchema);

        return new GeminiJsonSchema(
            "object",
            new Dictionary<string, GeminiJsonSchema>
            {
                ["ranked"] = rankedSchema,
            },
            Required: ["ranked"]);
    }

    private sealed record GeminiInteractionRequest(
        string? Model,
        string Input,
        [property: JsonPropertyName("system_instruction")] string SystemInstruction,
        bool Store,
        [property: JsonPropertyName("response_format")] GeminiResponseFormat ResponseFormat);

    private sealed record GeminiResponseFormat(
        string Type,
        [property: JsonPropertyName("mime_type")] string MimeType,
        GeminiJsonSchema Schema);

    private sealed record GeminiJsonSchema(
        string Type,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyDictionary<string, GeminiJsonSchema>? Properties = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        GeminiJsonSchema? Items = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyCollection<string>? Required = null);

    private sealed record SemanticRankingInput(
        IReadOnlyCollection<string> PreferenceTokens,
        IReadOnlyCollection<SemanticRankingCandidate> Candidates);

    private sealed record SemanticRankingCandidate(
        long PoiId,
        string Name,
        string CategoryName,
        IReadOnlyCollection<string> TagNames);

    private sealed class GeminiInteractionResponse
    {
        public string? Status { get; init; }

        public IReadOnlyCollection<GeminiInteractionStep>? Steps { get; init; }
    }

    private sealed class GeminiInteractionStep
    {
        public string? Type { get; init; }

        public IReadOnlyCollection<GeminiInteractionContent>? Content { get; init; }
    }

    private sealed class GeminiInteractionContent
    {
        public string? Type { get; init; }

        public string? Text { get; init; }
    }

    private sealed class StructuredRankingResult
    {
        public IReadOnlyCollection<StructuredRankingItem?>? Ranked { get; init; }
    }

    private sealed class StructuredRankingItem
    {
        public long? PoiId { get; init; }

        public decimal? AiScore { get; init; }

        public string? Reason { get; init; }
    }
}