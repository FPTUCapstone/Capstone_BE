using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Emergency.Common;

internal static class EmergencyOperatorAccess
{
    public static Task<bool> IsEligibleOperatorAsync(
        IApplicationDbContext dbContext,
        long operatorId,
        CancellationToken cancellationToken) =>
        dbContext.OperatorProfiles.AnyAsync(profile =>
            profile.UserId == operatorId
            && profile.ApprovalStatus == OperatorApprovalStatus.Approved
            && profile.User.Role == UserRole.TourOperator
            && profile.User.Status == AccountStatus.Active, cancellationToken);
}