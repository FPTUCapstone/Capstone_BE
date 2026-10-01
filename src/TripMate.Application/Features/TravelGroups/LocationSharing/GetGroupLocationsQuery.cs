using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed record GetGroupLocationsQuery(long GroupId, long TravelerUserId)
    : IRequest<Result<GroupLocationsResponse>>;