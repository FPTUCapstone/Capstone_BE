using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.Features.Scheduling.Common;

public sealed class SchedulingReservationOptions
{
    public const string SectionName = "SchedulingReservation";

    public static readonly TimeSpan RouteProviderTimeout = TimeSpan.FromSeconds(20);

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}

public sealed class SchedulingReservationOptionsValidator(
    IOptions<PersonalizationRankingOptions> personalizationOptions)
    : IValidateOptions<SchedulingReservationOptions>
{
    private readonly PersonalizationRankingOptions _personalizationOptions =
        personalizationOptions?.Value
        ?? throw new ArgumentNullException(nameof(personalizationOptions));

    public ValidateOptionsResult Validate(string? name, SchedulingReservationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.LeaseDuration <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.LeaseDuration)} must be greater than zero.");
        }

        TimeSpan minimumLeaseDuration;
        try
        {
            minimumLeaseDuration = checked(
                _personalizationOptions.ProviderTimeout
                + SchedulingReservationOptions.RouteProviderTimeout);
        }
        catch (OverflowException)
        {
            failures.Add(
                $"{nameof(PersonalizationRankingOptions.ProviderTimeout)} is too large to "
                + "calculate a safe generation lease.");
            minimumLeaseDuration = TimeSpan.MaxValue;
        }

        if (options.LeaseDuration <= minimumLeaseDuration)
        {
            failures.Add(
                $"{nameof(options.LeaseDuration)} must be longer than the configured ranking "
                + "timeout plus the route-provider timeout.");
        }

        if (options.PollInterval <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.PollInterval)} must be greater than zero.");
        }
        else if (options.PollInterval >= options.LeaseDuration)
        {
            failures.Add(
                $"{nameof(options.PollInterval)} must be shorter than "
                + $"{nameof(options.LeaseDuration)}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}