using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Coupons.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Coupons.Lifecycle;

public sealed class SetCouponActivationCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser,
    IDateTimeProvider clock) : IRequestHandler<SetCouponActivationCommand, Result<OperatorCouponResponse>>
{
    public async Task<Result<OperatorCouponResponse>> Handle(SetCouponActivationCommand request,
        CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue) return Denied();
        var operatorId = currentUser.UserId.Value;
        var eligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
            profile.UserId == operatorId && profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
            profile.User.Role == UserRole.TourOperator && profile.User.Status == AccountStatus.Active,
            cancellationToken);
        if (!eligible) return Denied();

        var coupon = await dbContext.Vouchers.Include(voucher => voucher.ApplicableTours)
            .SingleOrDefaultAsync(voucher => voucher.Id == request.CouponId && voucher.OwnerOperatorUserId == operatorId,
                cancellationToken);
        if (coupon is null)
            return Result.Failure<OperatorCouponResponse>(CouponErrorCodes.NotFound, "The requested coupon was not found.");

        try
        {
            if (request.Activate) coupon.Activate(clock.UtcNow);
            else coupon.Deactivate(clock.UtcNow);
        }
        catch (InvalidOperationException)
        {
            return Result.Failure<OperatorCouponResponse>(CouponErrorCodes.InvalidState,
                "An expired coupon cannot be activated. Update its end date first.");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success(OperatorCouponResponse.From(coupon));
    }

    private static Result<OperatorCouponResponse> Denied() =>
        Result.Failure<OperatorCouponResponse>(CouponErrorCodes.OperatorAccessRequired,
            "An active approved Tour Operator account is required.");
}
