using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Infrastructure.Ai;

namespace TripMate.Infrastructure.AiExplanation;

public sealed class HttpItineraryExplanationProvider(
    HttpClient httpClient,
    IOptions<ExplanationProviderOptions> options,
    ILogger<HttpItineraryExplanationProvider>? logger = null,
    IAiProviderBudget? budget = null) : IItineraryExplanationProvider
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
    private readonly ILogger<HttpItineraryExplanationProvider>? _logger = logger;
    private readonly IAiProviderBudget? _budget = budget;

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

        int maxAttempts = Math.Clamp(_options.MaxAttempts, 1, 2);
        TimeSpan overallBudget = TimeSpan.FromSeconds(_options.OverallTimeoutSeconds);
        TimeSpan attemptTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        long overallStart = Stopwatch.GetTimestamp();

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TimeSpan elapsed = Stopwatch.GetElapsedTime(overallStart);
            TimeSpan remainingBudget = overallBudget - elapsed;
            if (remainingBudget <= TimeSpan.Zero)
            {
                LogAttempt(attempt, maxAttempts, "timeout", httpStatus: null, latencyMs: elapsed.TotalMilliseconds, retryScheduled: false);
                return TimeoutFailure();
            }

            TimeSpan currentAttemptTimeout = remainingBudget < attemptTimeout ? remainingBudget : attemptTimeout;
            if (currentAttemptTimeout <= TimeSpan.Zero)
            {
                LogAttempt(attempt, maxAttempts, "timeout", httpStatus: null, latencyMs: elapsed.TotalMilliseconds, retryScheduled: false);
                return TimeoutFailure();
            }

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(currentAttemptTimeout);

            IDisposable? permit = null;
            if (_budget is not null
                && !_budget.TryAcquire(AiProviderNames.Explanation, out permit))
            {
                LogAttempt(attempt, maxAttempts, "local-budget", httpStatus: null, latencyMs: 0, retryScheduled: false);
                return Result.Failure<ItineraryExplanationResult>(
                    ExplanationProviderErrorCodes.Quota,
                    "The local itinerary explanation provider budget is unavailable.");
            }

            long attemptStart = Stopwatch.GetTimestamp();
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
                {
                    Content = JsonContent.Create(payload, options: SerializerOptions),
                };
                request.Headers.Add("x-goog-api-key", _options.ApiKey);

                response = await _httpClient.SendAsync(request, attemptCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attemptCts.IsCancellationRequested)
            {
                LogAttempt(attempt, maxAttempts, "timeout", httpStatus: null, latencyMs: Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds, retryScheduled: false);
                return TimeoutFailure();
            }
            catch (HttpRequestException)
            {
                LogAttempt(attempt, maxAttempts, "network", httpStatus: null, latencyMs: Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds, retryScheduled: false);
                return NetworkFailure();
            }
            catch (IOException)
            {
                LogAttempt(attempt, maxAttempts, "network", httpStatus: null, latencyMs: Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds, retryScheduled: false);
                return NetworkFailure();
            }
            finally
            {
                permit?.Dispose();
            }

            using (response)
            {
                int statusCodeNumber = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    string responseBody;
                    try
                    {
                        responseBody = await response.Content.ReadAsStringAsync(attemptCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attemptCts.IsCancellationRequested)
                    {
                        LogAttempt(attempt, maxAttempts, "timeout", statusCodeNumber, latencyMs: Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds, retryScheduled: false);
                        return TimeoutFailure();
                    }
                    catch (HttpRequestException)
                    {
                        LogAttempt(attempt, maxAttempts, "network", statusCodeNumber, latencyMs: Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds, retryScheduled: false);
                        return NetworkFailure();
                    }
                    catch (IOException)
                    {
                        LogAttempt(attempt, maxAttempts, "network", statusCodeNumber, latencyMs: Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds, retryScheduled: false);
                        return NetworkFailure();
                    }

                    string? output = ReadSingleModelOutput(responseBody);
                    if (output is null || !TryParseResult(output, input, out var result))
                    {
                        LogAttempt(attempt, maxAttempts, "invalid-response", statusCodeNumber, latencyMs: Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds, retryScheduled: false);
                        return InvalidResponseFailure();
                    }

                    LogAttempt(attempt, maxAttempts, "success", statusCodeNumber, latencyMs: Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds, retryScheduled: false);
                    return Result.Success(result!);
                }

                bool isTransient = response.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout;

                if (!isTransient || attempt >= maxAttempts)
                {
                    LogAttempt(attempt, maxAttempts, MapExplanationOutcome(response.StatusCode), statusCodeNumber, latencyMs: Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds, retryScheduled: false);
                    return MapHttpFailure(response.StatusCode);
                }

                TimeSpan delay = GetBackoffDelay(response, _options.RetryBaseDelayMilliseconds);
                TimeSpan elapsedSoFar = Stopwatch.GetElapsedTime(overallStart);
                TimeSpan remaining = overallBudget - elapsedSoFar;

                if (delay >= remaining)
                {
                    LogAttempt(attempt, maxAttempts, MapExplanationOutcome(response.StatusCode), statusCodeNumber, latencyMs: Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds, retryScheduled: false);
                    return MapHttpFailure(response.StatusCode);
                }

                LogAttempt(attempt, maxAttempts, MapExplanationOutcome(response.StatusCode), statusCodeNumber, latencyMs: Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds, retryScheduled: true);

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        return MapHttpFailure(HttpStatusCode.ServiceUnavailable);
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

    private static Result<ItineraryExplanationResult> TimeoutFailure() =>
        Result.Failure<ItineraryExplanationResult>(
            ExplanationProviderErrorCodes.Timeout,
            "The itinerary explanation provider timed out.");

    private static string MapExplanationOutcome(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.TooManyRequests => "quota",
        _ when (int)statusCode >= 500 && (int)statusCode <= 599 => "server-error",
        _ => "invalid-response",
    };

    private static TimeSpan GetBackoffDelay(HttpResponseMessage response, int retryBaseDelayMilliseconds)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter is not null)
        {
            if (response.Headers.RetryAfter.Delta.HasValue)
            {
                TimeSpan delta = response.Headers.RetryAfter.Delta.Value;
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }

            if (response.Headers.RetryAfter.Date.HasValue)
            {
                TimeSpan diff = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
            }
        }

        int baseMs = Math.Max(0, retryBaseDelayMilliseconds);
        int jitter = baseMs > 0 ? Random.Shared.Next(0, 50) : 0;
        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }

    private void LogAttempt(
        int attempt,
        int maxAttempts,
        string outcome,
        int? httpStatus,
        double latencyMs,
        bool retryScheduled)
    {
        if (_logger is null)
        {
            return;
        }

        if (outcome == "success")
        {
            _logger.LogInformation(
                "Itinerary explanation provider attempt={Attempt}/{MaxAttempts}; httpStatus={HttpStatus}; latencyMs={LatencyMs:F0}; outcome={Outcome}; retryScheduled={RetryScheduled}.",
                attempt,
                maxAttempts,
                httpStatus,
                latencyMs,
                outcome,
                retryScheduled);
        }
        else
        {
            _logger.LogWarning(
                "Itinerary explanation provider attempt={Attempt}/{MaxAttempts}; httpStatus={HttpStatus}; latencyMs={LatencyMs:F0}; outcome={Outcome}; retryScheduled={RetryScheduled}.",
                attempt,
                maxAttempts,
                httpStatus,
                latencyMs,
                outcome,
                retryScheduled);
        }
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
