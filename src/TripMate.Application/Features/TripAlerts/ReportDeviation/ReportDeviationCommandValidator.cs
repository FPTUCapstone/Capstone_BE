using FluentValidation;

namespace TripMate.Application.Features.TripAlerts.ReportDeviation;

public sealed class ReportDeviationCommandValidator : AbstractValidator<ReportDeviationCommand>
{
    public ReportDeviationCommandValidator()
    {
        RuleFor(command => command.SessionId).GreaterThan(0);
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
        RuleFor(command => command.EventId).NotEqual(Guid.Empty);
        RuleFor(command => command.TargetItemId).GreaterThan(0);
        RuleFor(command => command.ObservedAtUtc.Offset).Equal(TimeSpan.Zero)
            .WithMessage("observedAtUtc must be UTC.");
    }
}