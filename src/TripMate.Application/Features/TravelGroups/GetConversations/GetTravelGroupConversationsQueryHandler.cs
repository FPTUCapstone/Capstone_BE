using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.GetConversations;

public sealed class GetTravelGroupConversationsQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<GetTravelGroupConversationsQuery, Result<GetTravelGroupConversationsResponse>>
{
    public async Task<Result<GetTravelGroupConversationsResponse>> Handle(
        GetTravelGroupConversationsQuery request,
        CancellationToken cancellationToken)
    {
        if (request.Limit < 1 || request.Limit > GetTravelGroupConversationsQuery.MaxLimit
            || request.BeforeGroupId is <= 0)
        {
            return Result.Failure<GetTravelGroupConversationsResponse>(
                GetTravelGroupConversationsQuery.InvalidPaging,
                $"Limit must be between 1 and {GetTravelGroupConversationsQuery.MaxLimit}; beforeGroupId must be positive when supplied.");
        }

        var rows = await dbContext.TravelGroups
            .AsNoTracking()
            .Where(group => (group.Status == TravelGroupStatus.Active || group.Status == TravelGroupStatus.Closed)
                && group.GroupMembers.Any(member => member.UserId == request.UserId
                    && member.Status == GroupMemberStatus.Active)
                && (!request.BeforeGroupId.HasValue || group.Id < request.BeforeGroupId.Value))
            .OrderByDescending(group => group.Id)
            .Select(group => new
            {
                group.Id,
                group.Name,
                group.ItineraryId,
                group.Itinerary.Title,
                group.Itinerary.SourceType,
                group.HostUserId,
                MemberCount = group.GroupMembers.Count(member => member.Status == GroupMemberStatus.Active),
                group.Status,
                UnreadCount = dbContext.GroupMessages.Count(message => message.GroupId == group.Id
                    && message.SenderUserId != request.UserId
                    && message.Id > (group.GroupMembers
                        .Where(member => member.UserId == request.UserId && member.Status == GroupMemberStatus.Active)
                        .Select(member => member.LastReadChatMessageId)
                        .FirstOrDefault() ?? 0)),
                LastMessage = dbContext.GroupMessages
                    .Where(message => message.GroupId == group.Id)
                    .OrderByDescending(message => message.Id)
                    .Select(message => message.Content)
                    .FirstOrDefault(),
                LastMessageAtUtc = dbContext.GroupMessages
                    .Where(message => message.GroupId == group.Id)
                    .OrderByDescending(message => message.Id)
                    .Select(message => (DateTimeOffset?)message.CreatedAtUtc)
                    .FirstOrDefault(),
            })
            .Take(request.Limit + 1)
            .ToListAsync(cancellationToken);

        var items = rows.Take(request.Limit)
            .Select(row => new TravelGroupConversationResponse(
                row.Id, row.Name, row.ItineraryId, row.Title, row.SourceType, row.HostUserId,
                row.MemberCount, row.Status.ToString(), row.LastMessage, row.LastMessageAtUtc, row.UnreadCount))
            .ToList();

        return Result.Success(new GetTravelGroupConversationsResponse(
            items, rows.Count > request.Limit ? items[^1].GroupId : null));
    }
}