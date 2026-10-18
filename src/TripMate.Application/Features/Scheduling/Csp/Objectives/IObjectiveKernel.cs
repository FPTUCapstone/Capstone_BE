using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>
/// Trừu tượng hóa hàm mục tiêu của CSP solver.
/// Đóng gói toàn bộ logic quản lý incumbent, tính toán cận (CanImprove),
/// cập nhật trạng thái nhánh (OnPush, OnPop), sắp xếp thứ tự (ValueScore),
/// siết cận (Tighten), và so sánh kết quả cuối cùng.
/// </summary>
internal interface IObjectiveKernel
{
    void Initialize(RoutingContext ctx, SearchBuffers buffers, SearchPrecomputed pre, CspStatistics statistics);

    // Incumbent management
    IncumbentToken? AcceptInitial(EvaluatedItinerarySchedule? schedule);
    void ImportIncumbent(IncumbentToken? token);
    IncumbentToken? ExportIncumbent();
    bool HasIncumbent { get; }

    // State lifecycle
    void OnPush(int depth, int value, int start, int fromMatrix);
    void OnPop(int depth, int value);

    // Bounds & Pruning
    bool CanImprove(int[] domain, int domainCount, int depth, int departure, int current);
    long ValueScore(int value, int start, int current, int slack);

    // Elite management
    IEliteOrdering EliteOrdering { get; }
    EliteKey PeekEliteKey(int depth, int departure, int current, int travel, int finish);
    EliteKey SnapshotEliteKey(int depth, int departure, int current, int travel, int finish);

    // Tightening & Incumbent comparison
    int CompareToIncumbent(int depth, int departure, int current, int travel);
    void AcceptIncumbent(int depth, int departure, int current, int travel);
    int CompareEvaluated(EvaluatedItinerarySchedule schedule);
    void AcceptEvaluated(EvaluatedItinerarySchedule schedule);

    // Final selection
    IComparer<EvaluatedItinerarySchedule> ScheduleComparer { get; }
}
