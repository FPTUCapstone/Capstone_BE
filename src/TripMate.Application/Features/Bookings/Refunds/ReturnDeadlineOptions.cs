using Microsoft.Extensions.Options;

namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>BR-144 (SRS 3.8.4.3, spec v1.2): how often <see cref="ReturnDeadlineProcessor"/> scans
/// for refunds whose Tour Operator return deadline has passed without the full share received.</summary>
public sealed class ReturnDeadlineOptions
{
    public const string SectionName = "ReturnDeadline";
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromMinutes(15);
    public int BatchSize { get; init; } = 50;
}

public sealed class ReturnDeadlineOptionsValidator : IValidateOptions<ReturnDeadlineOptions>
{
    public ValidateOptionsResult Validate(string? name, ReturnDeadlineOptions options)
    {
        if (options.ScanInterval <= TimeSpan.Zero || options.BatchSize <= 0)
            return ValidateOptionsResult.Fail("ReturnDeadline options must be positive.");
        return ValidateOptionsResult.Success;
    }
}