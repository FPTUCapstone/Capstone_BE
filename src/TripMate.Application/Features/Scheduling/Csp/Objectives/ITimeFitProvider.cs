namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

/// <summary>
/// Cung cấp hệ số phù hợp thời gian (TimeFit Factor) theo mốc phút mô hình.
/// </summary>
public interface ITimeFitProvider
{
    /// <summary>Hệ số phù hợp thời gian cho lượt ghé [startMin, endMin] tính theo phút mô hình.</summary>
    double Factor(int node, int startMin, int endMin);

    /// <summary>Hệ số lớn nhất có thể của node trong ngày (dùng cho cận trên admissible).</summary>
    double MaxFactor(int node);
}

/// <summary>
/// Triển khai mặc định hệ số TimeFit không đổi (1.0).
/// </summary>
public sealed class ConstantTimeFit : ITimeFitProvider
{
    public double Factor(int node, int startMin, int endMin) => 1.0;
    public double MaxFactor(int node) => 1.0;
}

/// <summary>
/// Factory sinh bộ tính toán hệ số TimeFit cho một RoutingContext cụ thể.
/// </summary>
public interface ITimeFitProviderFactory
{
    ITimeFitProvider Create(Routing.RoutingContext ctx);
}

/// <summary>
/// Triển khai mặc định sinh ConstantTimeFit cho mọi context.
/// </summary>
public sealed class ConstantTimeFitFactory : ITimeFitProviderFactory
{
    public ITimeFitProvider Create(Routing.RoutingContext ctx) => new ConstantTimeFit();
}

