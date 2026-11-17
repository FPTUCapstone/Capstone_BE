using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Common;

/// <summary>
/// ADR-002 G5: WeightedUtility visit reasons, in Vietnamese, taken from the parts of J that chose the visit: the traveler's
/// own choice, an interest match (base score), the time-of-day factor and a short detour (travel cost). Web and Mobile show
/// the text as-is; the itinerary explanation picks its preference and nearby templates from "sở thích" and "gần".
/// </summary>
internal static class PersonalizedVisitReasons
{
    internal const string SelectedByTraveler = "Điểm bạn đã chọn";
    internal const string MatchesInterests = "Hợp sở thích của bạn";
    internal const string GoodTimeOfDay = "Đi vào khung giờ đẹp";
    internal const string NearRoute = "Gần lộ trình";
    internal const string FitsTrip = "Vừa thời gian chuyến đi";

    /// <summary>A time-of-day factor at least this high raised the visit's gain (cool morning, sunrise, …).</summary>
    internal const double GoodTimeFactor = 1.05;

    /// <summary>A leg this short costs little travel time, so it is named as a reason.</summary>
    internal const int NearRouteMinutes = 15;

    private const string Separator = " · ";

    public static string Describe(
        bool isMandatory,
        bool matchesInterests,
        string? categoryName,
        double timeFitFactor,
        int travelMinutes)
    {
        var parts = new List<string>(3);
        if (isMandatory)
        {
            parts.Add(SelectedByTraveler);
        }
        else if (matchesInterests)
        {
            parts.Add(string.IsNullOrWhiteSpace(categoryName) ? MatchesInterests : $"{MatchesInterests} ({categoryName.Trim()})");
        }

        if (timeFitFactor >= GoodTimeFactor)
        {
            parts.Add(GoodTimeOfDay);
        }

        if (!isMandatory && travelMinutes <= NearRouteMinutes)
        {
            parts.Add(NearRoute);
        }

        return parts.Count == 0 ? FitsTrip : string.Join(Separator, parts);
    }

    /// <summary>
    /// Adds the time-of-day step's slot reason. It is more precise than <see cref="GoodTimeOfDay"/>, so that part and the
    /// <see cref="FitsTrip"/> fallback are replaced; the own-choice, interest and detour parts stay in front. A reason that
    /// has none of them (for example a Lexicographic reason code) is replaced as before.
    /// </summary>
    public static string WithTimeOfDay(string? reason, string slotReason)
    {
        var kept = (reason ?? string.Empty)
            .Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part == SelectedByTraveler || part == NearRoute || part.StartsWith(MatchesInterests, StringComparison.Ordinal))
            .ToList();
        kept.Add(slotReason);
        return string.Join(Separator, kept);
    }

    /// <summary>Rewrites the visit reasons of a validated WeightedUtility plan; rest items keep theirs.</summary>
    public static GeneratedItineraryPlan Apply(
        GeneratedItineraryPlan plan,
        RoutingContext ctx,
        WeightedObjectiveInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(inputs);

        var nodeOf = new Dictionary<long, int>(ctx.Nodes.Count);
        for (var node = 0; node < ctx.Nodes.Count; node++)
        {
            nodeOf[ctx.Nodes[node].Candidate.Id] = node;
        }

        var items = plan.Items.OrderBy(item => item.SequenceNo).ToArray();
        var previousMatrix = ctx.StartMatrixIndex;
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (item.PointOfInterestId is not { } poiId
                || !ctx.CandidateMatrixIndices.TryGetValue(poiId, out var matrix))
            {
                continue;
            }

            var travelMinutes = ctx.RawTravel(previousMatrix, matrix);
            previousMatrix = matrix;
            if (item.Kind != ItineraryItemKind.Visit || !nodeOf.TryGetValue(poiId, out var visitNode))
            {
                continue;
            }

            var candidate = ctx.Nodes[visitNode].Candidate;
            var factor = inputs.TimeFit.Factor(
                visitNode,
                ObjectiveCalculator.ToModelMinute(item.PlannedArrivalUtc, ctx.Input.StartAtUtc),
                ObjectiveCalculator.ToModelMinute(item.PlannedDepartureUtc, ctx.Input.StartAtUtc));
            items[index] = item with
            {
                RecommendationReason = Describe(
                    item.IsMandatory,
                    candidate.PreferenceScore > 0,
                    candidate.CategoryName,
                    factor,
                    travelMinutes),
            };
        }

        return plan with { Items = items };
    }
}