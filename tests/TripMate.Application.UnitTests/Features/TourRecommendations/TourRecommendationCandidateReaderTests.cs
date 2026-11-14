using FluentAssertions;

using TripMate.Application.Features.TourRecommendations.Refresh;

namespace TripMate.Application.UnitTests.Features.TourRecommendations;

public sealed class TourRecommendationCandidateReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SelectRepresentativeSchedule_PrefersEarliestMatchingBookableSchedule()
    {
        var schedules = new[]
        {
            NewSchedule(1, new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero)),
            NewSchedule(3, new DateTimeOffset(2026, 12, 3, 1, 0, 0, TimeSpan.Zero)),
            NewSchedule(2, new DateTimeOffset(2026, 12, 3, 1, 0, 0, TimeSpan.Zero)),
        };

        var result = TourRecommendationCandidateReader.SelectRepresentativeSchedule(
            schedules,
            new DateOnly(2026, 12, 1),
            new DateOnly(2026, 12, 5));

        result!.Id.Should().Be(2);
        result.MatchesRequestedDate.Should().BeTrue();
    }

    [Fact]
    public void SelectRepresentativeSchedule_DateMismatchFallsBackWithoutFilteringTour()
    {
        var result = TourRecommendationCandidateReader.SelectRepresentativeSchedule(
            [NewSchedule(1, new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero))],
            new DateOnly(2026, 12, 1),
            new DateOnly(2026, 12, 5));

        result!.Id.Should().Be(1);
        result.MatchesRequestedDate.Should().BeFalse();
    }

    [Fact]
    public void IsBookable_RejectsPastClosedInvalidAndSoldOutSchedules()
    {
        TourRecommendationCandidateReader.IsBookable(NewSchedule(1, Now.AddDays(-1)), Now)
            .Should().BeFalse();
        TourRecommendationCandidateReader.IsBookable(NewSchedule(2, Now.AddDays(1), reserved: 10), Now)
            .Should().BeFalse();
        TourRecommendationCandidateReader.IsBookable(NewSchedule(3, Now.AddDays(1), total: 0), Now)
            .Should().BeFalse();
        TourRecommendationCandidateReader.IsBookable(NewSchedule(4, Now.AddDays(1), deadline: Now), Now)
            .Should().BeFalse();
    }

    private static TourRecommendationCandidateSchedule NewSchedule(
        long id,
        DateTimeOffset start,
        int total = 10,
        int reserved = 0,
        DateTimeOffset? deadline = null) =>
        new(id, start, start.AddDays(1), total, reserved, deadline);
}