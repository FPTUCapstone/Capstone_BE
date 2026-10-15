using FluentValidation;

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
    }
}