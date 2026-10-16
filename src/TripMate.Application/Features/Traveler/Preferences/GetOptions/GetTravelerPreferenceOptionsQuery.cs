using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Preferences.Common;

namespace TripMate.Application.Features.Traveler.Preferences.GetOptions;

/// <summary>UC-09: the shared option contract used by Web and Mobile (spec §6.1).</summary>
public sealed record GetTravelerPreferenceOptionsQuery(long TravelerUserId)
    : IRequest<Result<TravelerPreferenceOptionsDto>>;

public sealed class GetTravelerPreferenceOptionsQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<GetTravelerPreferenceOptionsQuery, Result<TravelerPreferenceOptionsDto>>
{
    public async Task<Result<TravelerPreferenceOptionsDto>> Handle(
        GetTravelerPreferenceOptionsQuery request,
        CancellationToken cancellationToken)
    {
        if (!await TravelerPreferenceAccess.IsActiveTravelerAsync(
                dbContext,
                request.TravelerUserId,
                cancellationToken))
        {
            return Result.Failure<TravelerPreferenceOptionsDto>(
                TravelerPreferenceErrors.Forbidden,
                "Only an active Traveler can access travel preferences.");
        }

        return Result.Success(new TravelerPreferenceOptionsDto(
            InterestOptionCatalog.Fixed.Options,
            TravelerPreferenceKeys.TransportModes,
            TravelerPreferenceKeys.TravelPaces,
            TravelerPreferenceKeys.FoodPreferences,
            TravelerPreferenceKeys.RiskTolerances));
    }
}
