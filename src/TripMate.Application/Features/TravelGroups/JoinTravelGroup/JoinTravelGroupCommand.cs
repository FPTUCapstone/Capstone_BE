using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.JoinTravelGroup;

public sealed record JoinTravelGroupCommand(
    string InvitationCode,
    long TravelerUserId,
    Guid IdempotencyKey) : IRequest<Result<JoinTravelGroupResponse>>;