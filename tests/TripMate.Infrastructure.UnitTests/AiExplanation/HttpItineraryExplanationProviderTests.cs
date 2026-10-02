using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.AiExplanation;

namespace TripMate.Infrastructure.UnitTests.AiExplanation;

public sealed class HttpItineraryExplanationProviderTests
{
    private const string Endpoint =
        "https://generativelanguage.googleapis.com/v1/interactions";
    private const string FakeApiKey = "test-only-gemini-key";

    [Fact]
    public async Task ExplainAsync_WithCompletedResponse_SendsRestrictedRequestAndMapsResult()
    {
        using var handler = new StubHttpMessageHandler(_ => JsonResponse(CompletedResponse("""
            {"items":[{"sequenceNo":1,"poiId":7,"friendlyExplanation":"Phù hợp sở thích văn hóa của bạn."}]}
            """)));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var result = await provider.ExplainAsync(Request(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEquivalentTo(
            [new ItineraryExplanationItemResult(1, 7L, "Phù hợp sở thích văn hóa của bạn.")],
            options => options.WithStrictOrdering());

        handler.CallCount.Should().Be(1);
        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri.Should().Be(Endpoint);
        handler.Request.Headers.GetValues("x-goog-api-key").Should().Equal(FakeApiKey);
        handler.Request.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");

        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        JsonElement root = body.RootElement;
        root.GetProperty("model").GetString().Should().Be("gemini-3.5-flash-lite");
        root.GetProperty("store").GetBoolean().Should().BeFalse();
        JsonElement responseFormat = root.GetProperty("response_format");
        responseFormat.GetProperty("type").GetString().Should().Be("text");
        responseFormat.GetProperty("mime_type").GetString().Should().Be("application/json");
        JsonElement rootSchema = responseFormat.GetProperty("schema");
        rootSchema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        JsonElement itemSchema = rootSchema.GetProperty("properties")
            .GetProperty("items").GetProperty("items");
        itemSchema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        itemSchema.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString())
            .Should().Equal("sequenceNo", "poiId", "friendlyExplanation");
        itemSchema.GetProperty("properties").EnumerateObject()
            .Select(property => property.Name)
            .Should().BeEquivalentTo("sequenceNo", "poiId", "friendlyExplanation");
        itemSchema.GetProperty("properties").GetProperty("poiId").GetProperty("type")
            .EnumerateArray().Select(value => value.GetString())
            .Should().Equal("integer", "null");

        string input = root.GetProperty("input").GetString()!;
        using JsonDocument explanationInput = JsonDocument.Parse(input);
        explanationInput.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("items", "context");
        JsonElement item = explanationInput.RootElement.GetProperty("items")[0];
        item.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "sequenceNo", "poiId", "poiName", "categoryName", "tagNames", "kind",
            "isMandatory", "plannedArrivalUtc", "plannedDepartureUtc",
            "stayDurationMinutes", "estimatedCost", "travelDurationToNextMinutes",
            "cspRecommendationReason");
        handler.RequestBody.Should().NotContain(FakeApiKey)
            .And.NotContain("travelerId")
            .And.NotContain("email")
            .And.NotContain("requestHash")
            .And.NotContain("idempotencyKey")
            .And.NotContain("baseScore")
            .And.NotContain("aiScore")
            .And.NotContain("effectiveDesirabilityScore")
            .And.NotContain("latitude")
            .And.NotContain("longitude");
        root.GetProperty("system_instruction").GetString().Should()
            .Contain("Chỉ viết bằng tiếng Việt")
            .And.Contain("tối đa 500 ký tự")
            .And.Contain("Không thêm, xóa, thay thế, sắp xếp lại hoặc đổi thời gian");
    }

