using FluentValidation;

namespace TripMate.Application.Features.TravelGroups.Chat.SendGroupMessage;

public class SendGroupMessageCommandValidator : AbstractValidator<SendGroupMessageCommand>
{
    private static readonly string[] AllowedTypes = ["text", "broadcast", "roll_call", "split_bill", "poll"];

    public SendGroupMessageCommandValidator()
    {
        RuleFor(x => x.GroupId).GreaterThan(0);
        RuleFor(x => x.SenderUserId).GreaterThan(0);
        RuleFor(x => x.MessageType)
            .Must(t => AllowedTypes.Contains(t.ToLowerInvariant()))
            .WithMessage("Invalid message type.");
        RuleFor(x => x.Content)
            .NotEmpty()
            .WithMessage("Content is required.");
    }
}
