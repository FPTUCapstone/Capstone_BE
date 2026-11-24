using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.Chat.SendGroupMessage;

public sealed record SendGroupMessageCommand(
    long GroupId,
    long SenderUserId,
    string MessageType,
    string Content,
    string? MetadataJson = null,
    Guid? ClientMessageId = null) : IRequest<Result<GroupMessageDto>>;