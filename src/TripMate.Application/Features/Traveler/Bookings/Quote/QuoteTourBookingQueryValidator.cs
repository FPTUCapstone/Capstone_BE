using FluentValidation;

namespace TripMate.Application.Features.Traveler.Bookings.Quote;

public sealed class QuoteTourBookingQueryValidator : AbstractValidator<QuoteTourBookingQuery>
{
    public QuoteTourBookingQueryValidator()
    {
        RuleFor(query => query.TourScheduleId).NotEmpty();
        RuleFor(query => query.ParticipantCounts).NotNull();
        When(query => query.ParticipantCounts is not null, () =>
        {
            RuleFor(query => query.ParticipantCounts.Adult).GreaterThanOrEqualTo(0);
            RuleFor(query => query.ParticipantCounts.Child).GreaterThanOrEqualTo(0);
            RuleFor(query => query.ParticipantCounts.Infant).GreaterThanOrEqualTo(0);
            RuleFor(query => query.ParticipantCounts.Total).GreaterThan(0)
                .WithMessage("At least one participant is required.");
        });
        RuleFor(query => query.VoucherCode).MaximumLength(30);
    }
}