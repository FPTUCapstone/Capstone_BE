using MediatR;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.Chat.GetGroupMessages;

public sealed record GetGroupMessagesQuery(
    long GroupId,
    long UserId,
    int Limit = 50,
    long? BeforeMessageId = null) : IRequest<Result<IReadOnlyList<GroupMessageDto>>>;
