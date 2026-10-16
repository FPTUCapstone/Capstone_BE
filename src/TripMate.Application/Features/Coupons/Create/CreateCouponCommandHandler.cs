using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Coupons.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Coupons.Create;

public sealed class CreateCouponCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider) : IRequestHandler<CreateCouponCommand, Result<CreateCouponResponse>>
{
    public async Task<Result<CreateCouponResponse>> Handle(CreateCouponCommand request, CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue) return Denied();
        var operatorId = currentUserService.UserId.Value;
        var now = dateTimeProvider.UtcNow;
        var operatorProfile = await dbContext.OperatorProfiles.AsNoTracking().FirstOrDefaultAsync(profile =>
            profile.UserId == operatorId && profile.ApprovalStatus == OperatorApprovalStatus.Approved
            && profile.User.Status == AccountStatus.Active && profile.User.Role == UserRole.TourOperator, cancellationToken);
        if (operatorProfile is null) return Denied();
        var ids = request.ApplicableTourIds?.Distinct().ToArray() ?? [];
        if (ids.Length == 0)
        {
            return Result.Failure<CreateCouponResponse>(
                CouponErrorCodes.InvalidScope,
                "At least one applicable tour is required.");
        }

        var tours = await dbContext.Tours.Where(tour => ids.Contains(tour.Id)).ToListAsync(cancellationToken);
        if (tours.Count != ids.Length)
        {
            return Result.Failure<CreateCouponResponse>(
                CouponErrorCodes.TourNotFound,
                "One or more selected tours do not exist.");
        }

        if (tours.Any(tour => tour.OperatorUserId != operatorId))
        {
            return Result.Failure<CreateCouponResponse>(
                CouponErrorCodes.TourNotOwned,
                "A selected tour is not owned by this operator.");
        }

        // A coupon must only target a public tour. Keep legacy published records readable
        // while UC-35 transitions all new publication actions to TourStatus.Published.
        if (tours.Any(tour => !IsPublicCouponTour(tour, now)))
        {
            return Result.Failure<CreateCouponResponse>(
                CouponErrorCodes.TourNotEligible,
                "Coupons can only apply to published tours.");
        }

        var code = request.Code.Trim().ToUpperInvariant();
        var voucher = Voucher.Create(operatorId, code, request.DiscountType, request.DiscountValue, request.MaxDiscountAmount,
            request.MinOrderAmount, request.UsageLimit, request.UsageLimitPerUser, request.ValidFromUtc, request.ValidToUtc, tours, now);

        try
        {
            return await dbContext.ExecuteInSerializableTransactionAsync(
                async transactionCancellationToken =>
                {
                    if (await dbContext.Vouchers.AsNoTracking().AnyAsync(
                            existingVoucher => existingVoucher.Code == code,
                            transactionCancellationToken))
                    {
                        return Result.Failure<CreateCouponResponse>(
                            CouponErrorCodes.CodeConflict,
                            "This coupon code already exists.");
                    }

                    dbContext.Vouchers.Add(voucher);
                    await dbContext.SaveChangesAsync(transactionCancellationToken);
                    return Result.Success(new CreateCouponResponse(voucher.Id, voucher.Code));
                },
                cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ClearTrackedEntities();
            var collidingCodeExists = await dbContext.Vouchers.AsNoTracking().AnyAsync(
                existingVoucher => existingVoucher.Code == code,
                cancellationToken);

            if (collidingCodeExists)
            {
                return Result.Failure<CreateCouponResponse>(
                    CouponErrorCodes.CodeConflict,
                    "This coupon code already exists.");
            }

            throw;
        }
    }
    private static bool IsPublicCouponTour(Tour tour, DateTimeOffset now) =>
        tour.Status == TourStatus.Published ||
        (tour.Status == TourStatus.Approved && tour.PublishedAtUtc.HasValue && tour.PublishedAtUtc <= now);

    private static Result<CreateCouponResponse> Denied() => Result.Failure<CreateCouponResponse>(CouponErrorCodes.OperatorAccessRequired, "An active approved Tour Operator account is required.");
}