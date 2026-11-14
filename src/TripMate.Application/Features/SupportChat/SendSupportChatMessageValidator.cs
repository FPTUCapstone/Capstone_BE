using System.Globalization;

using FluentValidation;

namespace TripMate.Application.Features.SupportChat;

public sealed class SendSupportChatMessageValidator : AbstractValidator<SendSupportChatMessage>
{
    public SendSupportChatMessageValidator()
    {
        RuleFor(x => x.Message).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.History).Must(history => history is null || history.Count <= 10);
        RuleForEach(x => x.History).NotNull().ChildRules(message =>
        {
            message.RuleFor(x => x.Role).Must(role => role is "user" or "assistant");
            message.RuleFor(x => x.Content).NotEmpty().MaximumLength(2000);
        });
        When(x => x.Context is not null, () =>
        {
            RuleFor(x => x.Context!.Kind).Must(kind => kind is "refund" or "itinerary" or "tour");
            RuleFor(x => x.Context!.Id).Must(id => long.TryParse(id, NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) && value > 0);
        });
    }
}