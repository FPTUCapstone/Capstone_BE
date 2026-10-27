using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Bookings.Common;

public static class OperatorBookingAccess
{
    public const string AccessRequiredMessage = "An active approved Tour Operator account is required.";

    /// <summary>
    /// Resolves the signed-in operator id. Only a Tour Operator with an approved application and an
    /// active account may view customer bookings.
    /// </summary>
    public static async Task<Result<long>> ResolveOperatorIdAsync(
        IApplicationDbContext dbContext,
        ICurrentUserService currentUser,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorId || operatorId <= 0
            || currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<long>(OperatorTourErrorCodes.AccessRequired, AccessRequiredMessage);
        }

        bool active = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(
            profile => profile.UserId == operatorId
                && profile.ApprovalStatus == OperatorApprovalStatus.Approved
                && profile.User.Status == AccountStatus.Active,
            cancellationToken);

        return active
            ? Result.Success(operatorId)
            : Result.Failure<long>(OperatorTourErrorCodes.AccessRequired, AccessRequiredMessage);
    }
}
