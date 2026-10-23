using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Infrastructure.Ai;
using TripMate.Infrastructure.AiRanking;
using TripMate.Infrastructure.TripMateAi;

namespace TripMate.Infrastructure.UnitTests.TripMateAi;

/// <summary>
/// Edge cases of <see cref="TripMateAiPoiRankingProvider"/> that the patch's own tests do not cover
/// (spec UC-10-tripmate-ai-provider §12.2, RT1–RT7).
/// </summary>
public sealed class TripMateAiPoiRankingProviderTests
{
    private const string Endpoint = "http://ai:8000/v1/rank-pois";
    private const string SharedKey = "test-only-shared-key";

    // RT1
    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, PoiRankingProviderErrorCodes.InvalidResponse)]
    [InlineData(HttpStatusCode.BadRequest, PoiRankingProviderErrorCodes.InvalidResponse)]
    [InlineData(HttpStatusCode.Forbidden, PoiRankingProviderErrorCodes.InvalidResponse)]
    [InlineData(HttpStatusCode.InternalServerError, PoiRankingProviderErrorCodes.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, PoiRankingProviderErrorCodes.ServerError)]
    public async Task RankAsync_MapsHttpStatusToErrorCode(HttpStatusCode status, string expectedCode)
    {
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status));
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.RankAsync(Request(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(expectedCode);
        handler.CallCount.Should().Be(1);
    }

    // RT2
    [Fact]
    public async Task RankAsync_WhenCallerCancelsDuringTheSend_PropagatesCancellation()
    {
        using var handler = new CancellationObservingHttpMessageHandler();
        var provider = CreateProvider(new HttpClient(handler));
        using var cancellation = new CancellationTokenSource();

        Task operation = provider.RankAsync(Request(), cancellation.Token);
        await handler.Called.Task;
        cancellation.Cancel();

        Func<Task> action = () => operation;
        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.SuppliedToken.IsCancellationRequested.Should().BeTrue();
    }

    // RT2
    [Fact]
    public async Task RankAsync_WithAnAlreadyCancelledToken_ThrowsBeforeAnyRequest()
    {
        using var handler = new StubHttpMessageHandler(_ => Json("""{"ranked":[]}"""));
        var provider = CreateProvider(new HttpClient(handler));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> action = () => provider.RankAsync(Request(), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.CallCount.Should().Be(0);
    }

    // RT3
    [Fact]
    public async Task RankAsync_SendsExactlyTheContractFields_AndTheKeyOnlyInTheHeader()
    {
        using var handler = new StubHttpMessageHandler(_ => Json("""{"ranked":[]}"""));
        var provider = CreateProvider(new HttpClient(handler));

        await provider.RankAsync(Request(), CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.RequestBody!);
        PropertyNames(body.RootElement).Should().BeEquivalentTo(["preferenceTokens", "candidates"]);
        foreach (JsonElement candidate in body.RootElement.GetProperty("candidates").EnumerateArray())
        {
            PropertyNames(candidate).Should().BeEquivalentTo(["poiId", "name", "categoryName", "tagNames"]);
        }

        handler.RequestBody.Should().NotContain("model", "the TripMate AI service picks its own model");
        handler.RequestBody.Should().NotContain(SharedKey);
        handler.Request!.Headers.GetValues(TripMateAiHeaders.ApiKey).Should().ContainSingle()
            .Which.Should().Be(SharedKey);
    }

    // RT4
    [Fact]
    public async Task RankAsync_PassesReasonsThroughUnchanged()
    {
        string longReason = new('a', 700);
        string responseBody = JsonSerializer.Serialize(new
        {
            ranked = new object[]
            {
                new { poiId = 7, aiScore = 0.8m, reason = (string?)null },
                new { poiId = 8, aiScore = 0.2m, reason = longReason },
            },
        });
        using var handler = new StubHttpMessageHandler(_ => Json(responseBody));
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.RankAsync(Request(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Ranked.Should().HaveCount(2);
        result.Value.Ranked.Single(item => item.PoiId == 7).Reason.Should().BeNull();
        result.Value.Ranked.Single(item => item.PoiId == 8).Reason.Should().Be(
            longReason,
            "the orchestrator, not the provider, truncates reasons");
    }

    // RT5
    [Theory]
    [InlineData("""{"ranked":null}""")]
    [InlineData("""{"ranked":[null]}""")]
    [InlineData("""{"ranked":[{"aiScore":0.5,"reason":null}]}""")]
    [InlineData("")]
    public async Task RankAsync_MalformedRankedItems_ReturnInvalidResponse(string responseBody)
    {
        using var handler = new StubHttpMessageHandler(_ => Json(responseBody));
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.RankAsync(Request(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PoiRankingProviderErrorCodes.InvalidResponse);
    }

    // RT5
    [Fact]
    public async Task RankAsync_IgnoresUnknownExtraFields()
    {
        using var handler = new StubHttpMessageHandler(_ => Json("""
            {"ranked":[{"poiId":7,"aiScore":0.6,"reason":null,"model":"x"}],"version":"1.0"}
            """));
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.RankAsync(Request(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Ranked.Should().ContainSingle().Which.AiScore.Should().Be(0.6m);
    }

    // RT6
    [Fact]
    public async Task RankAsync_FailureWhileReadingTheBody_ReturnsNetwork()
    {
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ThrowingHttpContent(new IOException("connection reset while reading")),
        });
        var provider = CreateProvider(new HttpClient(handler));

        var result = await provider.RankAsync(Request(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PoiRankingProviderErrorCodes.Network);
    }

    // RT7
    [Fact]
    public async Task RankAsync_ReleasesTheBudgetPermitAfterEachSend()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));
        var budget = new InMemoryAiProviderBudget(
            clock,
            new AiProviderBudgetSettings(RateLimitPerMinute: 10, MaxConcurrency: 1),
            new AiProviderBudgetSettings(RateLimitPerMinute: 10, MaxConcurrency: 1));
        using var handler = new StubHttpMessageHandler(_ => Json("""{"ranked":[]}"""));
        var provider = new TripMateAiPoiRankingProvider(new HttpClient(handler), Options.Create(RankingOptions()), budget);

        var first = await provider.RankAsync(Request(), CancellationToken.None);
        var second = await provider.RankAsync(Request(), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue("the permit of the first call must have been released");
        handler.CallCount.Should().Be(2);
    }

    private static PoiRankingProviderOptions RankingOptions() => new()
    {
        Enabled = true,
        Provider = AiProviderKind.TripMateAi,
        Endpoint = Endpoint,
        ApiKey = SharedKey,
    };

    private static TripMateAiPoiRankingProvider CreateProvider(HttpClient client) =>
        new(client, Options.Create(RankingOptions()));

    private static PoiRankingRequest Request() => new(
        [
            new PoiRankingCandidate(7L, "Bảo tàng Điêu khắc Chăm", "Bảo tàng", ["văn hóa"]),
            new PoiRankingCandidate(8L, "Bãi biển Mỹ Khê", "Bãi biển", ["biển"]),
        ],
        new PoiRankingContext(["van hoa", "bien"]));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static IEnumerable<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name);

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = now;
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