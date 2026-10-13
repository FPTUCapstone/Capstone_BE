using Microsoft.Extensions.Options;

namespace TripMate.Application.Features.Navigation.Common;

public sealed class NavigationSessionOptions
{
    public const string SectionName = "NavigationSession";

    /// <summary>How long before the first planned arrival a session may start.</summary>
    public TimeSpan EarlyStartWindow { get; init; } = TimeSpan.FromHours(3);

    /// <summary>How long after the last planned departure an open session expires.</summary>
    public TimeSpan ExpiryGracePeriod { get; init; } = TimeSpan.FromHours(6);

    /// <summary>How far ahead of the server clock a device-reported event time is still accepted.</summary>
    public TimeSpan ClientClockSkewTolerance { get; init; } = TimeSpan.FromMinutes(2);
}

public sealed class NavigationSessionOptionsValidator : IValidateOptions<NavigationSessionOptions>
{
    public ValidateOptionsResult Validate(string? name, NavigationSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.EarlyStartWindow <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.EarlyStartWindow)} must be greater than zero.");
        }

        if (options.ExpiryGracePeriod <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.ExpiryGracePeriod)} must be greater than zero.");
        }

        if (options.ClientClockSkewTolerance < TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.ClientClockSkewTolerance)} cannot be negative.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}