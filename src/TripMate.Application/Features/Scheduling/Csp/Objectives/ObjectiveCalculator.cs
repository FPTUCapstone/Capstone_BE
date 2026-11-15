using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

public static class ObjectiveCalculator
{
    public static int ToModelMinute(DateTimeOffset t, DateTimeOffset startAtUtc) =>
        (int)Math.Ceiling((t - startAtUtc).TotalMinutes - 1e-9);

    /// <summary>
    /// Scaled delta-J when visiting a node during [start, end], arriving via travelRaw leg duration and waiting for wait minutes.
    /// groupCountBefore: number of visits in the same diversity group prior to this visit. Updates soft state in place.
    /// </summary>
    public static long VisitTerm(
        WeightedObjectiveInputs x,
        int node,
        int start,
        int end,
        int travelRaw,
        int wait,
        int groupCountBefore,
        ref SoftState soft)
    {
        var p = x.P;
        long[] gammaRow = x.GammaScaledByNode[node];
        double gamma = gammaRow[groupCountBefore] / (double)WeightedObjectiveInputs.Scale;
        double gain = x.Base[node] * x.TimeFit.Factor(node, start, end) * gamma;

        double cost = p.LambdaPerMin * travelRaw + p.Mu * wait;

        soft.ConsecutiveOutdoor = x.IsExposedOutdoor[node] ? soft.ConsecutiveOutdoor + 1 : 0;
        if (soft.ConsecutiveOutdoor > p.MaxConsecutiveOutdoor)
        {
            cost += p.RhoOutdoor;
        }

        soft.ActiveSinceRestMin = x.IsRestStop[node] ? 0 : soft.ActiveSinceRestMin + travelRaw + (end - start);
        if (soft.ActiveSinceRestMin > p.RestAfterMin)
        {
            cost += p.RhoRest;
        }

        if (!x.IsFood[node] && !x.IsRestStop[node])
        {
            soft.Stops++;
            if (soft.Stops > p.MaxStopsPerDay)
            {
                cost += p.RhoStops;
            }
        }

        int beforeEffort = soft.Effort;
        soft.Effort += x.Effort[node];
        int excess = Math.Max(0, soft.Effort - Math.Max(beforeEffort, p.MaxDailyEffort));
        if (excess > 0)
        {
            cost += p.RhoEffortPerHour * (excess / 60.0);
        }

        return (long)Math.Round(WeightedObjectiveInputs.Scale * (gain - cost), MidpointRounding.ToEven);
    }

    /// <summary>Penalty for returning to end location.</summary>
    public static long ReturnTerm(WeightedObjectiveInputs x, int returnRaw)
        => -(long)Math.Round(WeightedObjectiveInputs.Scale * x.P.LambdaPerMin * returnRaw, MidpointRounding.ToEven);

    /// <summary>Replays a route on the model (used for KeyOf and consistency tests).</summary>
    public static long Replay(
        WeightedObjectiveInputs x,
        RoutingContext ctx,
        IReadOnlyList<int> seq,
        IReadOnlyList<int> starts,
        Span<int> groupScratch)
    {
        groupScratch.Clear();
        var soft = default(SoftState);
        long j = 0;
        int prevMatrix = ctx.StartMatrixIndex;
        int prevDeparture = 0;

        for (var i = 0; i < seq.Count; i++)
        {
            var v = seq[i];
            var n = ctx.Nodes[v];
            int arrive = prevDeparture + ctx.Travel(prevMatrix, n.MatrixIndex);
            int start = starts[i];
            int end = start + n.VisitMinutes;
            int group = x.DiversityGroupIndex[v];
            int travelRaw = ctx.RawTravel(prevMatrix, n.MatrixIndex);
            int wait = start - arrive;
            j += VisitTerm(x, v, start, end, travelRaw, wait, groupScratch[group], ref soft);
            groupScratch[group]++;
            prevMatrix = n.MatrixIndex;
            prevDeparture = end;
        }

        return j + ReturnTerm(x, ctx.RawTravel(prevMatrix, ctx.EndMatrixIndex));
    }

