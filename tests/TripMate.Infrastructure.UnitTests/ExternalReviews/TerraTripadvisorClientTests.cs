using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Infrastructure.ExternalReviews;

namespace TripMate.Infrastructure.UnitTests.ExternalReviews;

public sealed class TerraTripadvisorClientTests
{
    private const string LocationsBody = """
        { "data": [ { "id": 454980,
            "traveler_ratings": { "overall": { "rating": 4.2, "count": 8268, "icon_url": "https://ta/4.0.png" } },
            "urls": { "tripadvisor": { "main": "https://www.tripadvisor.com/marble" } } } ] }
        """;

    private const string ReviewsBody = """
        { "data": [ { "id": 1081457146, "publish_ts": "2026-10-12T02:01:37.503Z", "rating": 2,
            "travel_date": "2026-10", "url": "https://ta/r1",
            "rating_icon_url": { "url": "https://ta/2.0.png" },
            "user": { "username": "JoanRuff" },
            "title": [ { "language": "en", "value": "NO go!", "primary": true } ],
            "text": [ { "language": "en", "value": "Accosted by hawkers.", "primary": true } ] } ],
          "pagination": { "page": 1, "size": 1, "total_pages": 1, "total_elements": 1 } }
        """;

    [Fact]
    public async Task GetRatingsAsync_ReadsRatingCountIconAndUrl_WithTheKeyHeader()
    {
        var handler = new StubHandler(LocationsBody);
        var client = CreateClient(handler);

        var ratings = await client.GetRatingsAsync([454980], CancellationToken.None);

        var rating = ratings[454980];
        rating.Rating.Should().Be(4.2m);
        rating.ReviewCount.Should().Be(8268);
        rating.RatingImageUrl.Should().Be("https://ta/4.0.png");
        rating.Url.Should().Be("https://www.tripadvisor.com/marble");
        handler.LastRequest!.Headers.GetValues("X-API-Key").Should().Equal("test-key");
        handler.LastRequest.RequestUri!.Query.Should().Contain("id=454980");
    }

    [Fact]
    public async Task GetRatingsAsync_SecondCall_IsServedFromCache()
    {
        var handler = new StubHandler(LocationsBody);
        var client = CreateClient(handler);

        await client.GetRatingsAsync([454980], CancellationToken.None);
        await client.GetRatingsAsync([454980], CancellationToken.None);

        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task GetReviewsAsync_MapsPrimaryTitleTextAuthorAndDate()
    {
        var client = CreateClient(new StubHandler(ReviewsBody));

        var reviews = await client.GetReviewsAsync(454980, 5, CancellationToken.None);

        var review = reviews.Should().ContainSingle().Subject;
        review.Id.Should().Be(1081457146);
        review.Rating.Should().Be(2);
        review.Title.Should().Be("NO go!");
        review.Text.Should().Be("Accosted by hawkers.");
        review.Language.Should().Be("en");
        review.Author.Should().Be("JoanRuff");
        review.PublishedAt.Should().Be(new DateTimeOffset(2026, 10, 12, 2, 1, 37, 503, TimeSpan.Zero));
    }

    [Fact]
    public async Task GetReviewsAsync_DifferentPageSizes_ShareOneTripadvisorCall()
    {
        var handler = new StubHandler(ReviewsBody);
        var client = CreateClient(handler);

        await client.GetReviewsAsync(454980, 5, CancellationToken.None);
        await client.GetReviewsAsync(454980, 10, CancellationToken.None);

        handler.Calls.Should().Be(1);
        handler.LastRequest!.RequestUri!.Query.Should().Contain("size=10");
    }

    [Fact]
    public async Task GetRatingsAsync_LocationTripadvisorDoesNotReturn_IsNotAskedForAgain()
    {
        var handler = new StubHandler(LocationsBody);
        var client = CreateClient(handler);

        await client.GetRatingsAsync([454980, 111], CancellationToken.None);
        var second = await client.GetRatingsAsync([111], CancellationToken.None);

        second.Should().BeEmpty();
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Disabled_NeverCallsTripadvisor()
    {
        var handler = new StubHandler(LocationsBody);
        var client = CreateClient(handler, enabled: false);

        var ratings = await client.GetRatingsAsync([454980], CancellationToken.None);
        var reviews = await client.GetReviewsAsync(454980, 5, CancellationToken.None);

        ratings.Should().BeEmpty();
        reviews.Should().BeEmpty();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task DailyBudgetSpent_StopsCallingTripadvisor()
    {
        var handler = new StubHandler(LocationsBody);
        var client = CreateClient(handler, dailyBudget: 1);

        await client.GetReviewsAsync(1, 5, CancellationToken.None);
        var ratings = await client.GetRatingsAsync([454980], CancellationToken.None);

        ratings.Should().BeEmpty();
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ServerError_ReturnsEmptyInsteadOfThrowing()
    {
        var client = CreateClient(new StubHandler("oops", HttpStatusCode.InternalServerError));

        var ratings = await client.GetRatingsAsync([454980], CancellationToken.None);

        ratings.Should().BeEmpty();
    }

    private static TerraTripadvisorClient CreateClient(StubHandler handler, bool enabled = true, int dailyBudget = 40)
    {
        var options = Options.Create(new TripadvisorOptions
        {
            Enabled = enabled,
            ApiKey = "test-key",
            BaseUrl = "https://terra.example/api/",
            DailyCallBudget = dailyBudget,
        });
        return new TerraTripadvisorClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://terra.example/api/") },
            options,
            new MemoryCache(new MemoryCacheOptions()),
            new TripadvisorCallBudget(options, new FixedClock()),
            NullLogger<TerraTripadvisorClient>.Instance);
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => new(2026, 10, 16, 2, 0, 0, TimeSpan.Zero);
    }

    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}