using System.Runtime.CompilerServices;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

/// <summary>
/// Bộ so sánh các lịch trình đã qua đánh giá (EvaluatedItinerarySchedule) theo hàm mục tiêu cá nhân hóa.
/// Sử dụng ConditionalWeakTable để lưu điểm mục tiêu J đã tính, tránh tính lại nhiều lần
/// trong các thuật toán sắp xếp mà không gây rò rỉ bộ nhớ hay cấp phát lặp lại.
/// </summary>
internal sealed class WeightedScheduleComparer(
    WeightedObjectiveInputs inputs,
    RoutingContext ctx,
    IReadOnlyDictionary<long, int> nodeOf,
    IReadOnlyDictionary<long, int> matrixIndexOf,
    int[] catScratch,
    List<GeneratedItineraryItem> itemScratch) : IComparer<EvaluatedItinerarySchedule>
{
    private readonly ConditionalWeakTable<EvaluatedItinerarySchedule, StrongBox<long>> _cache = new();

    public int Compare(EvaluatedItinerarySchedule? x, EvaluatedItinerarySchedule? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return 1;
        }

        if (y is null)
        {
            return -1;
        }

        var jX = GetOrComputeJ(x);
        var jY = GetOrComputeJ(y);

        // Cấp 1: Điểm hàm mục tiêu cá nhân hóa (Utility) cao hơn xếp trước (trả về số âm)
        var cmp = jY.CompareTo(jX);
        if (cmp != 0)
        {
            return cmp;
        }

        // Cấp 2: Tổng phút di chuyển ít hơn xếp trước
        cmp = x.TotalMatrixTravelMinutes.CompareTo(y.TotalMatrixTravelMinutes);
        if (cmp != 0)
        {
            return cmp;
        }

        // Cấp 3: Thời điểm kết thúc sớm hơn xếp trước
        cmp = x.Plan.EndAtUtc.CompareTo(y.Plan.EndAtUtc);
        if (cmp != 0)
        {
            return cmp;
        }

        // Cấp 4: So sánh dãy Id POI để đảm bảo thứ tự toàn phần tất định
        var minCount = Math.Min(x.VisitPoiIds.Count, y.VisitPoiIds.Count);
        for (var i = 0; i < minCount; i++)
        {
            var idCmp = x.VisitPoiIds[i].CompareTo(y.VisitPoiIds[i]);
            if (idCmp != 0)
            {
                return idCmp;
            }
        }

        return x.VisitPoiIds.Count.CompareTo(y.VisitPoiIds.Count);
    }

    private long GetOrComputeJ(EvaluatedItinerarySchedule s)
    {
        if (_cache.TryGetValue(s, out var boxed))
        {
            return boxed.Value;
        }

        var j = ObjectiveCalculator.Evaluate(inputs, ctx, s, nodeOf, matrixIndexOf, catScratch, itemScratch);
        _cache.Add(s, new StrongBox<long>(j));
        return j;
    }
}
