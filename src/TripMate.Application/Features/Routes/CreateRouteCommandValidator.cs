using FluentValidation;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Routes;

public sealed class CreateRouteCommandValidator : AbstractValidator<CreateRouteCommand>
{
    public CreateRouteCommandValidator()
    {
        RuleFor(command => command.FromPoiId).NotNull().WithMessage(RouteErrors.Required).WithErrorCode(RouteErrors.Msg01);
        RuleFor(command => command.ToPoiId).NotNull().WithMessage(RouteErrors.Required).WithErrorCode(RouteErrors.Msg01);
        RuleFor(command => command.DistanceKm).NotNull().WithMessage(RouteErrors.Required).WithErrorCode(RouteErrors.Msg01);
        RuleFor(command => command.EstimatedDurationMinutes).NotNull().WithMessage(RouteErrors.Required).WithErrorCode(RouteErrors.Msg01);
        RuleFor(command => command.TransportMode).NotEmpty().WithMessage(RouteErrors.Required).WithErrorCode(RouteErrors.Msg01);
        RuleFor(command => command.IdempotencyKey).NotEqual(Guid.Empty).WithMessage(RouteErrors.Required).WithErrorCode(RouteErrors.Msg01);
        RuleFor(command => command.FromPoiId).GreaterThan(0).When(command => command.FromPoiId.HasValue)
            .WithMessage(RouteErrors.InvalidEndpoints).WithErrorCode(RouteErrors.Msg141);
        RuleFor(command => command.ToPoiId).GreaterThan(0).When(command => command.ToPoiId.HasValue)
            .WithMessage(RouteErrors.InvalidEndpoints).WithErrorCode(RouteErrors.Msg141);
        RuleFor(command => command.ToPoiId).NotEqual(command => command.FromPoiId)
            .When(command => command.FromPoiId.HasValue && command.ToPoiId.HasValue)
            .WithMessage(RouteErrors.InvalidEndpoints).WithErrorCode(RouteErrors.Msg141);
        RuleFor(command => command.DistanceKm).GreaterThan(0).When(command => command.DistanceKm.HasValue)
            .WithMessage(RouteErrors.InvalidNumbers).WithErrorCode(RouteErrors.Msg143);
        RuleFor(command => command.EstimatedDurationMinutes).GreaterThan(0).When(command => command.EstimatedDurationMinutes.HasValue)
            .WithMessage(RouteErrors.InvalidNumbers).WithErrorCode(RouteErrors.Msg143);
        RuleFor(command => command.DistanceKm)
            .Must(value => value <= RouteSegment.MaximumDistanceKm && decimal.Round(value!.Value, 2) == value)
            .When(command => command.DistanceKm > 0)
            .WithMessage($"Distance must fit the supported range up to {RouteSegment.MaximumDistanceKm} km with at most two decimal places.")
            .WithErrorCode(RouteErrors.InvalidInput);
        RuleFor(command => command.TransportMode)
            .Must(value => Enum.GetNames<TransportMode>().Contains(value, StringComparer.Ordinal))
            .When(command => !string.IsNullOrWhiteSpace(command.TransportMode))
            .WithMessage("Select a supported transport mode.").WithErrorCode(RouteErrors.InvalidInput);
        RuleFor(command => command.RouteNote).Must(value => value is null || value.Trim().Length <= RouteSegment.NoteMaxLength)
            .WithMessage($"Route note must contain at most {RouteSegment.NoteMaxLength} characters.").WithErrorCode(RouteErrors.InvalidInput);
    }
}