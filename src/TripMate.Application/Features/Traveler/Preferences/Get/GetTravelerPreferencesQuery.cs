using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Preferences.Common;

namespace TripMate.Application.Features.Traveler.Preferences.Get;

/// <summary>UC-09: the authenticated Traveler's saved preferences (spec §6.2).</summary>
public sealed record GetTravelerPreferencesQuery(long TravelerUserId)
    : IRequest<Result<TravelerPreferencesDto>>;

public sealed class GetTravelerPreferencesQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<GetTravelerPreferencesQuery, Result<TravelerPreferencesDto>>
{
    public async Task<Result<TravelerPreferencesDto>> Handle(
        GetTravelerPreferencesQuery request,
        CancellationToken cancellationToken)
    {
        if (!await TravelerPreferenceAccess.IsActiveTravelerAsync(
                dbContext,
                request.TravelerUserId,
                cancellationToken))
        {
            return Result.Failure<TravelerPreferencesDto>(
                TravelerPreferenceErrors.Forbidden,
                "Only an active Traveler can access travel preferences.");
        }

        var profile = await dbContext.TravelerProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.UserId == request.TravelerUserId,
                cancellationToken);
        return Result.Success(TravelerPreferenceMapping.ToDto(profile, InterestOptionCatalog.Fixed));
    }
}
