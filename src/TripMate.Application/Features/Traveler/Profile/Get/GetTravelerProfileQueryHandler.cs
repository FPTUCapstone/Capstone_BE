using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Profile.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Profile.Get;

public sealed class GetTravelerProfileQueryHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser)
    : IRequestHandler<GetTravelerProfileQuery, Result<TravelerProfileDto>>
{
    public async Task<Result<TravelerProfileDto>> Handle(
        GetTravelerProfileQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long userId || currentUser.Role != nameof(UserRole.Traveler))
        {
            return Result.Failure<TravelerProfileDto>(
                TravelerProfileErrorCodes.Forbidden,
                "Traveler access is required.");
        }

        var user = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<TravelerProfileDto>(
                TravelerProfileErrorCodes.NotFound,
                "Traveler profile not found.");
        }

        if (user.Role != UserRole.Traveler || user.Status != AccountStatus.Active)
        {
            return Result.Failure<TravelerProfileDto>(
                TravelerProfileErrorCodes.Forbidden,
                "The traveler account is not active.");
        }

        var profile = await db.TravelerProfiles.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);

        return Result.Success(new TravelerProfileDto(
            user.Id,
            user.FullName,
            user.Email,
            user.PhoneNumber,
            profile?.DateOfBirth,
            user.AvatarUrl));
    }
}