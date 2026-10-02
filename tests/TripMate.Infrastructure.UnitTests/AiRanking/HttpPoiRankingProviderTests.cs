using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Infrastructure.AiRanking;

namespace TripMate.Infrastructure.UnitTests.AiRanking;

public sealed class HttpPoiRankingProviderTests
{
    private const string Endpoint =
        "https://generativelanguage.googleapis.com/v1/interactions";
    private const string FakeApiKey = "test-only-gemini-key";

    [Fact]
    public async Task RankAsync_WithCompletedResponse_SendsSemanticRequestAndMapsResult()
    {
        using var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {
              "status": "completed",
              "steps": [
                {
                  "type": "model_output",
                  "content": [
                    {
                      "type": "text",
                      "text": "{\"ranked\":[{\"poiId\":7,\"aiScore\":0.75,\"reason\":\"Strong culture match\"},{\"poiId\":8,\"aiScore\":0.25}]}"
                    }
                  ]
                }
              ]
            }
            """));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var request = new PoiRankingRequest(
            [
                new PoiRankingCandidate(7L, "Museum", "Culture", ["heritage"]),
                new PoiRankingCandidate(8L, "Forest", "Nature", ["hiking"]),
            ],
            new PoiRankingContext(["culture", "museum"]));

        var result = await provider.RankAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Ranked.Should().BeEquivalentTo(
            [
                new PoiRankingItem(7L, 0.75m, "Strong culture match"),
                new PoiRankingItem(8L, 0.25m, null),
            ],
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
        root.TryGetProperty("previous_interaction_id", out _).Should().BeFalse();
        root.TryGetProperty("stream", out _).Should().BeFalse();

        JsonElement responseFormat = root.GetProperty("response_format");
        responseFormat.GetProperty("type").GetString().Should().Be("text");
        responseFormat.GetProperty("mime_type").GetString().Should().Be("application/json");
        JsonElement schema = responseFormat.GetProperty("schema");
        schema.GetProperty("required")
            .EnumerateArray().Select(value => value.GetString()).Should().Equal("ranked");
        JsonElement rankedItems = schema.GetProperty("properties")
            .GetProperty("ranked").GetProperty("items");
        rankedItems.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()).Should().Equal("poiId", "aiScore");
        rankedItems.GetProperty("properties").EnumerateObject()
            .Select(property => property.Name)
            .Should().BeEquivalentTo("poiId", "aiScore", "reason");

        root.GetProperty("system_instruction").GetString().Should()
            .Contain("semantic POI relevance scorer")
            .And.Contain("Do not perform itinerary scheduling");
        string input = root.GetProperty("input").GetString()!;
        using JsonDocument semanticInput = JsonDocument.Parse(input);
        semanticInput.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("preferenceTokens", "candidates");
        semanticInput.RootElement.GetProperty("preferenceTokens")
            .EnumerateArray().Select(value => value.GetString())
            .Should().Equal("culture", "museum");
        JsonElement firstCandidate = semanticInput.RootElement
            .GetProperty("candidates")[0];
        firstCandidate.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("poiId", "name", "categoryName", "tagNames");
        firstCandidate.GetProperty("poiId").GetInt64().Should().Be(7L);
        firstCandidate.GetProperty("name").GetString().Should().Be("Museum");
        firstCandidate.GetProperty("categoryName").GetString().Should().Be("Culture");
        firstCandidate.GetProperty("tagNames").EnumerateArray()
            .Select(value => value.GetString()).Should().Equal("heritage");
        handler.RequestBody.Should().NotContain(FakeApiKey);
    }

    [Fact]
    public async Task RankAsync_WhenLocalBudgetDenied_DoesNotCallProvider()
    {
        using var handler = new StubHttpMessageHandler(_ => JsonResponse("{}"));
        var provider = CreateProvider(new HttpClient(handler), new DenyBudget());

        var result = await provider.RankAsync(Request(), CancellationToken.None);

        result.ErrorCode.Should().Be(PoiRankingProviderErrorCodes.Quota);
        handler.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, PoiRankingProviderErrorCodes.Quota)]
    [InlineData(HttpStatusCode.InternalServerError, PoiRankingProviderErrorCodes.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PoiRankingProviderErrorCodes.ServerError)]
    [InlineData(HttpStatusCode.BadRequest, PoiRankingProviderErrorCodes.InvalidResponse)]
    [InlineData(HttpStatusCode.Unauthorized, PoiRankingProviderErrorCodes.InvalidResponse)]
    [InlineData(HttpStatusCode.Forbidden, PoiRankingProviderErrorCodes.InvalidResponse)]
    [InlineData(HttpStatusCode.NotFound, PoiRankingProviderErrorCodes.InvalidResponse)]
    public async Task RankAsync_WithNonSuccessStatus_ReturnsSanitizedFailure(
        HttpStatusCode statusCode,
        string expectedErrorCode)
    {
        string sensitiveBody = $"provider detail includes {FakeApiKey}";
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(sensitiveBody),
        });
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.RankAsync(Request(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(expectedErrorCode);
        result.ErrorMessage.Should().NotContain(FakeApiKey).And.NotContain(sensitiveBody);
        handler.CallCount.Should().Be(1);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("io")]
    public async Task RankAsync_WithTransportFailure_ReturnsNetworkFailure(string failureKind)
    {
        Exception exception = failureKind == "http"
            ? new HttpRequestException($"transport detail {FakeApiKey}")
            : new IOException($"stream detail {FakeApiKey}");
        using var client = new HttpClient(new ThrowingHttpMessageHandler(exception));
        var provider = CreateProvider(client);

        var result = await provider.RankAsync(Request(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PoiRankingProviderErrorCodes.Network);
        result.ErrorMessage.Should().NotContain(FakeApiKey).And.NotContain("detail");
    }

    [Fact]
    public async Task RankAsync_WithUnexpectedException_Propagates()
    {
        using var client = new HttpClient(
            new ThrowingHttpMessageHandler(new InvalidOperationException("provider defect")));
        var provider = CreateProvider(client);

        Func<Task> action = () => provider.RankAsync(Request(), CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RankAsync_WhenResponseContentReadFails_ReturnsNetworkFailure()
    {
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ThrowingHttpContent(new IOException(
                $"response stream detail {FakeApiKey}")),
        });
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.RankAsync(Request(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PoiRankingProviderErrorCodes.Network);
        result.ErrorMessage.Should().NotContain(FakeApiKey).And.NotContain("stream detail");
    }

    [Fact]
    public async Task RankAsync_WithBusinessInvalidItems_PreservesNeutralCarrierForW07()
    {
        string longReason = new('x', 700);
        string output = JsonSerializer.Serialize(new
        {
            ranked = new object[]
            {
                new { poiId = 7L, aiScore = 1.25m, reason = longReason },
                new { poiId = 7L, aiScore = -0.25m, reason = (string?)null },
            },
        });
        using var handler = new StubHttpMessageHandler(_ => JsonResponse(
            CompletedResponse(output)));
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.RankAsync(Request(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Ranked.Should().HaveCount(2);
        result.Value.Ranked.Select(item => item.PoiId).Should().Equal(7L, 7L);
        result.Value.Ranked.Select(item => item.AiScore).Should().Equal(1.25m, -0.25m);
        result.Value.Ranked.First().Reason.Should().Be(longReason);
    }

    [Theory]
    [MemberData(nameof(MalformedResponseCases))]
    public async Task RankAsync_WithMalformedProviderOutput_ReturnsInvalidResponse(
        string responseBody)
    {
        using var handler = new StubHttpMessageHandler(_ => JsonResponse(responseBody));
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.RankAsync(Request(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PoiRankingProviderErrorCodes.InvalidResponse);
        result.ErrorMessage.Should().NotContain(responseBody);
    }

    [Fact]
    public async Task RankAsync_WhenCallerCancels_PropagatesCancellationThroughHandler()
    {
        using var handler = new CancellationObservingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);
        using var cancellation = new CancellationTokenSource();

        Task operation = provider.RankAsync(Request(), cancellation.Token);
        await handler.Called.Task;
        cancellation.Cancel();

        Func<Task> action = () => operation;
        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.SuppliedToken.CanBeCanceled.Should().BeTrue();
        handler.SuppliedToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task RankAsync_WithPreCancelledToken_PropagatesCancellation()
    {
        using var handler = new StubHttpMessageHandler(_ => JsonResponse(
            CompletedResponse("{\"ranked\":[]}")));
        var provider = CreateProvider(new HttpClient(handler));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> action = () => provider.RankAsync(Request(), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RankAsync_WhenHandlerThrowsCancellation_Propagates()
    {
        using var client = new HttpClient(new ThrowingHttpMessageHandler(
            new OperationCanceledException("provider call cancelled")));
        var provider = CreateProvider(client);

        Func<Task> action = () => provider.RankAsync(Request(), CancellationToken.None);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    public static TheoryData<string> MalformedResponseCases => new()
    {
        "{",
        """{"status":"failed","steps":[]}""",
        """{"status":"completed"}""",
        """{"status":"completed","steps":[]}""",
        CompletedResponse("not-json"),
        CompletedResponse("{}"),
        CompletedResponse("{\"ranked\":{}}"),
        CompletedResponse("{\"ranked\":[{\"aiScore\":0.5}]}"),
        CompletedResponse("{\"ranked\":[{\"poiId\":7}]}"),
        CompletedResponse("{\"ranked\":[{\"poiId\":\"seven\",\"aiScore\":0.5}]}"),
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
                        new { type = "text", text = "{\"ranked\":[]}" },
                        new { type = "text", text = "{\"ranked\":[]}" },
                    },
                },
            },
        }),
    };

    private static HttpPoiRankingProvider CreateProvider(
        HttpClient client,
        IAiProviderBudget? budget = null) =>
        new(
            client,
            Options.Create(new PoiRankingProviderOptions
            {
                Enabled = true,
                Endpoint = Endpoint,
                ApiKey = FakeApiKey,
                ModelName = "gemini-3.5-flash-lite",
            }),
            budget);

    private static PoiRankingRequest Request() => new(
        [new PoiRankingCandidate(7L, "Museum", "Culture", ["heritage"])],
        new PoiRankingContext(["culture"]));

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string CompletedResponse(string outputText) => JsonSerializer.Serialize(new
    {
        status = "completed",
        steps = new[]
        {
            new
            {
                type = "model_output",
                content = new[]
                {
                    new { type = "text", text = outputText },
                },
            },
        },
    });

    private sealed class DenyBudget : IAiProviderBudget
    {
        public bool TryAcquire(string provider, out IDisposable? permit)
        {
            permit = null;
            return false;
        }
    }

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

        public CancellationToken SuppliedToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SuppliedToken = cancellationToken;
            Called.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
        }
    }

    private sealed class ThrowingHttpContent(Exception exception) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            Task.FromException(exception);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}