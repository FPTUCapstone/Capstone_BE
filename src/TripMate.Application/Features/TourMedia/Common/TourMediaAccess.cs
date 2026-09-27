using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TourMedia.Common;

internal sealed record TourMediaAccess(User? Actor, Tour? Tour, string? FailureCode)
{
    public bool IsAuthorized => FailureCode is null;
}

internal static class TourMediaAccessResolver
{
    public static async Task<TourMediaAccess> AuthorizeAsync(
        IApplicationDbContext dbContext,
        long tourId,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        User? actor = await dbContext.Users.SingleOrDefaultAsync(user =>
            user.Id == actorUserId &&
            user.Role == UserRole.TourOperator &&
            user.Status == AccountStatus.Active,
            cancellationToken);
        if (actor is null || !await dbContext.OperatorProfiles.AnyAsync(profile =>
                profile.UserId == actorUserId &&
                profile.ApprovalStatus == OperatorApprovalStatus.Approved,
                cancellationToken))
        {
            return new TourMediaAccess(null, null, TourMediaErrorCodes.OperatorAccessRequired);
        }

        Tour? tour = await dbContext.Tours.SingleOrDefaultAsync(candidate =>
            candidate.Id == tourId && candidate.OperatorUserId == actorUserId,
            cancellationToken);
        return tour is null
            ? new TourMediaAccess(null, null, TourMediaErrorCodes.TourNotFound)
            : new TourMediaAccess(actor, tour, null);
    }

    public static bool AllowsMaterialChange(TourStatus status) =>
        status is TourStatus.Draft or TourStatus.Rejected;
}

internal static class TourMediaProjection
{
    public static TourMediaDto ToDto(global::TripMate.Domain.Entities.TourMedia media) => new(
        media.Id,
        media.TourId,
        media.DeliveryUrl,
        media.Caption,
        media.AltText,
        media.SortOrder,
        media.IsPrimary,
        media.CreatedAtUtc,
        media.UpdatedAtUtc);
}