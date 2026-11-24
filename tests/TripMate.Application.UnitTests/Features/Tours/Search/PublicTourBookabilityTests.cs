using FluentAssertions;

using TripMate.Application.Features.Tours.Shared;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Tours.Search;

public sealed class PublicTourBookabilityTests
{
    [Fact]
    public async Task Read_RejectsClosedFullPastCancelledAndInvalidTimelines_AndKeepsLaterEligibleDeparture()
    {
        await using var db = TestDbContext.Create();
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await PublicTourTestData.SeedOperatorAsync(db, 7);
        var now = PublicTourTestData.Now;
        var start = now.AddDays(10).AddHours(-3);
        var valid = PublicTourTestData.NewTour(7, departures: [(start, 20, null), (start.AddDays(1), 20, null)],
            stops: [(1, 1, 100L)]);
        var invalid = PublicTourTestData.NewTour(7, stops: [(1, 1, 100L), (1, 2, 100L)],
            departures: [(start, 20, null)]);
        var closed = PublicTourTestData.NewTour(7, stops: [(1, 1, 100L)], departures: [(start, 20, null)]);
        var past = PublicTourTestData.NewTour(7, stops: [(1, 1, 100L)], departures: [(now.AddDays(-1), 20, null)]);
        var cancelled = PublicTourTestData.NewTour(7, stops: [(1, 1, 100L)], departures: [(start, 20, null)]);
        Set(valid.Schedules.First(), nameof(TourSchedule.ReservedCapacity), 20);
        Set(closed.Schedules.First(), nameof(TourSchedule.RegistrationDeadlineAtUtc), now);
        Set(cancelled.Schedules.First(), nameof(TourSchedule.Status), TourScheduleStatus.Cancelled);
        db.Tours.AddRange(valid, invalid, closed, past, cancelled);
        await db.SaveChangesAsync();

        var ids = await PublicTourBookability.ReadScheduleIdsAsync(db.Tours, now, CancellationToken.None);

        ids.Should().Equal(valid.Schedules.Last().Id);
    }

    private static void Set(object target, string property, object value) =>
        target.GetType().GetProperty(property)!.SetValue(target, value);
}