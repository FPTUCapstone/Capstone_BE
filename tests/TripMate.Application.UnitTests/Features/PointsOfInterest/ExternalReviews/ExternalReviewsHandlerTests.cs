using FluentAssertions;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.PointsOfInterest.ExternalReviews;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.ExternalReviews;

public sealed class ExternalReviewsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetReviews_LinkedPoi_ReturnsTripadvisorRatingAndReviews()
    {
        await using var dbContext = TestDbContext.Create();
        var poi = await AddPoiAsync(dbContext, tripadvisorId: 454980);
        var handler = new GetPoiExternalReviewsQueryHandler(dbContext, new FakeTripadvisor(), new FakeTranslator(enabled: true));

        var result = await handler.Handle(new GetPoiExternalReviewsQuery(poi.Id), CancellationToken.None);

        result.Value.Source.Should().Be(GetPoiExternalReviewsQueryHandler.TripadvisorSource);
        result.Value.Rating.Should().Be(4.2m);
        result.Value.ReviewCount.Should().Be(8268);
        result.Value.Reviews.Should().ContainSingle(review => review.Id == 7);
        result.Value.TranslationAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task GetReviews_UnlinkedPoi_ReturnsNoSourceWithoutCallingTripadvisor()
    {
        await using var dbContext = TestDbContext.Create();
        var poi = await AddPoiAsync(dbContext, tripadvisorId: null);
        var tripadvisor = new FakeTripadvisor();

        var result = await new GetPoiExternalReviewsQueryHandler(dbContext, tripadvisor, new FakeTranslator(enabled: true))
            .Handle(new GetPoiExternalReviewsQuery(poi.Id), CancellationToken.None);

        result.Value.Source.Should().BeNull();
        result.Value.Reviews.Should().BeEmpty();
        tripadvisor.Calls.Should().Be(0);
    }

    [Fact]
    public async Task GetReviews_UnknownPoi_IsNotFound()
    {
        await using var dbContext = TestDbContext.Create();

        var result = await new GetPoiExternalReviewsQueryHandler(dbContext, new FakeTripadvisor(), new FakeTranslator(enabled: true))
            .Handle(new GetPoiExternalReviewsQuery(404), CancellationToken.None);

        result.ErrorCode.Should().Be(ExternalReviewErrorCodes.PoiNotFound);
    }

    [Fact]
    public async Task Translate_ShownReview_ReturnsMachineTranslation()
    {
        await using var dbContext = TestDbContext.Create();
        var poi = await AddPoiAsync(dbContext, tripadvisorId: 454980);

        var result = await new TranslateExternalReviewQueryHandler(dbContext, new FakeTripadvisor(), new FakeTranslator(enabled: true))
            .Handle(new TranslateExternalReviewQuery(poi.Id, 7), CancellationToken.None);

        result.Value.Text.Should().Be("vi:Too crowded at noon.");
        result.Value.MachineTranslated.Should().BeTrue();
        result.Value.Language.Should().Be("vi");
    }

    [Fact]
    public async Task Translate_ReviewNotShownForThisPoi_IsNotFound()
    {
        await using var dbContext = TestDbContext.Create();
        var poi = await AddPoiAsync(dbContext, tripadvisorId: 454980);

        var result = await new TranslateExternalReviewQueryHandler(dbContext, new FakeTripadvisor(), new FakeTranslator(enabled: true))
            .Handle(new TranslateExternalReviewQuery(poi.Id, 999), CancellationToken.None);

        result.ErrorCode.Should().Be(ExternalReviewErrorCodes.ReviewNotFound);
    }

    [Fact]
    public async Task Translate_AReviewBeyondTheFirstFive_IsFound()
    {
        await using var dbContext = TestDbContext.Create();
        var poi = await AddPoiAsync(dbContext, tripadvisorId: 454980);
        var tripadvisor = new PagedTripadvisor();

        var result = await new TranslateExternalReviewQueryHandler(dbContext, tripadvisor, new FakeTranslator(enabled: true))
            .Handle(new TranslateExternalReviewQuery(poi.Id, 8), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ReviewId.Should().Be(8);
    }

    [Fact]
    public async Task Translate_WhenTranslationIsOff_IsUnavailable()
    {
        await using var dbContext = TestDbContext.Create();
        var poi = await AddPoiAsync(dbContext, tripadvisorId: 454980);

        var result = await new TranslateExternalReviewQueryHandler(dbContext, new FakeTripadvisor(), new FakeTranslator(enabled: false))
            .Handle(new TranslateExternalReviewQuery(poi.Id, 7), CancellationToken.None);

        result.ErrorCode.Should().Be(ExternalReviewErrorCodes.TranslationUnavailable);
    }

    private static async Task<PointOfInterest> AddPoiAsync(TestDbContext dbContext, long? tripadvisorId)
    {
        var poi = PointOfInterest.Create(PoiCategory.Create("Natural attraction", null), "Marble Mountains", 16.0039m, 108.2642m, 1, Now);
        poi.LinkTripadvisorLocation(tripadvisorId);
        dbContext.PointsOfInterest.Add(poi);
        await dbContext.SaveChangesAsync();
        return poi;
    }

    private sealed class FakeTripadvisor : ITripadvisorClient
    {
        public int Calls { get; private set; }

        public bool IsEnabled => true;

        public Task<IReadOnlyDictionary<long, TripadvisorRating>> GetRatingsAsync(
            IReadOnlyCollection<long> locationIds,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<long, TripadvisorRating>>(locationIds.ToDictionary(
                id => id,
                id => new TripadvisorRating(id, 4.2m, 8268, "https://ta/4.0.png", "https://ta/" + id)));
        }

        public Task<IReadOnlyList<ExternalReviewDto>> GetReviewsAsync(
            long locationId,
            int size,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ExternalReviewDto>>(
                [new ExternalReviewDto(7, 3, "Busy", "Too crowded at noon.", "en", Now, "2026-10", "Lan", "https://ta/r7", null)]);
        }
    }

    /// <summary>Returns as many reviews as asked for, with ids 1..size.</summary>
    private sealed class PagedTripadvisor : ITripadvisorClient
    {
        public bool IsEnabled => true;

        public Task<IReadOnlyDictionary<long, TripadvisorRating>> GetRatingsAsync(
            IReadOnlyCollection<long> locationIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<long, TripadvisorRating>>(new Dictionary<long, TripadvisorRating>());

        public Task<IReadOnlyList<ExternalReviewDto>> GetReviewsAsync(
            long locationId,
            int size,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalReviewDto>>(Enumerable.Range(1, size)
                .Select(id => new ExternalReviewDto(id, 4, "t", "x", "en", Now, null, "u", null, null))
                .ToArray());
    }

    private sealed class FakeTranslator(bool enabled) : IReviewTranslator
    {
        public bool IsEnabled => enabled;

        public Task<Result<TranslatedReview>> TranslateAsync(
            string cacheKey,
            string? title,
            string? text,
            string targetLanguage,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(new TranslatedReview($"{targetLanguage}:{title}", $"{targetLanguage}:{text}")));
    }
}