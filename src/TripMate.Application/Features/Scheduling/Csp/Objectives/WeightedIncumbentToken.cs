namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

/// <summary>
/// Đại diện incumbent dạng token cho WeightedUtilityKernel: chỉ lưu trữ dãy POI IDs của lộ trình.
/// Không mang ScaledUtility: giá trị J chỉ có nghĩa trong RoutingContext đã tính ra nó và luôn
/// được phát lại (Replay) khi nhập vào context mới.
/// </summary>
internal sealed class WeightedIncumbentToken(long[] visitPoiIds) : IncumbentToken
{
    public long[] VisitPoiIds { get; } = visitPoiIds;
}