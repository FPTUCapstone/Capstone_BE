using FluentValidation;

using TripMate.Application.Features.TravelGroups.Common;

namespace TripMate.Application.Features.TravelGroups.Chat.SendGroupMessage;

public class SendGroupMessageCommandValidator : AbstractValidator<SendGroupMessageCommand>
{
    private static readonly string[] AllowedTypes = ["text", "broadcast", "roll_call", "split_bill", "poll"];

    public SendGroupMessageCommandValidator()
    {
        RuleFor(x => x.GroupId).GreaterThan(0);
        RuleFor(x => x.SenderUserId).GreaterThan(0);

        RuleFor(x => x.MessageType)
            .Cascade(CascadeMode.Stop)
            .Must(t => !string.IsNullOrWhiteSpace(t) && AllowedTypes.Contains(t.Trim().ToLowerInvariant()))
            .WithMessage("Invalid message type.")
            .WithErrorCode(TravelGroupErrorCodes.InvalidMessageType);

        RuleFor(x => x.ClientMessageId)
            .Must(id => id != Guid.Empty)
            .WithMessage("Client message ID must not be empty.")
            .WithErrorCode(TravelGroupErrorCodes.InvalidMessageContent);

        RuleFor(x => x).Must(x => GroupChatMetadata.IsValid(x.MetadataJson, x.MessageType))
            .WithMessage("Metadata must be a valid JSON object of at most 16000 characters and depth 16 matching the message type requirements (polls require 2 to 6 options; split bills require valid positive integer amounts, recipient details, and participant allocations).")
            .WithErrorCode(TravelGroupErrorCodes.InvalidPollOptions);

        RuleFor(x => x.Content)
            .Cascade(CascadeMode.Stop)
            .Must(c => !string.IsNullOrWhiteSpace(c))
            .WithMessage("Content is required.")
            .WithErrorCode(TravelGroupErrorCodes.InvalidMessageContent)
            .Must(c => c is not null && c.Trim().Length <= 4000)
            .WithMessage("Content must not exceed 4000 characters.")
            .WithErrorCode(TravelGroupErrorCodes.InvalidMessageContent);
    }
}