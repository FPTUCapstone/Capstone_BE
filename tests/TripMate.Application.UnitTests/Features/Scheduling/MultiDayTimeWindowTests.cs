using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling;

/// <summary>
/// A trip is one continuous window that may cross midnight: a stop that no longer fits today waits for the next
/// day it fits, and only the overall time budget decides whether that is acceptable.
/// </summary>
public sealed class MultiDayTimeWindowTests
{
    // Fixture start: Tuesday 2026-10-20 08:00 in Asia/Ho_Chi_Minh (01:00 UTC).
    private static readonly TimeZoneInfo Zone = OptionalRouteOptimizationScenarios.LocalTimeZone;

    private static readonly GenerationOpeningHours[] DailyEightToFive =
        Enumerable.Range(0, 7)
            .Select(day => new GenerationOpeningHours((byte)day, new TimeOnly(8, 0), new TimeOnly(17, 0)))
            .ToArray();

    [Fact]
    public void AlignToOpeningHours_WhenArrivingAfterClosing_WaitsForNextDayOpening()
    {
        var candidate = Candidate(1, visitMinutes: 60);

        var aligned = ItineraryScheduleEvaluator.AlignToOpeningHours(Local(2026, 10, 20, 18, 0), candidate, Zone);

        aligned.Should().Be(Local(2026, 10, 21, 8, 0));
    }

    [Fact]
    public void AlignToOpeningHours_WhenVisitNoLongerFitsBeforeClosing_WaitsForNextDayOpening()
    {
        var candidate = Candidate(1, visitMinutes: 60);

        var aligned = ItineraryScheduleEvaluator.AlignToOpeningHours(Local(2026, 10, 20, 16, 30), candidate, Zone);

        aligned.Should().Be(Local(2026, 10, 21, 8, 0));
    }

    [Fact]
    public void AlignToOpeningHours_WhenOpenAndVisitFits_KeepsArrival()
    {
        var candidate = Candidate(1, visitMinutes: 60);

        var aligned = ItineraryScheduleEvaluator.AlignToOpeningHours(Local(2026, 10, 20, 15, 0), candidate, Zone);

        aligned.Should().Be(Local(2026, 10, 20, 15, 0));
    }

    [Fact]
    public void AlignToOpeningHours_WhenClosedToday_UsesNextOpenDay()
    {
        // Open on Thursdays only (DayOfWeek 4).
        var candidate = Candidate(1, visitMinutes: 60) with
        {
            OpeningHours = [new GenerationOpeningHours(4, new TimeOnly(9, 0), new TimeOnly(11, 0))],
        };

        var aligned = ItineraryScheduleEvaluator.AlignToOpeningHours(Local(2026, 10, 20, 10, 0), candidate, Zone);

        aligned.Should().Be(Local(2026, 10, 22, 9, 0));
    }

    [Fact]
    public void AlignToOpeningHours_WhenVisitNeverFitsTheOpeningHours_IsInfeasible()
    {
        var candidate = Candidate(1, visitMinutes: 600);

        var aligned = ItineraryScheduleEvaluator.AlignToOpeningHours(Local(2026, 10, 20, 8, 0), candidate, Zone);

        aligned.Should().Be(DateTimeOffset.MinValue);
    }

    [Fact]
    public void Evaluate_WhenSecondStopOnlyFitsTomorrow_SchedulesItTheNextMorning()
    {
        var (input, matrix) = TwoLongStops(availableMinutes: 48 * 60);
        var candidates = input.Candidates.ToArray();

        var evaluated = new ItineraryScheduleEvaluator().Evaluate(
            input,
            candidates,
            matrix,
            candidates,
            Indices(candidates));

        evaluated.Should().NotBeNull();
        var visits = evaluated!.Plan.Items.Where(item => item.Kind == ItineraryItemKind.Visit).ToArray();
        visits.Should().HaveCount(2);
        visits[0].PlannedArrivalUtc.Should().Be(Local(2026, 10, 20, 8, 20)); // 10 min travel + 10 min transition buffer
        visits[1].PlannedArrivalUtc.Should().Be(Local(2026, 10, 21, 8, 0));
        visits[1].PlannedDepartureUtc.Should().Be(Local(2026, 10, 21, 13, 0));
    }

    [Fact]
    public void Evaluate_WhenNextDayFallsOutsideTheTimeBudget_ReturnsNull()
    {
        var (input, matrix) = TwoLongStops(availableMinutes: 12 * 60);
        var candidates = input.Candidates.ToArray();

        var evaluated = new ItineraryScheduleEvaluator().Evaluate(
            input,
            candidates,
            matrix,
            candidates,
            Indices(candidates));

        evaluated.Should().BeNull();
    }

    [Fact]
    public void RoutingStartAt_AgreesWithEvaluatorAcrossMidnight()
    {
        var (input, matrix) = TwoLongStops(availableMinutes: 48 * 60);
        var candidates = input.Candidates.ToArray();
        var context = RoutingContext.Create(
            input,
            matrix,
            candidates,
            Indices(candidates),
            new SchedulingGenerationOptions(),
            new MiniRoutingOptions(),
            reserveRestTime: false);
        var secondNode = Enumerable.Range(0, context.Nodes.Count)
            .Single(index => context.Nodes[index].Candidate.Id == 2);

        // Arriving at 13:30 on day one: five hours no longer fit before 17:00, so the visit starts at 08:00 tomorrow.
        var arrival = (int)(Local(2026, 10, 20, 13, 30) - input.StartAtUtc).TotalMinutes;
        var start = context.StartAt(secondNode, arrival);

        start.Should().Be((int)(Local(2026, 10, 21, 8, 0) - input.StartAtUtc).TotalMinutes);
    }

    private static (GenerationInput Input, RouteDurationMatrix Matrix) TwoLongStops(int availableMinutes)
    {
        var candidates = new[] { Candidate(1, visitMinutes: 300), Candidate(2, visitMinutes: 300) };
        var input = OptionalRouteOptimizationScenarios.CreateInput(
            availableMinutes,
            candidates,
            mandatoryPoiIds: [1, 2],
            budgetVnd: null);

        // Points: 0:Start, 1:Stop1, 2:Stop2, 3:End.
        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0, 10, 15, 5 },
            { 10, 0, 10, 10 },
            { 15, 10, 0, 10 },
            { 5, 10, 10, 0 },
        });

        return (input, matrix);
    }

    private static GenerationCandidate Candidate(long id, int visitMinutes) =>
        OptionalRouteOptimizationScenarios.CreateCandidate(
            id,
            $"Stop {id}",
            visitMinutes,
            cost: 0m,
            effectiveDesirabilityScore: 0.5m,
            openingHours: DailyEightToFive);

    private static Dictionary<long, int> Indices(IReadOnlyList<GenerationCandidate> candidates) =>
        candidates.Select((candidate, index) => (candidate.Id, Index: index + 1))
            .ToDictionary(pair => pair.Id, pair => pair.Index);

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(7));
}