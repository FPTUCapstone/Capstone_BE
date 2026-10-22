using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Payouts.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Payouts.GetHistory;

public sealed class GetOperatorPayoutHistoryQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetOperatorPayoutHistoryQuery, Result<PayoutHistoryResponse>>
{
    public async Task<Result<PayoutHistoryResponse>> Handle(GetOperatorPayoutHistoryQuery request, CancellationToken ct)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator)
            || !await db.OperatorProfiles.AnyAsync(p => p.UserId == operatorId
                && p.ApprovalStatus == OperatorApprovalStatus.Approved && p.User.Status == AccountStatus.Active, ct))
        {
            return Result.Failure<PayoutHistoryResponse>(
                OperatorPayoutErrorCodes.OperatorNotApproved, "An active approved Tour Operator account is required.");
        }

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var query = db.Payouts.AsNoTracking()
            .Where(p => p.OperatorUserId == operatorId)
            .OrderByDescending(p => p.PeriodEnd).ThenByDescending(p => p.Id);

        var totalCount = await query.CountAsync(ct);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var items = rows.Select(p => new PayoutHistoryItemDto(
            p.PayoutCode,
            p.PeriodCount,
            p.PeriodStart.ToString("yyyy-MM-dd"),
            p.PeriodEnd.ToString("yyyy-MM-dd"),
            p.NetAmount,
            p.Status,
            p.RequestedAtUtc,
            p.Status == Payout.StatusSettled ? p.LastGatewayAttemptAtUtc : null,
            p.RejectionReason,
            p.IsCancellable)).ToList();

        return Result.Success(new PayoutHistoryResponse(
            items, page, pageSize, totalCount, totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize)));
    }
}
