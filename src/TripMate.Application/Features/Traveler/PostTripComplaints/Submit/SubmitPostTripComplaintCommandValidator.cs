using FluentValidation;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Traveler.PostTripComplaints.Submit;

public sealed class SubmitPostTripComplaintCommandValidator : AbstractValidator<SubmitPostTripComplaintCommand>
{
    public SubmitPostTripComplaintCommandValidator()
    {
        RuleFor(command => command.BookingId).GreaterThan(0);
        RuleFor(command => command.IdempotencyKey).NotEmpty();
        RuleFor(command => command.ReasonDetail).Must(value => !string.IsNullOrWhiteSpace(value))
            .MaximumLength(PostTripComplaint.ReasonDetailMaxLength);
        RuleFor(command => command.Evidence).NotNull().Must(items => items.Count <= 5);
    }
}