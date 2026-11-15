using FluentValidation;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Bookings.Cancel;

public sealed class CancelBookingCommandValidator : AbstractValidator<CancelBookingCommand>
{
    public const int ReasonCategoryMaxLength = 64;
    public const int ReasonDetailMaxLength = 300;

    public CancelBookingCommandValidator()
    {
        RuleFor(command => command.BookingId).GreaterThan(0);
        RuleFor(command => command.IdempotencyKey).NotEmpty();
        RuleFor(command => command.ReasonCategory).MaximumLength(ReasonCategoryMaxLength);
        RuleFor(command => command.ReasonDetail).MaximumLength(ReasonDetailMaxLength);
        RuleFor(command => command)
            .Must(command => ComposedReasonLength(command.ReasonCategory, command.ReasonDetail)
                <= Booking.CancelReasonMaxLength)
            .WithMessage($"The combined cancellation reason must not exceed {Booking.CancelReasonMaxLength} characters.");
    }

    private static int ComposedReasonLength(string? category, string? detail)
    {
        string? normalizedCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        string? normalizedDetail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
        return normalizedCategory is null
            ? normalizedDetail?.Length ?? 0
            : normalizedDetail is null
                ? normalizedCategory.Length
                : normalizedCategory.Length + 2 + normalizedDetail.Length;
    }
}