using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.RemoveGroupMember;

public sealed record RemoveGroupMemberCommand(
    long GroupId,
    long MemberUserId,
    long HostUserId,
    Guid IdempotencyKey) : IRequest<Result<RemoveGroupMemberResponse>>;