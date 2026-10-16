using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Coupons.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Coupons.GetEligibleTours;

public sealed class GetEligibleCouponToursQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider clock)
    : IRequestHandler<GetEligibleCouponToursQuery, Result<IReadOnlyList<EligibleCouponTourResponse>>>
{
    public async Task<Result<IReadOnlyList<EligibleCouponTourResponse>>> Handle(
        GetEligibleCouponToursQuery request,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Denied();
        }

        var operatorId = currentUserService.UserId.Value;
        var hasApprovedProfile = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(
            profile => profile.UserId == operatorId
                && profile.ApprovalStatus == OperatorApprovalStatus.Approved
                && profile.User.Status == AccountStatus.Active
                && profile.User.Role == UserRole.TourOperator,
            cancellationToken);
        if (!hasApprovedProfile)
        {
            return Denied();
        }

        // UC-35: Published is the public, bookable lifecycle state. Older records that were
        // published before the explicit status transition remain valid for compatibility.
        var now = clock.UtcNow;
        var tours = await dbContext.Tours.AsNoTracking()
            .Where(tour => tour.OperatorUserId == operatorId &&
                (tour.Status == TourStatus.Published ||
                 (tour.Status == TourStatus.Approved && tour.PublishedAtUtc.HasValue && tour.PublishedAtUtc <= now)))
            .OrderBy(tour => tour.Title)
            .ThenBy(tour => tour.Id)
            .Select(tour => new EligibleCouponTourResponse(
                tour.Id,
                tour.Title,
                tour.Destinations.OrderBy(destination => destination.SequenceNo)
                    .Select(destination => destination.Destination.Name)
                    .FirstOrDefault(),
                tour.BasePrice))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<EligibleCouponTourResponse>>(tours);
    }

    private static Result<IReadOnlyList<EligibleCouponTourResponse>> Denied() =>
        Result.Failure<IReadOnlyList<EligibleCouponTourResponse>>(
            CouponErrorCodes.OperatorAccessRequired,
            "An active approved Tour Operator account is required.");
}
