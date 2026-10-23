using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>
/// Explains why an ordered itinerary does not fit by re-running the same fixed-order scheduler on growing
/// prefixes and relaxed copies of the input. Reusing the scheduler keeps the explanation consistent with the
/// result the traveler gets. The route provider should be memoizing: every run uses the same matrix.
/// </summary>
public sealed class FixedOrderDiagnosis(
    IRouteDurationProvider routeDurationProvider,
    SchedulingGenerationOptions? options = null)
{
    private const int RelaxedExtraMinutes = 24 * 60;

    public async Task<StopInfeasibility?> ExplainAsync(
        GenerationInput input,
        IReadOnlyList<long> orderedVisitPoiIds,
        CancellationToken cancellationToken)
    {
        // Mandatory stops are checked by the caller; prefixes must not fail because one is still missing.
        var unconstrained = input with { MandatoryPoiIds = [] };
        var candidates = unconstrained.Candidates.ToDictionary(candidate => candidate.Id);
        decimal totalCost = 0;
        for (var count = 1; count <= orderedVisitPoiIds.Count; count++)
        {
            var poiId = orderedVisitPoiIds[count - 1];
            totalCost += candidates[poiId].EstimatedVisitCost ?? 0;
            if (unconstrained.BudgetVnd is { } budget && totalCost > budget)
            {
                return new StopInfeasibility(poiId, StopInfeasibilityReasons.ExceedsBudget, OverflowVnd: totalCost - budget);
            }

            var prefix = orderedVisitPoiIds.Take(count).ToArray();
            if ((await GenerateAsync(unconstrained, prefix, cancellationToken)).IsSuccess)
            {
                continue;
            }

            var compressedPrefixInput = ElasticVisitCompression.ApplyCompression(unconstrained, prefix);
            if ((await GenerateAsync(compressedPrefixInput, prefix, cancellationToken)).IsSuccess)
            {
                continue;
            }

            return await ExplainStopAsync(compressedPrefixInput, prefix, candidates[poiId], cancellationToken);
        }

        return null;
    }

    private async Task<StopInfeasibility> ExplainStopAsync(
        GenerationInput input,
        IReadOnlyList<long> prefix,
        GenerationCandidate stop,
        CancellationToken cancellationToken)
    {
        var alwaysOpen = input with
        {
            Candidates = input.Candidates
                .Select(candidate => candidate.Id == stop.Id
                    ? candidate with { OpeningHours = AllDay() }
                    : candidate)
                .ToArray(),
        };
        var withoutHours = await GenerateAsync(alwaysOpen, prefix, cancellationToken);
        if (withoutHours.IsSuccess)
        {
            var arrival = PlannedArrival(withoutHours.Value, stop.Id);
            return new StopInfeasibility(
                stop.Id,
                StopInfeasibilityReasons.ClosedAtArrival,
                arrival,
                ClosingTime(stop, arrival, input.TimeZone));
        }

        var moreTime = alwaysOpen with { AvailableMinutes = input.AvailableMinutes + RelaxedExtraMinutes };
        var relaxed = await GenerateAsync(moreTime, prefix, cancellationToken);
        if (!relaxed.IsSuccess)
        {
            return new StopInfeasibility(stop.Id, StopInfeasibilityReasons.ExceedsEndTime);
        }

        var deadline = input.StartAtUtc.AddMinutes(input.AvailableMinutes);
        return new StopInfeasibility(
            stop.Id,
            StopInfeasibilityReasons.ExceedsEndTime,
            PlannedArrival(relaxed.Value, stop.Id),
            OverflowMinutes: Math.Max(1, (int)Math.Ceiling((relaxed.Value.EndAtUtc - deadline).TotalMinutes)));
    }

    private Task<TripMate.Application.Common.Models.Result<GeneratedItineraryPlan>> GenerateAsync(
        GenerationInput input,
        IReadOnlyList<long> orderedVisitPoiIds,
        CancellationToken cancellationToken) =>
        new ItineraryGenerationService(routeDurationProvider, options)
            .GenerateFixedOrderAsync(input, orderedVisitPoiIds, cancellationToken);

    private static DateTimeOffset? PlannedArrival(GeneratedItineraryPlan plan, long poiId) =>
        plan.Items.LastOrDefault(item => item.PointOfInterestId == poiId)?.PlannedArrivalUtc;

    private static TimeOnly? ClosingTime(GenerationCandidate stop, DateTimeOffset? arrivalUtc, TimeZoneInfo timeZone)
    {
        if (arrivalUtc is null)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(arrivalUtc.Value, timeZone);
        return stop.OpeningHours
            .Where(hours => hours.DayOfWeek == (byte)local.DayOfWeek)
            .Select(hours => (TimeOnly?)hours.CloseTime)
            .FirstOrDefault();
    }

    private static IReadOnlyCollection<GenerationOpeningHours> AllDay() =>
        Enumerable.Range(0, 7)
            .Select(day => new GenerationOpeningHours((byte)day, new TimeOnly(0, 0), new TimeOnly(23, 59)))
            .ToArray();
}