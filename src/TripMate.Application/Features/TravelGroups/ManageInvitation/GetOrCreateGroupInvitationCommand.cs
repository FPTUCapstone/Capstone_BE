using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.GetInvitation;

namespace TripMate.Application.Features.TravelGroups.ManageInvitation;

public sealed record GetOrCreateGroupInvitationCommand(
    long GroupId,
    long CurrentUserId,
    Guid IdempotencyKey) : IRequest<Result<GetGroupInvitationResponse>>;