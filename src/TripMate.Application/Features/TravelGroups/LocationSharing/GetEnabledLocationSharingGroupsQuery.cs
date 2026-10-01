using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed record GetEnabledLocationSharingGroupsQuery(long TravelerUserId)
    : IRequest<Result<EnabledLocationSharingGroupsResponse>>;

public sealed record EnabledLocationSharingGroupsResponse(IReadOnlyList<long> GroupIds);