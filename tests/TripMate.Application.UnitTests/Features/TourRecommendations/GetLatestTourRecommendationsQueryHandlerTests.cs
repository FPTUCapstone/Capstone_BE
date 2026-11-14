using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TourRecommendations.GetLatest;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.TourRecommendations;

public sealed class GetLatestTourRecommendationsQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_WithoutSnapshot_ReturnsContractualEmptyPage()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetLatestTourRecommendationsQueryHandler(db, new User(7), new Clock());

        var result = await handler.Handle(new GetLatestTourRecommendationsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Page.Should().Be(1);
        result.Value.PageSize.Should().Be(20);
        result.Value.TotalCount.Should().Be(0);
        result.Value.Criteria.Should().BeNull();
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_SelectsHighestSnapshotIdForOnlyCurrentTraveler()
    {
        await using var db = TestDbContext.Create();
        db.TourSearchRequests.AddRange(
            Snapshot(7, "Old", Now.AddMinutes(10)),
            Snapshot(8, "Other traveler", Now.AddMinutes(30)),
            Snapshot(7, "Latest by id", Now));
        await db.SaveChangesAsync();
        var handler = new GetLatestTourRecommendationsQueryHandler(db, new User(7), new Clock());

        var result = await handler.Handle(new GetLatestTourRecommendationsQuery(), CancellationToken.None);

        result.Value.Criteria!.Destination.Should().Be("Latest by id");
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public void Validator_RejectsNonPositivePage()
    {
        new GetLatestTourRecommendationsQueryValidator()
            .Validate(new GetLatestTourRecommendationsQuery(0))
            .IsValid.Should().BeFalse();
    }

    private static TourSearchRequest Snapshot(long travelerId, string destination, DateTimeOffset requestedAt) =>
        TourSearchRequest.Create(travelerId, destination, null, null, null, null, "[\"beach\"]", requestedAt);

    private sealed record User(long Id) : ICurrentUserService
    {
        public long? UserId => Id;
        public string? Role => "Traveler";
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }
}