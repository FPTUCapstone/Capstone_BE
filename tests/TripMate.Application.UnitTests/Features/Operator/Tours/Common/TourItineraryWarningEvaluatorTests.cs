using FluentAssertions;

using TripMate.Application.Features.Operator.Tours.Common;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.Common;

public sealed class TourItineraryWarningEvaluatorTests
{
    // 2026-11-01 is a Sunday (DayOfWeek 0); day 2 is Monday (1).
    private static readonly DateOnly Sunday = new(2026, 11, 1);

    private static TourItineraryItemFact Item(int day, int sequence, long poi, string? time, int stay = 60) =>
        new(day, sequence, poi, time is null ? null : TimeOnly.Parse(time), stay);

    private static PoiOpeningHoursFact Open(long poi, byte day, string open, string close) =>
        new(poi, day, TimeOnly.Parse(open), TimeOnly.Parse(close), false);

    [Fact]
    public void Evaluate_FlagsEveryRepeatOfAPoiButNotTheFirstVisit()
    {
        var warnings = TourItineraryWarningEvaluator.Evaluate(
            [Item(1, 1, 5, null), Item(1, 2, 6, null), Item(2, 1, 5, null), Item(2, 2, 5, null)], null, []);

        warnings.Should().BeEquivalentTo([
            new OperatorTourWarningDto("duplicate_poi", 2, 1, 5),
            new OperatorTourWarningDto("duplicate_poi", 2, 2, 5),
        ]);
    }

    [Fact]
    public void Evaluate_WithinOpeningHours_HasNoWarning()
    {
        TourItineraryWarningEvaluator.Evaluate(
            [Item(1, 1, 5, "09:00", 120)], Sunday, [Open(5, 0, "08:00", "17:00")]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("07:30", 60)]   // starts before opening
    [InlineData("16:30", 60)]   // stay runs past closing
    public void Evaluate_OutsideOpeningHours_Warns(string time, int stay)
    {
        TourItineraryWarningEvaluator.Evaluate(
            [Item(1, 1, 5, time, stay)], Sunday, [Open(5, 0, "08:00", "17:00")])
            .Should().ContainSingle().Which.Code.Should().Be("outside_opening_hours");
    }

    [Fact]
    public void Evaluate_ClosedDay_Warns()
    {
        TourItineraryWarningEvaluator.Evaluate(
            [Item(1, 1, 5, "09:00")], Sunday, [new PoiOpeningHoursFact(5, 0, null, null, true)])
            .Should().ContainSingle().Which.Code.Should().Be("poi_closed_on_day");
    }

    [Fact]
    public void Evaluate_UsesTheDayOfTheItineraryDay_NotTheDepartureDay()
    {
        // Day 2 falls on Monday; the POI is only open on Sunday, so Monday has no data.
        TourItineraryWarningEvaluator.Evaluate(
            [Item(2, 1, 5, "09:00")], Sunday, [Open(5, 0, "08:00", "17:00")])
            .Should().ContainSingle().Which.Code.Should().Be("opening_hours_unknown");
    }

    [Fact]
    public void Evaluate_WithoutAPlannedTimeOrDeparture_DoesNotInventAnOpeningHoursJudgement()
    {
        TourItineraryWarningEvaluator.Evaluate([Item(1, 1, 5, null)], Sunday, []).Should().BeEmpty();
        TourItineraryWarningEvaluator.Evaluate([Item(1, 1, 5, "09:00")], null, []).Should().BeEmpty();
    }
}