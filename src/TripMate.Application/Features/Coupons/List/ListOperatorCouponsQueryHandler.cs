using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Coupons.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Coupons.List;

public sealed class ListOperatorCouponsQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    : IRequestHandler<ListOperatorCouponsQuery, Result<IReadOnlyList<OperatorCouponResponse>>>
{
    public async Task<Result<IReadOnlyList<OperatorCouponResponse>>> Handle(ListOperatorCouponsQuery request,
        CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue) return Denied();
        var operatorId = currentUser.UserId.Value;
        var eligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
            profile.UserId == operatorId && profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
            profile.User.Role == UserRole.TourOperator && profile.User.Status == AccountStatus.Active,
            cancellationToken);
        if (!eligible) return Denied();

        var coupons = await dbContext.Vouchers.AsNoTracking()
            .Include(voucher => voucher.ApplicableTours)
            .Where(voucher => voucher.OwnerOperatorUserId == operatorId)
            .OrderByDescending(voucher => voucher.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<OperatorCouponResponse>>(
            coupons.Select(OperatorCouponResponse.From).ToArray());
    }

    private static Result<IReadOnlyList<OperatorCouponResponse>> Denied() =>
        Result.Failure<IReadOnlyList<OperatorCouponResponse>>(CouponErrorCodes.OperatorAccessRequired,
            "An active approved Tour Operator account is required.");
}
