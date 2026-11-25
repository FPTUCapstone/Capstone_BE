using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.GetConversations;

public sealed record GetTravelGroupConversationsQuery(
    long UserId,
    int Limit = GetTravelGroupConversationsQuery.DefaultLimit,
    long? BeforeGroupId = null) : IRequest<Result<GetTravelGroupConversationsResponse>>
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;
    public const string InvalidPaging = "travel_group.conversations.invalid_paging";
}