    [Fact]
    public async Task ExplainAsync_WhenLocalBudgetDenied_DoesNotCallProvider()
    {
        using var handler = new StubHttpMessageHandler(_ => JsonResponse("{}"));
        var provider = CreateProvider(new HttpClient(handler), budget: new DenyBudget());

        var result = await provider.ExplainAsync(Request(), CancellationToken.None);

        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.Quota);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ExplainAsync_WithPoiLessRest_ParsesNullablePoiId()
    {
        using var handler = new StubHttpMessageHandler(_ => JsonResponse(CompletedResponse("""
            {"items":[{"sequenceNo":1,"poiId":null,"friendlyExplanation":"Khoảng nghỉ tự do giúp lịch trình cân bằng."}]}
            """)));
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.ExplainAsync(RestRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.PoiId.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ExplanationProviderErrorCodes.Quota)]
    [InlineData(HttpStatusCode.InternalServerError, ExplanationProviderErrorCodes.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ExplanationProviderErrorCodes.ServerError)]
    [InlineData(HttpStatusCode.BadRequest, ExplanationProviderErrorCodes.InvalidResponse)]
    public async Task ExplainAsync_WithNonSuccessStatus_ReturnsSanitizedFailure(
        HttpStatusCode statusCode,
        string expectedErrorCode)
    {
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent($"provider detail includes {FakeApiKey}"),
        });
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.ExplainAsync(Request(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(expectedErrorCode);
        result.ErrorMessage.Should().NotContain(FakeApiKey).And.NotContain("provider detail");
    }

    [Theory]
    [InlineData("http")]
    [InlineData("io")]
    public async Task ExplainAsync_WithTransportFailure_ReturnsNetworkFailure(string failureKind)
    {
        Exception exception = failureKind == "http"
            ? new HttpRequestException($"transport detail {FakeApiKey}")
            : new IOException($"stream detail {FakeApiKey}");
        using var client = new HttpClient(new ThrowingHttpMessageHandler(exception));
        var provider = CreateProvider(client);

        var result = await provider.ExplainAsync(Request(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.Network);
        result.ErrorMessage.Should().NotContain(FakeApiKey).And.NotContain("detail");
    }

    [Theory]
    [MemberData(nameof(InvalidResponseCases))]
    public async Task ExplainAsync_WithInvalidProviderOutput_ReturnsInvalidResponse(string body)
    {
        using var handler = new StubHttpMessageHandler(_ => JsonResponse(body));
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.ExplainAsync(Request(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.InvalidResponse);
        result.ErrorMessage.Should().NotContain(body);
    }

    [Fact]
    public async Task ExplainAsync_WhenCallerCancels_PropagatesCancellation()
    {
        using var handler = new CancellationObservingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);
        using var cancellation = new CancellationTokenSource();

        Task operation = provider.ExplainAsync(Request(), cancellation.Token);
        await handler.Called.Task;
        cancellation.Cancel();

        Func<Task> action = () => operation;
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    public static TheoryData<string> InvalidResponseCases => new()
    {
        "{",
        """{"status":"completed","steps":[]}""",
        JsonSerializer.Serialize(new
        {
            status = "completed",
            steps = new[]
            {
                new
                {
                    type = "model_output",
                    content = new[]
                    {
                        new { type = "text", text = "{\"items\":[]}" },
                        new { type = "text", text = "{\"items\":[]}" },
                    },
                },
            },
        }),
        JsonSerializer.Serialize(new
        {
            status = "completed",
            steps = new[]
            {
                new
                {
                    type = "model_output",
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = "{\"items\":[{\"sequenceNo\":1,\"poiId\":7,\"friendlyExplanation\":\"Hợp lịch trình.\"}]}",
                        },
                    },
                },
                new
                {
                    type = "model_output",
                    content = new[] { new { type = "text", text = " " } },
                },
            },
        }),
    };

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ExplainAsync_WithFirstAttemptTransientFailure_RetriesAndSucceeds(
        HttpStatusCode transientStatus)
    {
        int attempt = 0;
        using var handler = new StubHttpMessageHandler(_ =>
        {
            attempt++;
            return attempt == 1
                ? new HttpResponseMessage(transientStatus)
                : JsonResponse(CompletedResponse("""
                    {"items":[{"sequenceNo":1,"poiId":7,"friendlyExplanation":"Giải thích thành công sau retry."}]}
                    """));
        });
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.ExplainAsync(Request(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.FriendlyExplanation.Should().Be(
            "Giải thích thành công sau retry.");
        handler.CallCount.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, ExplanationProviderErrorCodes.ServerError)]
    [InlineData(HttpStatusCode.TooManyRequests, ExplanationProviderErrorCodes.Quota)]
    public async Task ExplainAsync_WithTransientFailureExhaustion_ReturnsFailureAfterTwoAttempts(
        HttpStatusCode transientStatus,
        string expectedErrorCode)
    {
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(transientStatus));
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.ExplainAsync(Request(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(expectedErrorCode);
        handler.CallCount.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ExplainAsync_WithNonRetryableStatusCode_DoesNotRetry(HttpStatusCode statusCode)
    {
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode));
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.ExplainAsync(Request(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.InvalidResponse);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ExplainAsync_WhenRetryDelayExceedsRemainingBudget_SkipsRetry()
    {
        using var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(30));
            return response;
        });
        var provider = CreateProvider(new HttpClient(handler), new ExplanationProviderOptions
        {
            Enabled = true,
            Endpoint = Endpoint,
            ApiKey = FakeApiKey,
            ModelName = "gemini-3.5-flash-lite",
            TimeoutSeconds = 5,
            OverallTimeoutSeconds = 5,
            MaxAttempts = 2,
            RetryBaseDelayMilliseconds = 0,
        });

        var result = await provider.ExplainAsync(Request(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.Quota);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ExplainAsync_WhenAttemptTimesOut_ReturnsTimeoutFailureAndDoesNotRetry()
    {
        using var handler = new DelayingHttpMessageHandler(TimeSpan.FromSeconds(5));
        var provider = CreateProvider(new HttpClient(handler), new ExplanationProviderOptions
        {
            Enabled = true,
            Endpoint = Endpoint,
            ApiKey = FakeApiKey,
            ModelName = "gemini-3.5-flash-lite",
            TimeoutSeconds = 1,
            OverallTimeoutSeconds = 5,
            MaxAttempts = 2,
            RetryBaseDelayMilliseconds = 0,
        });

        var result = await provider.ExplainAsync(Request(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.Timeout);
        handler.CallCount.Should().Be(1);
    }

    private static HttpItineraryExplanationProvider CreateProvider(
        HttpClient client,
        ExplanationProviderOptions? customOptions = null,
        IAiProviderBudget? budget = null) =>
        new(
            client,
            Options.Create(customOptions ?? new ExplanationProviderOptions
            {
                Enabled = true,
                Endpoint = Endpoint,
                ApiKey = FakeApiKey,
                ModelName = "gemini-3.5-flash-lite",
                RetryBaseDelayMilliseconds = 0,
            }),
            budget: budget);

    private static ItineraryExplanationInput Request() => new(
        [
            new ItineraryExplanationItem(
                1,
                7L,
                "Bảo tàng",
                "Văn hóa",
                ["di sản"],
                ItineraryItemKind.Visit,
                false,
                new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.Zero),
                60,
                100_000m,
                null,
                "Selected by deterministic scheduler."),
        ],
        new ItineraryExplanationContext(
            ["văn hóa"],
            new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero),
            "Asia/Ho_Chi_Minh",
            60));

    private static ItineraryExplanationInput RestRequest() => new(
        [
            new ItineraryExplanationItem(
                1,
                null,
                null,
                null,
                [],
                ItineraryItemKind.Rest,
                false,
                new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 1, 2, 30, 0, TimeSpan.Zero),
                30,
                null,
                null,
                "Scheduled rest."),
        ],
        new ItineraryExplanationContext(
            [],
            new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero),
            "Asia/Ho_Chi_Minh",
            30));

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class DenyBudget : IAiProviderBudget
    {
        public bool TryAcquire(string provider, out IDisposable? permit)
        {
            permit = null;
            return false;
        }
    }

    private static string CompletedResponse(string outputText) => JsonSerializer.Serialize(new
    {
        status = "completed",
        steps = new[]
        {
            new
            {
                type = "model_output",
                content = new[] { new { type = "text", text = outputText } },
            },
        },
    });

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public HttpRequestMessage? Request { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }

    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
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
}
