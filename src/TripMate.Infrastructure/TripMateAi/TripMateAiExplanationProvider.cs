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
using TripMate.Infrastructure.AiExplanation;

namespace TripMate.Infrastructure.TripMateAi;

/// <summary>
/// Giải thích lịch trình qua service TripMate AI (POST /v1/explain-itinerary), thay cho Gemini khi
/// <c>AiExplanation:Provider = TripMateAi</c>.
/// <para>Thân request chính là <see cref="ItineraryExplanationInput"/> (camelCase, enum dạng chữ thường), nên
/// service AI nhận đúng dữ liệu mà Gemini đang nhận. Kết quả đi qua <see cref="ItineraryExplanationValidator"/>
/// giống provider Gemini; không hợp lệ thì trả lỗi để tầng Application dùng lời giải thích dự phòng.</para>
/// <para>Timeout từng lần, timeout tổng, số lần thử và giới hạn tốc độ lấy từ <see cref="ExplanationProviderOptions"/>.
/// Thử lại với 429, 502 và 503 như provider Gemini. Riêng 504 không thử lại: service AI trả 504 khi model
/// vượt <c>LLM_TIMEOUT_SECONDS</c>, đo thực tế cho thấy đây là lỗi có hệ thống theo số item (§13.3 của spec),
/// nên lần thử thứ hai cũng 504 và chỉ làm người dùng chờ gấp đôi trước khi nhận lời giải thích dự phòng.</para>
/// <para>Khoảng chờ trước khi thử lại tôn trọng header <c>Retry-After</c> của 429, các trường hợp còn lại dùng
/// <see cref="ExplanationProviderOptions.RetryBaseDelayMilliseconds"/> cộng 0–49 ms ngẫu nhiên để các request
/// đồng thời không cùng bắn lại một lúc.</para>
/// </summary>
public sealed class TripMateAiExplanationProvider(
    HttpClient httpClient,
    IOptions<ExplanationProviderOptions> options,
    ILogger<TripMateAiExplanationProvider>? logger = null,
    IAiProviderBudget? budget = null) : IItineraryExplanationProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly HashSet<string> RootProperties = ["items"];
    private static readonly HashSet<string> ItemProperties = ["sequenceNo", "poiId", "friendlyExplanation"];

    private readonly HttpClient _httpClient = httpClient
        ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ExplanationProviderOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<TripMateAiExplanationProvider>? _logger = logger;
    private readonly IAiProviderBudget? _budget = budget;

    public async Task<Result<ItineraryExplanationResult>> ExplainAsync(
        ItineraryExplanationInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        int maxAttempts = Math.Clamp(_options.MaxAttempts, 1, 2);
        TimeSpan overallBudget = TimeSpan.FromSeconds(_options.OverallTimeoutSeconds);
        TimeSpan attemptTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        long overallStart = Stopwatch.GetTimestamp();

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TimeSpan remaining = overallBudget - Stopwatch.GetElapsedTime(overallStart);
            if (remaining <= TimeSpan.Zero)
            {
                Log(attempt, maxAttempts, "timeout", null, overallStart);
                return TimeoutFailure();
            }

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(remaining < attemptTimeout ? remaining : attemptTimeout);

            IDisposable? permit = null;
            if (_budget is not null && !_budget.TryAcquire(AiProviderNames.Explanation, out permit))
            {
                Log(attempt, maxAttempts, "local-budget", null, overallStart);
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
                    Content = JsonContent.Create(input, options: SerializerOptions),
                };
                request.Headers.Add(TripMateAiHeaders.ApiKey, _options.ApiKey);
                response = await _httpClient.SendAsync(request, attemptCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attemptCts.IsCancellationRequested)
            {
                Log(attempt, maxAttempts, "timeout", null, attemptStart);
                return TimeoutFailure();
            }
            catch (HttpRequestException)
            {
                Log(attempt, maxAttempts, "network", null, attemptStart);
                return NetworkFailure();
            }
            catch (IOException)
            {
                Log(attempt, maxAttempts, "network", null, attemptStart);
                return NetworkFailure();
            }
            finally
            {
                permit?.Dispose();
            }

            using (response)
            {
                int status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    string body;
                    try
                    {
                        body = await response.Content.ReadAsStringAsync(attemptCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attemptCts.IsCancellationRequested)
                    {
                        Log(attempt, maxAttempts, "timeout", status, attemptStart);
                        return TimeoutFailure();
                    }
                    catch (HttpRequestException)
                    {
                        Log(attempt, maxAttempts, "network", status, attemptStart);
                        return NetworkFailure();
                    }
                    catch (IOException)
                    {
                        Log(attempt, maxAttempts, "network", status, attemptStart);
                        return NetworkFailure();
                    }

                    if (!TryParseResult(body, input, out ItineraryExplanationResult? result))
                    {
                        Log(attempt, maxAttempts, "invalid-response", status, attemptStart);
                        return InvalidResponseFailure();
                    }

                    Log(attempt, maxAttempts, "success", status, attemptStart);
                    return Result.Success(result!);
                }

                bool isTransient = response.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable;
                TimeSpan delay = GetBackoffDelay(response, _options.RetryBaseDelayMilliseconds);
                bool canRetry = isTransient
                    && attempt < maxAttempts
                    && delay < overallBudget - Stopwatch.GetElapsedTime(overallStart);

                Log(attempt, maxAttempts, MapOutcome(response.StatusCode), status, attemptStart, canRetry);
                if (!canRetry)
                {
                    return MapHttpFailure(response.StatusCode);
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        return MapHttpFailure(HttpStatusCode.ServiceUnavailable);
    }

    private static bool TryParseResult(
        string body,
        ItineraryExplanationInput input,
        out ItineraryExplanationResult? result)
    {
        result = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasExactProperties(root, RootProperties)
                || !root.TryGetProperty("items", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array
                || items.EnumerateArray().Any(item =>
                    item.ValueKind != JsonValueKind.Object || !HasExactProperties(item, ItemProperties)))
            {
                return false;
            }

            result = JsonSerializer.Deserialize<ItineraryExplanationResult>(body, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (!ItineraryExplanationValidator.TryValidate(input, result, out IReadOnlyDictionary<int, string> validated))
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

    private static Result<ItineraryExplanationResult> TimeoutFailure() =>
        Result.Failure<ItineraryExplanationResult>(
            ExplanationProviderErrorCodes.Timeout,
            "The itinerary explanation provider timed out.");

    private static Result<ItineraryExplanationResult> MapHttpFailure(HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            return Result.Failure<ItineraryExplanationResult>(
                ExplanationProviderErrorCodes.Quota,
                "The itinerary explanation provider quota was exceeded.");
        }

        if (statusCode == HttpStatusCode.GatewayTimeout)
        {
            return TimeoutFailure();
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

    /// <summary>
    /// Khoảng chờ trước lần thử lại: tôn trọng <c>Retry-After</c> khi service trả 429 (dạng giây hoặc mốc thời gian),
    /// còn lại là delay cơ sở cộng 0–49 ms ngẫu nhiên. Giống <c>HttpItineraryExplanationProvider.GetBackoffDelay</c>.
    /// </summary>
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

    private static string MapOutcome(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.TooManyRequests => "quota",
        HttpStatusCode.GatewayTimeout => "timeout",
        _ when (int)statusCode is >= 500 and <= 599 => "server-error",
        _ => "invalid-response",
    };

    private void Log(int attempt, int maxAttempts, string outcome, int? httpStatus, long since, bool retryScheduled = false)
    {
        if (_logger is null)
        {
            return;
        }

        double latencyMs = Stopwatch.GetElapsedTime(since).TotalMilliseconds;
        LogLevel level = outcome == "success" ? LogLevel.Information : LogLevel.Warning;
        _logger.Log(
            level,
            "TripMate AI explanation attempt={Attempt}/{MaxAttempts}; httpStatus={HttpStatus}; latencyMs={LatencyMs:F0}; outcome={Outcome}; retryScheduled={RetryScheduled}.",
            attempt,
            maxAttempts,
            httpStatus,
            latencyMs,
            outcome,
            retryScheduled);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}