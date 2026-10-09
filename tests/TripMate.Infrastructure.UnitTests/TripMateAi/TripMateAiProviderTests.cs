using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Ai;
using TripMate.Infrastructure.AiExplanation;
using TripMate.Infrastructure.AiRanking;
using TripMate.Infrastructure.TripMateAi;

namespace TripMate.Infrastructure.UnitTests.TripMateAi;

public sealed class TripMateAiProviderTests
{
    private const string RankingEndpoint = "http://ai:8000/v1/rank-pois";
    private const string ExplanationEndpoint = "http://ai:8000/v1/explain-itinerary";
    private const string SharedKey = "test-only-shared-key";

    // ---------------- Xếp hạng POI ----------------

    [Fact]
    public async Task RankAsync_SendsCamelCaseRequestWithKey_AndMapsResult()
    {
        var handler = new StubHandler(_ => Json("""
            {"ranked":[{"poiId":7,"aiScore":0.82,"reason":"Khớp sở thích: văn hóa"},{"poiId":8,"aiScore":0.1,"reason":null}]}
            """));
        var provider = RankingProvider(handler);

        var result = await provider.RankAsync(RankingRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Ranked.Select(item => item.PoiId).Should().Equal(7L, 8L);
        result.Value.Ranked.Select(item => item.AiScore).Should().Equal(0.82m, 0.1m);
        handler.LastRequestUri.Should().Be(RankingEndpoint);
        handler.LastApiKey.Should().Be(SharedKey);

        using JsonDocument sent = JsonDocument.Parse(handler.LastBody!);
        sent.RootElement.GetProperty("preferenceTokens")[0].GetString().Should().Be("văn hóa");
        sent.RootElement.GetProperty("candidates")[0].GetProperty("poiId").GetInt64().Should().Be(7);
        sent.RootElement.GetProperty("candidates")[0].GetProperty("categoryName").GetString().Should().Be("Bảo tàng");
    }

    [Theory]
    [InlineData("""{"ranked":[{"poiId":7}]}""")]
    [InlineData("""{"items":[]}""")]
    [InlineData("not json")]
    public async Task RankAsync_InvalidBody_ReturnsInvalidResponse(string body)
    {
        var provider = RankingProvider(new StubHandler(_ => Json(body)));

        var result = await provider.RankAsync(RankingRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PoiRankingProviderErrorCodes.InvalidResponse);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, PoiRankingProviderErrorCodes.Quota)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PoiRankingProviderErrorCodes.ServerError)]
    [InlineData(HttpStatusCode.GatewayTimeout, PoiRankingProviderErrorCodes.ServerError)]
    [InlineData(HttpStatusCode.Unauthorized, PoiRankingProviderErrorCodes.InvalidResponse)]
    public async Task RankAsync_HttpFailure_MapsErrorCode(HttpStatusCode status, string expectedCode)
    {
        var provider = RankingProvider(new StubHandler(_ => new HttpResponseMessage(status)));

        var result = await provider.RankAsync(RankingRequest(), CancellationToken.None);

        result.ErrorCode.Should().Be(expectedCode);
    }

    [Fact]
    public async Task RankAsync_NetworkFailure_ReturnsNetworkError()
    {
        var provider = RankingProvider(new StubHandler(_ => throw new HttpRequestException("refused")));

        var result = await provider.RankAsync(RankingRequest(), CancellationToken.None);

        result.ErrorCode.Should().Be(PoiRankingProviderErrorCodes.Network);
    }

    [Fact]
    public async Task RankAsync_LocalBudgetDenied_DoesNotCallService()
    {
        var handler = new StubHandler(_ => Json("""{"ranked":[]}"""));
        var provider = new TripMateAiPoiRankingProvider(
            new HttpClient(handler),
            Options.Create(RankingOptions()),
            new DenyBudget());

        var result = await provider.RankAsync(RankingRequest(), CancellationToken.None);

        result.ErrorCode.Should().Be(PoiRankingProviderErrorCodes.Quota);
        handler.CallCount.Should().Be(0);
    }

    // ---------------- Giải thích lịch trình ----------------

    [Fact]
    public async Task ExplainAsync_SendsItineraryInputWithKey_AndReturnsValidatedResult()
    {
        var handler = new StubHandler(_ => Json("""
            {"items":[
              {"sequenceNo":1,"poiId":12,"friendlyExplanation":"  Bảo tàng Chăm hợp với sở thích văn hóa của bạn.  "},
              {"sequenceNo":2,"poiId":null,"friendlyExplanation":"Nghỉ ngơi để giữ sức."}
            ]}
            """));
        var provider = ExplanationProvider(handler);

        var result = await provider.ExplainAsync(ExplanationInput(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(item => item.FriendlyExplanation).Should().Equal(
            "Bảo tàng Chăm hợp với sở thích văn hóa của bạn.",
            "Nghỉ ngơi để giữ sức.");
        handler.LastRequestUri.Should().Be(ExplanationEndpoint);
        handler.LastApiKey.Should().Be(SharedKey);

        using JsonDocument sent = JsonDocument.Parse(handler.LastBody!);
        JsonElement firstItem = sent.RootElement.GetProperty("items")[0];
        firstItem.GetProperty("kind").GetString().Should().Be("visit");
        firstItem.GetProperty("poiName").GetString().Should().Be("Bảo tàng Điêu khắc Chăm");
        sent.RootElement.GetProperty("context").GetProperty("timeZoneId").GetString().Should().Be("Asia/Ho_Chi_Minh");
    }

    [Theory]
    [InlineData("""{"items":[{"sequenceNo":1,"poiId":12,"friendlyExplanation":"a","extra":1},{"sequenceNo":2,"poiId":null,"friendlyExplanation":"b"}]}""")]
    [InlineData("""{"items":[{"sequenceNo":1,"poiId":99,"friendlyExplanation":"a"},{"sequenceNo":2,"poiId":null,"friendlyExplanation":"b"}]}""")]
    [InlineData("""{"items":[{"sequenceNo":1,"poiId":12,"friendlyExplanation":"a"}]}""")]
    [InlineData("""{"items":[{"sequenceNo":1,"poiId":12,"friendlyExplanation":" "},{"sequenceNo":2,"poiId":null,"friendlyExplanation":"b"}]}""")]
    public async Task ExplainAsync_ResultFailingValidation_ReturnsInvalidResponse(string body)
    {
        var provider = ExplanationProvider(new StubHandler(_ => Json(body)));

        var result = await provider.ExplainAsync(ExplanationInput(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.InvalidResponse);
    }

    [Fact]
    public async Task ExplainAsync_ServiceUnavailableOnce_RetriesAndSucceeds()
    {
        int calls = 0;
        var handler = new StubHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json("""{"items":[{"sequenceNo":1,"poiId":12,"friendlyExplanation":"a"},{"sequenceNo":2,"poiId":null,"friendlyExplanation":"b"}]}"""));
        var provider = ExplanationProvider(handler);

        var result = await provider.ExplainAsync(ExplanationInput(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        handler.CallCount.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, ExplanationProviderErrorCodes.ServerError)]
    [InlineData(HttpStatusCode.GatewayTimeout, ExplanationProviderErrorCodes.Timeout)]
    [InlineData(HttpStatusCode.Unauthorized, ExplanationProviderErrorCodes.InvalidResponse)]
    public async Task ExplainAsync_NonRetryableFailure_DoesNotRetry(HttpStatusCode status, string expectedCode)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));
        var provider = ExplanationProvider(handler);

        var result = await provider.ExplainAsync(ExplanationInput(), CancellationToken.None);

        result.ErrorCode.Should().Be(expectedCode);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ExplainAsync_NetworkFailure_ReturnsNetworkError()
    {
        var provider = ExplanationProvider(new StubHandler(_ => throw new HttpRequestException("refused")));

        var result = await provider.ExplainAsync(ExplanationInput(), CancellationToken.None);

        result.ErrorCode.Should().Be(ExplanationProviderErrorCodes.Network);
    }

    // ---------------- Cấu hình và DI ----------------

    [Fact]
    public void Validators_AcceptInternalHttpEndpointWithoutModelName_ForTripMateAi()
    {
        new PoiRankingProviderOptionsValidator().Validate(null, RankingOptions()).Succeeded.Should().BeTrue();
        new ExplanationProviderOptionsValidator().Validate(null, ExplanationOptions()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validators_StillRequireHttpsAndModelName_ForGemini()
    {
        var options = new PoiRankingProviderOptions
        {
            Enabled = true,
            Provider = AiProviderKind.Gemini,
            Endpoint = RankingEndpoint,
            ApiKey = SharedKey,
        };

        var result = new PoiRankingProviderOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("HTTPS");
        result.FailureMessage.Should().Contain(nameof(PoiRankingProviderOptions.ModelName));
    }

    [Fact]
    public void Validators_RequireSharedKey_ForTripMateAi()
    {
        var options = new ExplanationProviderOptions
        {
            Enabled = true,
            Provider = AiProviderKind.TripMateAi,
            Endpoint = ExplanationEndpoint,
        };

        new ExplanationProviderOptionsValidator().Validate(null, options).Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("Gemini", false)]
    [InlineData("TripMateAi", true)]
    public void Registration_PicksProviderFromConfiguration(string? provider, bool expectTripMateAi)
    {
        var settings = new Dictionary<string, string?>();
        if (provider is not null)
        {
            settings["AiRanking:Provider"] = provider;
            settings["AiExplanation:Provider"] = provider;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<PoiRankingProviderOptions>();
        services.AddOptions<ExplanationProviderOptions>();
        services.AddPoiRankingHttpProvider(configuration);
        services.AddItineraryExplanationHttpProvider(configuration);
        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        IPoiRankingProvider ranking = serviceProvider.GetRequiredService<IPoiRankingProvider>();
        IItineraryExplanationProvider explanation = serviceProvider.GetRequiredService<IItineraryExplanationProvider>();

        (ranking is TripMateAiPoiRankingProvider).Should().Be(expectTripMateAi);
        (ranking is HttpPoiRankingProvider).Should().Be(!expectTripMateAi);
        (explanation is TripMateAiExplanationProvider).Should().Be(expectTripMateAi);
        (explanation is HttpItineraryExplanationProvider).Should().Be(!expectTripMateAi);
    }

    // ---------------- Hỗ trợ ----------------

    private static PoiRankingProviderOptions RankingOptions() => new()
    {
        Enabled = true,
        Provider = AiProviderKind.TripMateAi,
        Endpoint = RankingEndpoint,
        ApiKey = SharedKey,
    };

    private static ExplanationProviderOptions ExplanationOptions() => new()
    {
        Enabled = true,
        Provider = AiProviderKind.TripMateAi,
        Endpoint = ExplanationEndpoint,
        ApiKey = SharedKey,
        TimeoutSeconds = 5,
        OverallTimeoutSeconds = 10,
        MaxAttempts = 2,
        RetryBaseDelayMilliseconds = 0,
    };

    private static TripMateAiPoiRankingProvider RankingProvider(StubHandler handler) =>
        new(new HttpClient(handler), Options.Create(RankingOptions()));

    private static TripMateAiExplanationProvider ExplanationProvider(StubHandler handler) =>
        new(new HttpClient(handler), Options.Create(ExplanationOptions()));

    private static PoiRankingRequest RankingRequest() => new(
        [
            new PoiRankingCandidate(7L, "Bảo tàng Điêu khắc Chăm", "Bảo tàng", ["văn hóa"]),
            new PoiRankingCandidate(8L, "Bãi biển Mỹ Khê", "Bãi biển", ["biển"]),
        ],
        new PoiRankingContext(["văn hóa", "ẩm thực"]));

    private static ItineraryExplanationInput ExplanationInput()
    {
        var start = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        return new ItineraryExplanationInput(
            [
                new ItineraryExplanationItem(
                    1, 12L, "Bảo tàng Điêu khắc Chăm", "Bảo tàng", ["văn hóa"], ItineraryItemKind.Visit, true,
                    start.AddMinutes(30), start.AddMinutes(90), 60, 60_000m, 15, "Mandatory location"),
                new ItineraryExplanationItem(
                    2, null, null, null, [], ItineraryItemKind.Rest, false,
                    start.AddMinutes(105), start.AddMinutes(135), 30, null, null, "Free/rest time"),
            ],
            new ItineraryExplanationContext(["văn hóa"], start, "Asia/Ho_Chi_Minh", 135));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string? LastRequestUri { get; private set; }

        public string? LastApiKey { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastApiKey = request.Headers.TryGetValues(TripMateAiHeaders.ApiKey, out var values)
                ? values.Single()
                : null;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private sealed class DenyBudget : IAiProviderBudget
    {
        public bool TryAcquire(string provider, out IDisposable? permit)
        {
            permit = null;
            return false;
        }
    }
}