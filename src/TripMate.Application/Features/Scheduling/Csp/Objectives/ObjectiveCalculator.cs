using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

public static class ObjectiveCalculator
{
    public static int ToModelMinute(DateTimeOffset t, DateTimeOffset startAtUtc) =>
        (int)Math.Ceiling((t - startAtUtc).TotalMinutes - 1e-9);

    /// <summary>
    /// ΔJ (scaled) khi thêm lượt ghé node tại [start, end], đến từ chặng dài travelRaw, chờ wait phút.
    /// categoryCountBefore: số lượt cùng danh mục đã có TRƯỚC lượt này. Cập nhật soft state tại chỗ.
    /// </summary>
    public static long VisitTerm(
        WeightedObjectiveInputs x,
        int node,
        int start,
        int end,
        int travelRaw,
        int wait,
        int categoryCountBefore,
        ref SoftState soft)
    {
        var p = x.P;
        int cat = x.CategoryIndex[node];
        double gamma = x.GammaScaled[cat][categoryCountBefore] / (double)WeightedObjectiveInputs.Scale;
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

    /// <summary>Phạt chặng về đích.</summary>
    public static long ReturnTerm(WeightedObjectiveInputs x, int returnRaw)
        => -(long)Math.Round(WeightedObjectiveInputs.Scale * x.P.LambdaPerMin * returnRaw, MidpointRounding.ToEven);

    /// <summary>Phát lại một lộ trình trên mô hình (dùng cho KeyOf và test nhất quán).</summary>
    public static long Replay(
        WeightedObjectiveInputs x,
        RoutingContext ctx,
        IReadOnlyList<int> seq,
        IReadOnlyList<int> starts,
        Span<int> catScratch)
    {
        catScratch.Clear();
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
            int cat = x.CategoryIndex[v];
            int travelRaw = ctx.RawTravel(prevMatrix, n.MatrixIndex);
            int wait = start - arrive;
            j += VisitTerm(x, v, start, end, travelRaw, wait, catScratch[cat], ref soft);
            catScratch[cat]++;
            prevMatrix = n.MatrixIndex;
            prevDeparture = end;
        }

        return j + ReturnTerm(x, ctx.RawTravel(prevMatrix, ctx.EndMatrixIndex));
    }

    /// <summary>
    /// Tính J thật trên lịch đã qua Evaluator (đã chèn rest stop, giờ bắt đầu/kết thúc là thời gian thực tế).
    /// </summary>
    public static long Evaluate(
        WeightedObjectiveInputs x,
        RoutingContext ctx,
        EvaluatedItinerarySchedule s,
        IReadOnlyDictionary<long, int> nodeOf,
        IReadOnlyDictionary<long, int> matrixIndexOf,
        Span<int> catScratch,
        List<GeneratedItineraryItem> itemScratch)
    {
        catScratch.Clear();
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
                    : throw new InvalidOperationException($"POI {poiId} không có trong ma trận."))
                : prevMatrix;

            int travelRaw = ctx.RawTravel(prevMatrix, curMatrix);
            Invariant.Check(
                i == 0 || itemScratch[i - 1].TravelDurationToNextMinutes is not { } rec || rec == travelRaw,
                "Evaluator ghi chặng khác ma trận thô");
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
                    throw new InvalidOperationException($"Visit {item.PointOfInterestId} không thuộc tập ứng viên.");
                }

                Invariant.Check(end - start == ctx.Nodes[node].VisitMinutes, "Evaluator đổi thời lượng tham quan?");

                int arrivedAt = prevEnd + travelRaw + ctx.TransitionBuffer;
                int wait = start - arrivedAt;
                Invariant.Check(wait >= 0, "Lịch thật bắt đầu trước khi đến nơi?");

                int cat = x.CategoryIndex[node];
                j += VisitTerm(x, node, start, end, travelRaw, wait, catScratch[cat], ref soft);
                catScratch[cat]++;
                prevEnd = end;
            }

            prevMatrix = curMatrix;
        }

        int returnRaw = ctx.RawTravel(prevMatrix, ctx.EndMatrixIndex);
        Invariant.Check(
            legSum + returnRaw == s.TotalMatrixTravelMinutes,
            "Tổng chặng lệch TotalMatrixTravelMinutes");

        return j + ReturnTerm(x, returnRaw);
    }
}
