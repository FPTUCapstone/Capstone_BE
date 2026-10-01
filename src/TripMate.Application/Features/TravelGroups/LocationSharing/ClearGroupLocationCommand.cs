using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed record ClearGroupLocationCommand(long GroupId, long TravelerUserId)
    : IRequest<Result>;