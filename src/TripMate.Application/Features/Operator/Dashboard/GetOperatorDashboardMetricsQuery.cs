using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Dashboard;

public sealed record GetOperatorDashboardMetricsQuery : IRequest<Result<OperatorDashboardMetricsResponse>>;

public sealed record OperatorDashboardMetricsResponse(
    int ActiveTourCount,
    int MonthlyBookingCount,
    decimal MonthlyNetAmount,
    int ActiveCouponCount,
    DateTimeOffset AsOfUtc,
    DateOnly MonthStart,
    DateOnly MonthEndExclusive);