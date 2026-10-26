using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.TimeOfDay;

public sealed class TimeOfDayOptions
{
    public const string SectionName = "TimeOfDay";

    public bool Enabled { get; init; } = true;

    /// <summary>Absolute cap on minutes added by refinement; zero disables all extensions.</summary>
    public int MaxExtraMinutes { get; init; } = 60;

    /// <summary>Minutes considered for ordinary trips, subject to MaxExtraMinutes.</summary>
    public int NormalExtraMinutes { get; init; } = 15;

    /// <summary>Minutes considered for an early sunrise stop, subject to MaxExtraMinutes.</summary>
    public int SunriseExtraMinutes { get; init; } = 60;

    /// <summary>Latest local start minute, counted from midnight, that qualifies as an early trip.</summary>
    public int EarlyTripCutoffMinutes { get; init; } = 7 * 60;
}

/// <summary>
/// Runs after the solver (option A, decided 2026-10-16). Plans with a model objective keep the solver's order and
/// receive recommendation reasons only. Other plans try orders of the same visits and keep one only when it scores
/// better on <see cref="TimeOfDayScorer"/>, stays valid (same fixed-order scheduler), and makes the trip at most
/// <see cref="TimeOfDayOptions.MaxExtraMinutes"/> longer. The route provider should memoize: every order reuses one
/// matrix.
/// </summary>
public sealed class TimeOfDayRefiner(
    IRouteDurationProvider routeDurationProvider,
    SchedulingGenerationOptions? generationOptions,
    TimeOfDayOptions options)
{
    /// <summary>All orders are tried up to this many visits; longer plans use pairwise swaps.</summary>
    private const int ExhaustiveVisitLimit = 6;

    public async Task<GeneratedItineraryPlan> RefineAsync(
        GenerationInput input,
        GeneratedItineraryPlan plan,
        IReadOnlyDictionary<long, TimeOfDayTraits> traits,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return plan;
        }

        if (plan.ModelObjective.HasValue)
        {
            return WithReasons(plan, input.TimeZone, traits);
        }

        var visits = plan.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit && item.PointOfInterestId.HasValue)
            .OrderBy(item => item.SequenceNo)
            .Select(item => item.PointOfInterestId!.Value)
            .ToArray();
        var best = plan;
        var bestScore = ScorePlan(plan, input.TimeZone, traits);
        if (visits.Length >= 2)
        {
            var localStart = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(input.StartAtUtc, input.TimeZone).DateTime);
            var isEarlyTrip = localStart <= TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(options.EarlyTripCutoffMinutes));
            var hasEarlySunriseSpot = isEarlyTrip && visits.Any(id =>
                traits.TryGetValue(id, out var t) && TimeOfDayScorer.IsSunriseSpot(t));

            var effectiveMaxExtraMinutes = hasEarlySunriseSpot
                ? Math.Min(options.MaxExtraMinutes, options.SunriseExtraMinutes)
                : Math.Min(options.MaxExtraMinutes, options.NormalExtraMinutes);

            var latestEnd = plan.EndAtUtc.AddMinutes(effectiveMaxExtraMinutes);
            var service = new ItineraryGenerationService(routeDurationProvider, generationOptions);
            foreach (var order in CandidateOrders(visits, traits))
            {
                var candidate = await service.GenerateFixedOrderAsync(input, order, cancellationToken);
                if (candidate.IsFailure || candidate.Value.EndAtUtc > latestEnd)
                {
                    continue;
                }

                var score = ScorePlan(candidate.Value, input.TimeZone, traits);
                if (score > bestScore || (score == bestScore && candidate.Value.EndAtUtc < best.EndAtUtc && !ReferenceEquals(best, plan)))
                {
                    best = candidate.Value;
                    bestScore = score;
                }
            }
        }

        return WithReasons(best, input.TimeZone, traits);
    }

    public static int ScorePlan(
        GeneratedItineraryPlan plan,
        TimeZoneInfo timeZone,
        IReadOnlyDictionary<long, TimeOfDayTraits> traits) =>
        plan.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit && item.PointOfInterestId.HasValue)
            .Sum(item => traits.TryGetValue(item.PointOfInterestId!.Value, out var placeTraits)
                ? TimeOfDayScorer.Score(Middle(item, timeZone), placeTraits)
                : 0);

    private static GeneratedItineraryPlan WithReasons(
        GeneratedItineraryPlan plan,
        TimeZoneInfo timeZone,
        IReadOnlyDictionary<long, TimeOfDayTraits> traits) =>
        plan with
        {
            Items = plan.Items
                .Select(item => item.Kind == ItineraryItemKind.Visit
                    && item.PointOfInterestId is { } poiId
                    && traits.TryGetValue(poiId, out var placeTraits)
                    && TimeOfDayScorer.Reason(Middle(item, timeZone), placeTraits) is { } reason
                        ? item with { RecommendationReason = reason }
                        : item)
                .ToArray(),
        };

    private static TimeOnly Middle(GeneratedItineraryItem item, TimeZoneInfo timeZone)
    {
        var middle = item.PlannedArrivalUtc + ((item.PlannedDepartureUtc - item.PlannedArrivalUtc) / 2);
        return TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(middle, timeZone).DateTime);
    }

    private static IEnumerable<long[]> CandidateOrders(
        long[] visits,
        IReadOnlyDictionary<long, TimeOfDayTraits> traits)
    {
        if (visits.Length <= ExhaustiveVisitLimit)
        {
            return Permutations(visits).Where(order => !order.SequenceEqual(visits));
        }

        var orders = new List<long[]>();
        var seen = new HashSet<string>();

        void TryAdd(long[] order)
        {
            if (order.SequenceEqual(visits))
            {
                return;
            }

            var key = string.Join(",", order);
            if (seen.Add(key))
            {
                orders.Add(order);
            }
        }

        // 1. Prioritize placing any sunrise spot at the first visit position (slot 0)
        for (var i = 1; i < visits.Length; i++)
        {
            if (traits.TryGetValue(visits[i], out var t) && TimeOfDayScorer.IsSunriseSpot(t))
            {
                var order = new long[visits.Length];
                order[0] = visits[i];
                var targetIdx = 1;
                for (var j = 0; j < visits.Length; j++)
                {
                    if (j != i)
                    {
                        order[targetIdx++] = visits[j];
                    }
                }

                TryAdd(order);
            }
        }

        // 2. Pairwise swaps for all pairs
        for (var first = 0; first < visits.Length - 1; first++)
        {
            for (var second = first + 1; second < visits.Length; second++)
            {
                var order = (long[])visits.Clone();
                (order[first], order[second]) = (order[second], order[first]);
                TryAdd(order);
            }
        }

        return orders;
    }

    private static IEnumerable<long[]> Permutations(long[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var index = 0; index < items.Length; index++)
        {
            var rest = items.Where((_, position) => position != index).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return [items[index], .. tail];
            }
        }
    }
}