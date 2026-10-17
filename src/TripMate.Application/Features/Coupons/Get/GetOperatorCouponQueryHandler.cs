using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Coupons.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Coupons.Get;

public sealed class GetOperatorCouponQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    : IRequestHandler<GetOperatorCouponQuery, Result<OperatorCouponResponse>>
{
    public async Task<Result<OperatorCouponResponse>> Handle(GetOperatorCouponQuery request,
        CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue) return Denied();
        var operatorId = currentUser.UserId.Value;
        var eligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
            profile.UserId == operatorId && profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
            profile.User.Role == UserRole.TourOperator && profile.User.Status == AccountStatus.Active,
            cancellationToken);
        if (!eligible) return Denied();

        var coupon = await dbContext.Vouchers.AsNoTracking()
            .Include(voucher => voucher.ApplicableTours)
            .SingleOrDefaultAsync(voucher => voucher.Id == request.CouponId && voucher.OwnerOperatorUserId == operatorId,
                cancellationToken);
        return coupon is null
            ? Result.Failure<OperatorCouponResponse>(CouponErrorCodes.NotFound, "The requested coupon was not found.")
            : Result.Success(OperatorCouponResponse.From(coupon));
    }

    private static Result<OperatorCouponResponse> Denied() =>
        Result.Failure<OperatorCouponResponse>(CouponErrorCodes.OperatorAccessRequired,
            "An active approved Tour Operator account is required.");
}
