using FluentValidation;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.ServiceProviderRequests;

public sealed class ListServiceProviderRequestsValidator : AbstractValidator<ListServiceProviderRequests>
{
    public ListServiceProviderRequestsValidator()
    {
        RuleFor(query => query.PageNumber).InclusiveBetween(1, 1_000_000);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
        RuleFor(query => query.Keyword).MaximumLength(200);
        RuleFor(query => query.Status).Must(status => status is null or ServiceBooking.PendingConfirmation
            or ServiceBooking.Confirmed or ServiceBooking.Rejected or ServiceBooking.Expired
            or ServiceBooking.Completed or ServiceBooking.NoShow or ServiceBooking.Cancelled);
        RuleFor(query => query).Must(query => !query.RequestedDateFrom.HasValue || !query.RequestedDateTo.HasValue
            || query.RequestedDateFrom <= query.RequestedDateTo);
        RuleFor(query => query.RequestedDateTo).Must(day => day is null || day < DateOnly.MaxValue);
    }
}

public sealed class DecideServiceProviderRequestValidator : AbstractValidator<DecideServiceProviderRequest>
{
    public DecideServiceProviderRequestValidator()
    {
        RuleFor(command => command.Id).GreaterThan(0);
        RuleFor(command => command.IdempotencyKey).NotEmpty();
        RuleFor(command => command.RowVersion).Must(ProviderRequestValidation.IsVersion);
        RuleFor(command => command.Decision).Must(decision => decision is ServiceBooking.Confirmed or ServiceBooking.Rejected);
        RuleFor(command => command.Reason).MaximumLength(ServiceBooking.RejectionReasonMaxLength);
        RuleFor(command => command.Reason).NotEmpty().When(command => command.Decision == ServiceBooking.Rejected);
    }
}

public sealed class RecordServiceProviderOutcomeValidator : AbstractValidator<RecordServiceProviderOutcome>
{
    public RecordServiceProviderOutcomeValidator()
    {
        RuleFor(command => command.Id).GreaterThan(0);
        RuleFor(command => command.IdempotencyKey).NotEmpty();
        RuleFor(command => command.RowVersion).Must(ProviderRequestValidation.IsVersion);
        RuleFor(command => command.Outcome).Must(outcome => outcome is ServiceBooking.Completed or ServiceBooking.NoShow);
        RuleFor(command => command.ActualAmount).Must(amount => amount > 0 && amount <= ServiceBooking.MaximumAmount
                && decimal.Round(amount.Value, 2) == amount)
            .When(command => command.Outcome == ServiceBooking.Completed);
        RuleFor(command => command.ActualAmount).Must(amount => amount is null or 0)
            .When(command => command.Outcome == ServiceBooking.NoShow);
    }
}

internal static class ProviderRequestValidation
{
    public static bool IsVersion(string? version) => version is not null
        && Convert.TryFromBase64String(version, new byte[8], out var written) && written == 8;
}