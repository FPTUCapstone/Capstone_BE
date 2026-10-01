using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed record PublishGroupLocationCommand(
    long GroupId, long TravelerUserId, decimal Latitude, decimal Longitude, string? SessionVersion = null)
    : IRequest<Result<GroupLocationResponse>>;