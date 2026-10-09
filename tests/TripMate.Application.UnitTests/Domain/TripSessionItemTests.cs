using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Domain;

public sealed class TripSessionItemTests
{
    private static readonly DateTimeOffset PlannedArrivalUtc =
        new(2026, 10, 20, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Snapshot_StoresPlannedScheduleAndMandatoryFlagAsPending()
    {
        var item = TripSessionItem.Snapshot(
            1001,
            1,
            12,
            "  Marble Mountains  ",
            16.003300m,
            108.263500m,
            PlannedArrivalUtc,
            PlannedArrivalUtc.AddMinutes(90),
            isMandatory: true);

        item.Should().BeEquivalentTo(new
        {
            ItineraryItemId = 1001L,
            SequenceNo = 1,
            PoiId = 12L,
            PoiName = "Marble Mountains",
            Latitude = 16.003300m,
            Longitude = 108.263500m,
            PlannedArrivalUtc,
            PlannedDepartureUtc = PlannedArrivalUtc.AddMinutes(90),
            IsMandatory = true,
            ReachedAtUtc = (DateTimeOffset?)null,
            SkippedAtUtc = (DateTimeOffset?)null,
            Status = TripSessionItem.PendingStatus,
        });
    }

    [Fact]
    public void Snapshot_RejectsNonUtcOrInvertedPlannedSchedule()
    {
        Action nonUtcArrival = () => Create(
            PlannedArrivalUtc.ToOffset(TimeSpan.FromHours(7)),
            PlannedArrivalUtc.AddHours(1));
        Action nonUtcDeparture = () => Create(
            PlannedArrivalUtc,
            PlannedArrivalUtc.AddHours(1).ToOffset(TimeSpan.FromHours(7)));
        Action inverted = () => Create(PlannedArrivalUtc, PlannedArrivalUtc.AddMinutes(-1));

        nonUtcArrival.Should().Throw<ArgumentException>();
        nonUtcDeparture.Should().Throw<ArgumentException>();
        inverted.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void StatusConstants_MatchTheContractVocabulary()
    {
        TripSessionItem.PendingStatus.Should().Be("Pending");
        TripSessionItem.ReachedStatus.Should().Be("Reached");
        TripSessionItem.SkippedStatus.Should().Be("Skipped");
    }

    private static TripSessionItem Create(DateTimeOffset arrival, DateTimeOffset departure) =>
        TripSessionItem.Snapshot(1001, 1, 12, "Marble Mountains", 16m, 108m, arrival, departure, false);
}