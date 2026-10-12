using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Ai;
using TripMate.Infrastructure.AiExplanation;
using TripMate.Infrastructure.TripMateAi;

namespace TripMate.Infrastructure.UnitTests.TripMateAi;

/// <summary>
/// Retry, timeout, budget, contract and logging paths of <see cref="TripMateAiExplanationProvider"/> that the
/// patch's own tests do not cover (spec UC-10-tripmate-ai-provider §12.2, E1–E10).
/// </summary>
public sealed class TripMateAiExplanationProviderTests
{
    private const string Endpoint = "http://ai:8000/v1/explain-itinerary";
    private const string SharedKey = "test-only-shared-key";
    private const string PoiName = "Bảo tàng Điêu khắc Chăm";
    private const string VisitText = "Bảo tàng Điêu khắc Chăm là điểm bạn đã chọn để tìm hiểu văn hóa.";
    private const string RestText = "Bạn có 30 phút nghỉ để lấy lại sức.";

    // E1
    [Fact]
    public async Task ExplainAsync_WhenTheBudgetDeniesTheFirstAttempt_ReturnsQuotaWithoutARequest()
    {
        using var handler = new SequenceHttpMessageHandler(_ => Success());
        var provider = CreateProvider(handler, ExplanationOptions(), budget: new DenyBudget());

        var result = await provider.ExplainAsync(Input(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.Quota);
        handler.CallCount.Should().Be(0);
    }

    // E2
    [Fact]
    public async Task ExplainAsync_WhenTheBudgetDeniesTheRetry_ReturnsQuotaAfterOneRequest()
    {
        var budget = new InMemoryAiProviderBudget(
            new FixedClock(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero)),
            new AiProviderBudgetSettings(RateLimitPerMinute: 1, MaxConcurrency: 1),
            new AiProviderBudgetSettings(RateLimitPerMinute: 1, MaxConcurrency: 1));
        using var handler = new SequenceHttpMessageHandler(_ => Status(HttpStatusCode.ServiceUnavailable));
        var provider = CreateProvider(handler, ExplanationOptions(), budget: budget);

        var result = await provider.ExplainAsync(Input(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.Quota);
        handler.CallCount.Should().Be(1, "every attempt takes its own permit, and the second is denied");
    }

    // E3
    [Fact]
    public async Task ExplainAsync_WhenAnAttemptTimesOut_ReturnsTimeoutWithoutRetrying()
    {
        using var handler = new DelayingHttpMessageHandler(TimeSpan.FromSeconds(5));
        var provider = CreateProvider(handler, ExplanationOptions(timeoutSeconds: 1, overallTimeoutSeconds: 5));

        var result = await provider.ExplainAsync(Input(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.Timeout);
        handler.CallCount.Should().Be(1);
    }

    // E4
    [Fact]
    public async Task ExplainAsync_503Twice_ReturnsServerErrorAfterTwoRequests()
    {
        using var handler = new SequenceHttpMessageHandler(_ => Status(HttpStatusCode.ServiceUnavailable));
        var provider = CreateProvider(handler, ExplanationOptions());

        var result = await provider.ExplainAsync(Input(), CancellationToken.None);

        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.ServerError);
        handler.CallCount.Should().Be(2);
    }

    // E4
    [Fact]
    public async Task ExplainAsync_429ThenSuccess_SucceedsAfterTwoRequests()
    {
        using var handler = new SequenceHttpMessageHandler(
            _ => Status(HttpStatusCode.TooManyRequests),
            _ => Success());
        var provider = CreateProvider(handler, ExplanationOptions());

        var result = await provider.ExplainAsync(Input(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        handler.CallCount.Should().Be(2);
    }

    // E4
    [Fact]
    public async Task ExplainAsync_429Twice_ReturnsQuotaAfterTwoRequests()
    {
        using var handler = new SequenceHttpMessageHandler(_ => Status(HttpStatusCode.TooManyRequests));
        var provider = CreateProvider(handler, ExplanationOptions());

        var result = await provider.ExplainAsync(Input(), CancellationToken.None);

        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.Quota);
        handler.CallCount.Should().Be(2);
    }

    // E5
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, ExplanationProviderErrorCodes.ServerError)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ExplanationProviderErrorCodes.InvalidResponse)]
    [InlineData(HttpStatusCode.BadRequest, ExplanationProviderErrorCodes.InvalidResponse)]
    [InlineData(HttpStatusCode.Forbidden, ExplanationProviderErrorCodes.InvalidResponse)]
    public async Task ExplainAsync_NonRetryableStatus_MapsWithOneRequest(HttpStatusCode status, string expectedCode)
    {
        using var handler = new SequenceHttpMessageHandler(_ => Status(status));
        var provider = CreateProvider(handler, ExplanationOptions());

        var result = await provider.ExplainAsync(Input(), CancellationToken.None);

        result.ErrorCode.Should().Be(expectedCode);
        handler.CallCount.Should().Be(1);
    }

    // E6
    [Fact]
    public async Task ExplainAsync_RetryDelayThatDoesNotFitTheOverallBudget_DoesNotRetry()
    {
        using var handler = new SequenceHttpMessageHandler(_ => Status(HttpStatusCode.ServiceUnavailable));
        var provider = CreateProvider(handler, ExplanationOptions(overallTimeoutSeconds: 10, retryDelayMilliseconds: 20_000));

        var result = await provider.ExplainAsync(Input(), CancellationToken.None);

        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.ServerError);
        handler.CallCount.Should().Be(1);
    }

    // E7
    [Fact]
    public async Task ExplainAsync_WhenCallerCancelsDuringTheSend_PropagatesCancellation()
    {
        using var handler = new CancellationObservingHttpMessageHandler();
        var provider = CreateProvider(handler, ExplanationOptions());
        using var cancellation = new CancellationTokenSource();

        Task operation = provider.ExplainAsync(Input(), cancellation.Token);
        await handler.Called.Task;
        cancellation.Cancel();

        Func<Task> action = () => operation;
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    // E7
    [Fact]
    public async Task ExplainAsync_WhenCallerCancelsDuringTheRetryDelay_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new SequenceHttpMessageHandler(_ =>
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            return Status(HttpStatusCode.ServiceUnavailable);
        });
        var provider = CreateProvider(handler, ExplanationOptions(overallTimeoutSeconds: 30, retryDelayMilliseconds: 5_000));

        Func<Task> action = () => provider.ExplainAsync(Input(), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.CallCount.Should().Be(1);
    }

    // E8
    [Fact]
    public async Task ExplainAsync_SendsTheInputRecordsExactly_IncludingAFreeRestItem()
    {
        using var handler = new SequenceHttpMessageHandler(_ => Success());
        var provider = CreateProvider(handler, ExplanationOptions());

        await provider.ExplainAsync(Input(), CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.RequestBodies.Single());
        JsonElement root = body.RootElement;
        Names(root).Should().BeEquivalentTo(CamelCaseProperties<ItineraryExplanationInput>());
        Names(root.GetProperty("context")).Should().BeEquivalentTo(CamelCaseProperties<ItineraryExplanationContext>());

        JsonElement[] items = root.GetProperty("items").EnumerateArray().ToArray();
        foreach (JsonElement item in items)
        {
            Names(item).Should().BeEquivalentTo(CamelCaseProperties<ItineraryExplanationItem>());
        }

        JsonElement rest = items[1];
        rest.GetProperty("poiId").ValueKind.Should().Be(JsonValueKind.Null);
        rest.GetProperty("poiName").ValueKind.Should().Be(JsonValueKind.Null);
        rest.GetProperty("categoryName").ValueKind.Should().Be(JsonValueKind.Null);
        rest.GetProperty("tagNames").GetArrayLength().Should().Be(0);
        rest.GetProperty("kind").GetString().Should().Be("rest");
    }

    // E9
    [Theory]
    [InlineData("""{"items":[{"sequenceNo":1,"poiId":12,"friendlyExplanation":"Một câu."},{"sequenceNo":2,"poiId":null,"friendlyExplanation":"Hai câu."}],"model":"qwen3"}""")]
    [InlineData("""{"Items":[{"sequenceNo":1,"poiId":12,"friendlyExplanation":"Một câu."},{"sequenceNo":2,"poiId":null,"friendlyExplanation":"Hai câu."}]}""")]
    [InlineData("""[{"sequenceNo":1,"poiId":12,"friendlyExplanation":"Một câu."},{"sequenceNo":2,"poiId":null,"friendlyExplanation":"Hai câu."}]""")]
    public async Task ExplainAsync_RootThatIsNotExactlyItems_ReturnsInvalidResponse(string responseBody)
    {
        using var handler = new SequenceHttpMessageHandler(_ => Json(responseBody));
        var provider = CreateProvider(handler, ExplanationOptions());

        var result = await provider.ExplainAsync(Input(), CancellationToken.None);

        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.InvalidResponse);
        handler.CallCount.Should().Be(1);
    }

    // E10
    [Fact]
    public async Task ExplainAsync_LogsEachAttemptWithoutKeyNamesOrTexts()
    {
        var logger = new CapturingLogger<TripMateAiExplanationProvider>();
        using var successHandler = new SequenceHttpMessageHandler(_ => Success());
        using var failureHandler = new SequenceHttpMessageHandler(_ => Status(HttpStatusCode.InternalServerError));

        await CreateProvider(successHandler, ExplanationOptions(), logger).ExplainAsync(Input(), CancellationToken.None);
        await CreateProvider(failureHandler, ExplanationOptions(), logger).ExplainAsync(Input(), CancellationToken.None);

        logger.Entries.Select(entry => entry.Level).Should().Equal(LogLevel.Information, LogLevel.Warning);
        foreach ((LogLevel _, string message) in logger.Entries)
        {
            message.Should().NotContain(SharedKey);
            message.Should().NotContain(PoiName);
            message.Should().NotContain(VisitText);
            message.Should().NotContain(RestText);
        }
    }

    private static ExplanationProviderOptions ExplanationOptions(
        int timeoutSeconds = 5,
        int overallTimeoutSeconds = 10,
        int retryDelayMilliseconds = 0) => new()
        {
            Enabled = true,
            Provider = AiProviderKind.TripMateAi,
            Endpoint = Endpoint,
            ApiKey = SharedKey,
            TimeoutSeconds = timeoutSeconds,
            OverallTimeoutSeconds = overallTimeoutSeconds,
            MaxAttempts = 2,
            RetryBaseDelayMilliseconds = retryDelayMilliseconds,
        };

    private static TripMateAiExplanationProvider CreateProvider(
        HttpMessageHandler handler,
        ExplanationProviderOptions options,
        ILogger<TripMateAiExplanationProvider>? logger = null,
        IAiProviderBudget? budget = null) =>
        new(new HttpClient(handler), Options.Create(options), logger, budget);

    private static ItineraryExplanationInput Input()
    {
        var start = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        return new ItineraryExplanationInput(
            [
                new ItineraryExplanationItem(
                    1, 12L, PoiName, "Bảo tàng", ["văn hóa"], ItineraryItemKind.Visit, true,
                    start.AddMinutes(30), start.AddMinutes(90), 60, 60_000m, 15, "Mandatory location"),
                new ItineraryExplanationItem(
                    2, null, null, null, [], ItineraryItemKind.Rest, false,
                    start.AddMinutes(105), start.AddMinutes(135), 30, null, null, "Free/rest time"),
            ],
            new ItineraryExplanationContext(["van hoa"], start, "Asia/Ho_Chi_Minh", 135));
    }

    private static HttpResponseMessage Success() => Json(JsonSerializer.Serialize(new
    {
        items = new object[]
        {
            new { sequenceNo = 1, poiId = (long?)12, friendlyExplanation = VisitText },
            new { sequenceNo = 2, poiId = (long?)null, friendlyExplanation = RestText },
        },
    }));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    private static IEnumerable<string> Names(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name);

    private static IEnumerable<string> CamelCaseProperties<T>() =>
        typeof(T).GetProperties().Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name));

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class DenyBudget : IAiProviderBudget
    {
        public bool TryAcquire(string provider, out IDisposable? permit)
        {
            permit = null;
            return false;
        }
    }

    /// <summary>Answers each request with the next factory; the last one repeats.</summary>
    private sealed class SequenceHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return responses[Math.Min(CallCount, responses.Length) - 1](request);
        }
    }

    private sealed class DelayingHttpMessageHandler(TimeSpan delay) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class CancellationObservingHttpMessageHandler : HttpMessageHandler
    {
        public TaskCompletionSource Called { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Called.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}