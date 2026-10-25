using FluentValidation;

namespace TripMate.Application.Features.TripAlerts.PushDevices;

public sealed class RegisterPushDeviceCommandValidator : AbstractValidator<RegisterPushDeviceCommand>
{
    public RegisterPushDeviceCommandValidator()
    {
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
        RuleFor(command => command.InstallationId).NotEqual(Guid.Empty);
        RuleFor(command => command.Token).NotEmpty().MaximumLength(1500);
    }
}