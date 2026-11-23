using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TourRecommendations.GetLatest;
using TripMate.Application.Features.TourRecommendations.Refresh;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TourRecommendations;

public sealed class RecommendationBookabilityTests
{
    private static readonly DateTimeOffset Now = PublicTourTestData.Now;

    [Fact]
    public async Task FreshCandidates_RejectInvalidTimeline_AndKeepLaterValidSchedule()
    {
        await using var db = TestDbContext.Create();
        await SeedAsync(db);
        var invalid = await AddTourAsync(db, valid: false);
        var valid = await AddTourAsync(db, valid: true);
        var reader = new TourRecommendationCandidateReader(db);
        var candidates = new List<TourRecommendationCandidate>();

        await foreach (var batch in reader.ReadBatchesAsync(Now, null, null, CancellationToken.None))
            candidates.AddRange(batch);

        candidates.Should().ContainSingle().Which.TourId.Should().Be(valid.Id);
        candidates.Single().RepresentativeSchedule.Id.Should().Be(valid.Schedules.Last().Id);
        candidates.Should().NotContain(candidate => candidate.TourId == invalid.Id);
    }

    [Fact]
    public async Task CachedRecommendations_FilterBeforeCountAndPaging_AndChooseValidSchedule()
    {
        await using var db = TestDbContext.Create();
        await SeedAsync(db);
        var snapshot = TourSearchRequest.Create(7, null, null, null, null, null, "[\"beach\"]", Now);
        db.TourSearchRequests.Add(snapshot);
        await db.SaveChangesAsync();
        for (var i = 0; i < 21; i++)
        {
            var invalid = await AddTourAsync(db, valid: false);
            db.TourRecommendationLogs.Add(TourRecommendationLog.CreateForPersistedSnapshot(
                snapshot.Id, invalid.Id, 99m, 80m, Now));
        }
        var valid = await AddTourAsync(db, valid: true);
        db.TourRecommendationLogs.Add(TourRecommendationLog.CreateForPersistedSnapshot(
            snapshot.Id, valid.Id, 90m, 80m, Now));
        await db.SaveChangesAsync();
        var handler = new GetLatestTourRecommendationsQueryHandler(db, new User(), new Clock());

        var result = await handler.Handle(new GetLatestTourRecommendationsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.TotalPages.Should().Be(1);
        result.Value.Items.Should().ContainSingle();
        result.Value.Items.Single().Tour.TourId.Should().Be(valid.Id.ToString());
        result.Value.Items.Single().Tour.RepresentativeScheduleId.Should().Be(valid.Schedules.Last().Id.ToString());
    }

    private static async Task SeedAsync(TestDbContext db)
    {
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await PublicTourTestData.SeedOperatorAsync(db, 8);
    }

    private static async Task<Tour> AddTourAsync(TestDbContext db, bool valid)
    {
        // A one-day window cannot fit the day-two stop. The later departure can.
        var tour = await PublicTourTestData.AddTourAsync(db, 8, TourStatus.Published,
            departures: [(Now.AddDays(10).AddHours(-3), 10, null), (Now.AddDays(12).AddHours(-3), 10, null)],
            stops: valid ? [(1, 1, 100L), (2, 1, 100L)] : [(1, 1, 100L), (1, 2, 100L)]);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.EndAtUtc))!
            .SetValue(tour.Schedules.First(), tour.Schedules.First().StartAtUtc.AddHours(12));
        await db.SaveChangesAsync();
        return tour;
    }

    private sealed class User : ICurrentUserService
    {
        public long? UserId => 7;
        public string? Role => nameof(UserRole.Traveler);
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }
}