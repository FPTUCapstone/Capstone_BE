using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Revenue.Common;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Revenue.GetReport;

public sealed class GetOperatorRevenueReportQueryHandler(
    IApplicationDbContext db, ICurrentUserService currentUser, OperatorRevenueAggregator aggregator)
    : IRequestHandler<GetOperatorRevenueReportQuery, Result<OperatorRevenueReportResponse>>
{
    public async Task<Result<OperatorRevenueReportResponse>> Handle(GetOperatorRevenueReportQuery request, CancellationToken ct)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator) || request.EndDate < request.StartDate)
            return Result.Failure<OperatorRevenueReportResponse>(OperatorTourErrorCodes.AccessRequired, "An active approved Tour Operator account is required.");

        // P2: Validate granularity parameter
        if (!OperatorRevenueAggregator.IsValidGranularity(request.Granularity))
            return Result.Failure<OperatorRevenueReportResponse>(OperatorRevenueErrorCodes.InvalidGranularity, "Granularity must be 'daily', 'weekly', or 'monthly'.");

        var active = await db.OperatorProfiles.AnyAsync(p => p.UserId == operatorId && p.ApprovalStatus == OperatorApprovalStatus.Approved && p.User.Status == AccountStatus.Active, ct);
        if (!active) return Result.Failure<OperatorRevenueReportResponse>(OperatorTourErrorCodes.AccessRequired, "An active approved Tour Operator account is required.");

        int page = Math.Max(1, request.Page), pageSize = Math.Clamp(request.PageSize, 1, 100);

        var aggregate = await aggregator.AggregateAsync(
            operatorId, request.StartDate, request.EndDate, request.Granularity, request.TourId, ct);
        var details = aggregate.AllTourDetails;

        return Result.Success(new OperatorRevenueReportResponse(
            aggregate.Summary, aggregate.ChartData,
            details.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            details.Count, page, pageSize,
            details.Count == 0 ? 0 : (int)Math.Ceiling(details.Count / (double)pageSize)));
    }
}