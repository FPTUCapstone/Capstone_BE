using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Explanation;

namespace TripMate.Infrastructure.AiExplanation;

public sealed class HttpItineraryExplanationProvider(
    HttpClient httpClient,
    IOptions<ExplanationProviderOptions> options) : IItineraryExplanationProvider
{
    private const string SystemInstruction = """
        Bạn là trợ lý giải thích lịch trình du lịch.

        Chỉ viết bằng tiếng Việt và trả về đúng một câu giải thích ngắn, tối đa 500 ký tự, cho mỗi mục được cung cấp theo đúng thứ tự.
        Chỉ giải thích dựa trên sở thích, thông tin điểm đến và lịch trình đã được cung cấp.
        Không suy diễn hoặc bịa đặt danh tính hay dữ kiện.
        Không thêm, xóa, thay thế, sắp xếp lại hoặc đổi thời gian các điểm dừng.
        Không đưa ra quyết định về chi phí, di chuyển, tính bắt buộc hoặc tính khả thi của lịch trình.
        """;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly GeminiJsonSchema ResponseSchema = CreateResponseSchema();
    private static readonly HashSet<string> RootOutputProperties = ["items"];
    private static readonly HashSet<string> ItemOutputProperties =
        ["sequenceNo", "poiId", "friendlyExplanation"];

    private readonly HttpClient _httpClient = httpClient
        ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ExplanationProviderOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    public async Task<Result<ItineraryExplanationResult>> ExplainAsync(
        ItineraryExplanationInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        string serializedInput = JsonSerializer.Serialize(input, SerializerOptions);
        var payload = new GeminiInteractionRequest(
            _options.ModelName,
            serializedInput,
            SystemInstruction,
            Store: false,
            new GeminiResponseFormat(
                "text",
                "application/json",
                ResponseSchema));

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };
        request.Headers.Add("x-goog-api-key", _options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return NetworkFailure();
        }
        catch (IOException)
        {
            return NetworkFailure();
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

            string? output = ReadSingleModelOutput(responseBody);
            if (output is null || !TryParseResult(output, input, out var result))
            {
                return InvalidResponseFailure();
            }

            return Result.Success(result!);
        }
    }

    private static string? ReadSingleModelOutput(string responseBody)
    {
        GeminiInteractionResponse? interaction;
        try
        {
            interaction = JsonSerializer.Deserialize<GeminiInteractionResponse>(
                responseBody,
                SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (interaction?.Status != "completed" || interaction.Steps is null)
        {
            return null;
        }

        GeminiInteractionStep[] modelOutputs = interaction.Steps
            .Where(step => step.Type == "model_output")
            .ToArray();
        if (modelOutputs.Length != 1)
        {
            return null;
        }

        string[] outputTexts = (modelOutputs[0].Content ?? [])
            .Where(content => content.Type == "text" && !string.IsNullOrWhiteSpace(content.Text))
            .Select(content => content.Text!)
            .ToArray();

        return outputTexts.Length == 1 ? outputTexts[0] : null;
    }

    private static bool TryParseResult(
        string output,
        ItineraryExplanationInput input,
        out ItineraryExplanationResult? result)
    {
        result = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasExactProperties(root, RootOutputProperties)
                || !root.TryGetProperty("items", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array
                || items.EnumerateArray().Any(item =>
                    item.ValueKind != JsonValueKind.Object
                    || !HasExactProperties(item, ItemOutputProperties)))
            {
                return false;
            }

            result = JsonSerializer.Deserialize<ItineraryExplanationResult>(
                output,
                SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (!ItineraryExplanationValidator.TryValidate(
                input,
                result,
                out IReadOnlyDictionary<int, string> validated))
        {
            result = null;
            return false;
        }

        result = new ItineraryExplanationResult(
            result!.Items.Select(item => new ItineraryExplanationItemResult(
                item.SequenceNo,
                item.PoiId,
                validated[item.SequenceNo])).ToArray());
        return true;
    }

    private static bool HasExactProperties(JsonElement element, HashSet<string> expected)
    {
        string[] actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.Length == expected.Count && actual.All(expected.Contains);
    }

    private static Result<ItineraryExplanationResult> NetworkFailure() =>
        Result.Failure<ItineraryExplanationResult>(
            ExplanationProviderErrorCodes.Network,
            "The itinerary explanation provider could not be reached.");

    private static Result<ItineraryExplanationResult> InvalidResponseFailure() =>
        Result.Failure<ItineraryExplanationResult>(
            ExplanationProviderErrorCodes.InvalidResponse,
            "The itinerary explanation provider returned an invalid response.");

    private static Result<ItineraryExplanationResult> MapHttpFailure(HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            return Result.Failure<ItineraryExplanationResult>(
                ExplanationProviderErrorCodes.Quota,
                "The itinerary explanation provider quota was exceeded.");
        }

        int numericStatus = (int)statusCode;
        if (numericStatus is >= 500 and <= 599)
        {
            return Result.Failure<ItineraryExplanationResult>(
                ExplanationProviderErrorCodes.ServerError,
                "The itinerary explanation provider returned a server error.");
        }

        return Result.Failure<ItineraryExplanationResult>(
            ExplanationProviderErrorCodes.InvalidResponse,
            "The itinerary explanation provider rejected the request.");
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static GeminiJsonSchema CreateResponseSchema()
    {
        var itemSchema = new GeminiJsonSchema(
            "object",
            new Dictionary<string, GeminiJsonSchema>
            {
                ["sequenceNo"] = new("integer"),
                ["poiId"] = new(new[] { "integer", "null" }),
                ["friendlyExplanation"] = new("string"),
            },
            Required: ["sequenceNo", "poiId", "friendlyExplanation"],
            AdditionalProperties: false);

        return new GeminiJsonSchema(
            "object",
            new Dictionary<string, GeminiJsonSchema>
            {
                ["items"] = new("array", Items: itemSchema),
            },
            Required: ["items"],
            AdditionalProperties: false);
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
        object Type,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyDictionary<string, GeminiJsonSchema>? Properties = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        GeminiJsonSchema? Items = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyCollection<string>? Required = null,
        [property: JsonPropertyName("additionalProperties")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? AdditionalProperties = null);

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
}