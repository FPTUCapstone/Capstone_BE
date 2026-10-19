using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

/// <summary>
/// Dữ liệu đầu vào từ yêu cầu lập lịch phục vụ hàm mục tiêu WeightedUtility.
/// Tách rời khỏi cấu hình thuật toán của CspOptions.
/// </summary>
public sealed record WeightedRequestInputs(
    IReadOnlyDictionary<long, double> BaseByPoiId,
    PersonalParams Params,
    ITimeFitProviderFactory TimeFit,
    string BaseSource = "base-only");
