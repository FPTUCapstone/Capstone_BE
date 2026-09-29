using FluentValidation;

using TripMate.Domain.Enums;

namespace TripMate.Application.Features.RecommendationFeedback.Capture;

public sealed class CaptureRecommendationFeedbackCommandValidator
    : AbstractValidator<CaptureRecommendationFeedbackCommand>
{
    public CaptureRecommendationFeedbackCommandValidator()
    {
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
        RuleFor(command => command.ClientEventId).NotEqual(Guid.Empty);
        RuleFor(command => command.EventType).IsInEnum();
        RuleFor(command => command.PoiId).GreaterThan(0);
        RuleFor(command => command.ItineraryId)
            .GreaterThan(0)
            .When(command => command.ItineraryId.HasValue);
        RuleFor(command => command.Source).IsInEnum();
        RuleFor(command => command)
            .Must(HaveValidSourceContext)
            .WithMessage("Itinerary context must match the capture source.");
        RuleFor(command => command)
            .Must(HaveValidTypeShape)
            .WithMessage("Event positions and source must match the event type.");
    }

    private static bool HaveValidSourceContext(CaptureRecommendationFeedbackCommand command) =>
        command.Source == RecommendationCaptureSource.Itinerary
            ? command.ItineraryId.HasValue
            : !command.ItineraryId.HasValue;

    private static bool HaveValidTypeShape(CaptureRecommendationFeedbackCommand command) =>
        command.EventType switch
        {
            RecommendationEventType.Like or RecommendationEventType.Dislike =>
                command.OriginalPosition is null && command.NewPosition is null,
            RecommendationEventType.Skip =>
                command.Source == RecommendationCaptureSource.Itinerary
                && command.OriginalPosition > 0
                && command.NewPosition is null,
            RecommendationEventType.Reorder =>
                command.Source == RecommendationCaptureSource.Itinerary
                && command.OriginalPosition > 0
                && command.NewPosition > 0,
            _ => false,
        };
}