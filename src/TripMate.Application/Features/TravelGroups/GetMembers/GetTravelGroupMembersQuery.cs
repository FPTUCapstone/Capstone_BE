using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.GetMembers;

public sealed record GetTravelGroupMembersQuery(long GroupId, long TravelerUserId)
    : IRequest<Result<GetTravelGroupMembersResponse>>;