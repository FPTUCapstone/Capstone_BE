using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

namespace TripMate.Api.Authorization;

public static class TripReviewAuthorizationPolicies
{
    public const string ActiveTraveler = "TripReview.ActiveTraveler";
}

public sealed class ActiveTravelerRequirement : IAuthorizationRequirement;

public sealed class ActiveTravelerAuthorizationHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser) : AuthorizationHandler<ActiveTravelerRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActiveTravelerRequirement requirement)
    {
        if (currentUser.UserId is not > 0
            || currentUser.Role != nameof(UserRole.Traveler))
        {
            return;
        }

        var cancellationToken = context.Resource is HttpContext httpContext
            ? httpContext.RequestAborted
            : CancellationToken.None;
        var isActiveTraveler = await dbContext.Users.AsNoTracking()
            .AnyAsync(user => user.Id == currentUser.UserId.Value
                && user.Role == UserRole.Traveler
                && user.Status == AccountStatus.Active, cancellationToken);

        if (isActiveTraveler)
            context.Succeed(requirement);
    }
}