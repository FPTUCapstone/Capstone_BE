using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed record GetLocationSharingQuery(long GroupId, long TravelerUserId)
    : IRequest<Result<LocationSharingResponse>>;