    /// <summary>
    /// Evaluates the true J objective on a schedule evaluated by the Evaluator, directly using RoutingContext and CandidateMatrixIndices.
    /// Automatically builds nodeOf and scratch buffers if the caller does not reuse them.
    /// </summary>
    public static long Evaluate(
        WeightedObjectiveInputs x,
        RoutingContext ctx,
        EvaluatedItinerarySchedule s)
    {
        var nodeOf = new Dictionary<long, int>(ctx.Nodes.Count);
        for (var i = 0; i < ctx.Nodes.Count; i++)
        {
            nodeOf[ctx.Nodes[i].Candidate.Id] = i;
        }

        return Evaluate(
            x,
            ctx,
            s,
            nodeOf,
            ctx.CandidateMatrixIndices,
            new int[x.DiversityGroupCount],
            new List<GeneratedItineraryItem>(s.Plan.Items.Count));
    }

    /// <summary>
    /// Computes the true J objective on an evaluated schedule (with rest stops inserted and actual start/end timestamps).
    /// </summary>
    public static long Evaluate(
        WeightedObjectiveInputs x,
        RoutingContext ctx,
        EvaluatedItinerarySchedule s,
        IReadOnlyDictionary<long, int> nodeOf,
        IReadOnlyDictionary<long, int> matrixIndexOf,
        Span<int> groupScratch,
        List<GeneratedItineraryItem> itemScratch)
    {
        groupScratch.Clear();
        itemScratch.Clear();
        itemScratch.AddRange(s.Plan.Items);
        itemScratch.Sort(static (a, b) => a.SequenceNo.CompareTo(b.SequenceNo));

        var soft = default(SoftState);
        long j = 0;
        int prevMatrix = ctx.StartMatrixIndex;
        int prevEnd = 0;
        int legSum = 0;

        for (var i = 0; i < itemScratch.Count; i++)
        {
            var item = itemScratch[i];

            int curMatrix = item.PointOfInterestId is { } poiId
                ? (matrixIndexOf.TryGetValue(poiId, out var m)
                    ? m
                    : throw new InvalidOperationException($"POI {poiId} is not in the routing matrix."))
                : prevMatrix;

            int travelRaw = ctx.RawTravel(prevMatrix, curMatrix);
            Invariant.Check(
                i == 0 || itemScratch[i - 1].TravelDurationToNextMinutes is not { } rec || rec == travelRaw,
                "Evaluator recorded leg duration different from raw travel matrix");
            legSum += travelRaw;

            int start = ToModelMinute(item.PlannedArrivalUtc, ctx.Input.StartAtUtc);
            int end = ToModelMinute(item.PlannedDepartureUtc, ctx.Input.StartAtUtc);

            if (item.Kind == ItineraryItemKind.Rest)
            {
                soft.ActiveSinceRestMin = 0;
                j -= (long)Math.Round(WeightedObjectiveInputs.Scale * x.P.LambdaPerMin * travelRaw, MidpointRounding.ToEven);
                prevEnd = end;
            }
            else
            {
                if (item.PointOfInterestId is not { } visitId || !nodeOf.TryGetValue(visitId, out var node))
                {
                    throw new InvalidOperationException($"Visit {item.PointOfInterestId} is not in candidate nodes.");
                }

                Invariant.Check(end - start == ctx.Nodes[node].VisitMinutes, "Evaluator changed visit duration");

                int arrivedAt = prevEnd + travelRaw + ctx.TransitionBuffer;
                int wait = start - arrivedAt;
                Invariant.Check(wait >= 0, "Actual schedule started before arrival");

                int group = x.DiversityGroupIndex[node];
                j += VisitTerm(x, node, start, end, travelRaw, wait, groupScratch[group], ref soft);
                groupScratch[group]++;
                prevEnd = end;
            }

            prevMatrix = curMatrix;
        }

        int returnRaw = ctx.RawTravel(prevMatrix, ctx.EndMatrixIndex);
        Invariant.Check(
            legSum + returnRaw == s.TotalMatrixTravelMinutes,
            "Total leg travel duration mismatch with TotalMatrixTravelMinutes");

        return j + ReturnTerm(x, returnRaw);
    }
}