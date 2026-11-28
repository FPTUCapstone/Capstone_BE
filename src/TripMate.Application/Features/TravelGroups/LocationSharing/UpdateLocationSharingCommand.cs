using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed record UpdateLocationSharingCommand(long GroupId, long TravelerUserId, bool Enabled)
    : IRequest<Result<LocationSharingResponse>>;