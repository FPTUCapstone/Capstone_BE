using System.Globalization;

using FluentValidation;

using TripMate.Application.Features.Tours.Search;

namespace TripMate.Application.Features.SupportChat;

public sealed class ChatQueryPlanValidator : AbstractValidator<ChatQueryPlan>
{
    // Bounds the assisted form prefill. Existing checkout validates actual booking capacity.
    public const int MaximumPartySize = 50;

    public ChatQueryPlanValidator()
    {
        RuleFor(plan => plan.Intent).Must(intent => ChatIntents.All.Contains(intent));
        RuleFor(plan => plan.Search).MaximumLength(TourSearchCriteria.DestinationMaxLength);
        RuleFor(plan => plan.Destination).MaximumLength(TourSearchCriteria.DestinationMaxLength);
        RuleFor(plan => plan.ResourceId).Must(id => id is null ||
            long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0);
        RuleFor(plan => plan.DepartureDate).Must(date => date is null ||
            DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) &&
            TourSearchCriteria.TryGetDepartureBoundsUtc(parsed, out _, out _));
        RuleFor(plan => plan.MaxPrice).InclusiveBetween(0, TourSearchCriteria.MaximumWholeVndPrice)
            .When(plan => plan.MaxPrice.HasValue);
        RuleFor(plan => plan.PartySize).InclusiveBetween(1, MaximumPartySize)
            .When(plan => plan.PartySize.HasValue);
        RuleFor(plan => plan.ChildCount).InclusiveBetween(0, MaximumPartySize)
            .When(plan => plan.ChildCount.HasValue);
        RuleFor(plan => plan.InfantCount).InclusiveBetween(0, MaximumPartySize)
            .When(plan => plan.InfantCount.HasValue);
        RuleFor(plan => plan.RequestedParticipants).LessThanOrEqualTo(MaximumPartySize)
            .When(plan => plan.RequestedParticipants.HasValue);
    }
}
