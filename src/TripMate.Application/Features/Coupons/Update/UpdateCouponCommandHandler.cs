using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Coupons.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Coupons.Update;

public sealed class UpdateCouponCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser,
    IDateTimeProvider clock) : IRequestHandler<UpdateCouponCommand, Result<OperatorCouponResponse>>
{
    public async Task<Result<OperatorCouponResponse>> Handle(UpdateCouponCommand request,
        CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue) return Denied();
        var operatorId = currentUser.UserId.Value;
        if (!await IsEligibleOperator(operatorId, cancellationToken)) return Denied();

        var coupon = await dbContext.Vouchers.Include(voucher => voucher.ApplicableTours)
            .SingleOrDefaultAsync(voucher => voucher.Id == request.CouponId && voucher.OwnerOperatorUserId == operatorId,
                cancellationToken);
        if (coupon is null)
            return Result.Failure<OperatorCouponResponse>(CouponErrorCodes.NotFound, "The requested coupon was not found.");

        var tourResult = await ResolveEligibleTours(operatorId, request.ApplicableTourIds, cancellationToken);
        if (!tourResult.IsSuccess)
            return Result.Failure<OperatorCouponResponse>(tourResult.ErrorCode ?? CouponErrorCodes.InvalidScope,
                tourResult.ErrorMessage ?? "The selected tour scope is invalid.");

        try
        {
            coupon.Update(request.Name, request.DiscountType, request.DiscountValue, request.MaxDiscountAmount,
                request.MinOrderAmount, request.UsageLimit, request.UsageLimitPerUser, request.ValidFromUtc,
                request.ValidToUtc, tourResult.Value, clock.UtcNow);
        }
        catch (ArgumentException)
        {
            return Result.Failure<OperatorCouponResponse>(CouponErrorCodes.InvalidScope,
                "Coupon details or the selected tour scope are invalid.");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success(OperatorCouponResponse.From(coupon));
    }

    private async Task<bool> IsEligibleOperator(long operatorId, CancellationToken cancellationToken) =>
        await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
            profile.UserId == operatorId && profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
            profile.User.Role == UserRole.TourOperator && profile.User.Status == AccountStatus.Active,
            cancellationToken);

    private async Task<Result<IReadOnlyCollection<Tour>>> ResolveEligibleTours(long operatorId,
        IReadOnlyCollection<long>? requestedIds, CancellationToken cancellationToken)
    {
        var ids = requestedIds?.Distinct().ToArray() ?? [];
        if (ids.Length == 0)
            return Result.Failure<IReadOnlyCollection<Tour>>(CouponErrorCodes.InvalidScope,
                "At least one applicable tour is required.");
        var tours = await dbContext.Tours.Where(tour => ids.Contains(tour.Id)).ToListAsync(cancellationToken);
        if (tours.Count != ids.Length)
            return Result.Failure<IReadOnlyCollection<Tour>>(CouponErrorCodes.TourNotFound,
                "One or more selected tours do not exist.");
        if (tours.Any(tour => tour.OperatorUserId != operatorId))
            return Result.Failure<IReadOnlyCollection<Tour>>(CouponErrorCodes.TourNotOwned,
                "A selected tour is not owned by this operator.");
        if (tours.Any(tour => !IsPublicCouponTour(tour, clock.UtcNow)))
            return Result.Failure<IReadOnlyCollection<Tour>>(CouponErrorCodes.TourNotEligible,
                "Coupons can only apply to published tours.");
        return Result.Success<IReadOnlyCollection<Tour>>(tours);
    }

    private static bool IsPublicCouponTour(Tour tour, DateTimeOffset now) => tour.Status == TourStatus.Published ||
        (tour.Status == TourStatus.Approved && tour.PublishedAtUtc.HasValue && tour.PublishedAtUtc <= now);

    private static Result<OperatorCouponResponse> Denied() =>
        Result.Failure<OperatorCouponResponse>(CouponErrorCodes.OperatorAccessRequired,
            "An active approved Tour Operator account is required.");
}
