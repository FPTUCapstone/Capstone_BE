using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.GetInvitation;

/**
 * [UC-18] Get Group Invitation Query
 * Query to retrieve or generate an invitation code for a travel group.
 */
public sealed record GetGroupInvitationQuery(long GroupId, long CurrentUserId)
    : IRequest<Result<GetGroupInvitationResponse>>;