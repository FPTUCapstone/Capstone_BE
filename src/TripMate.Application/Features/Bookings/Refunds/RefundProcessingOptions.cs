using Microsoft.Extensions.Options;

namespace TripMate.Application.Features.Bookings.Refunds;

public sealed class RefundProcessingOptions
{
    public const string SectionName = "RefundProcessing";
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ProcessingLeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
    public int BatchSize { get; init; } = 50;
    public int MaxAttempts { get; init; } = 3;
}

public sealed class RefundProcessingOptionsValidator : IValidateOptions<RefundProcessingOptions>
{
    public ValidateOptionsResult Validate(string? name, RefundProcessingOptions options)
    {
        if (options.SweepInterval <= TimeSpan.Zero || options.RetryDelay <= TimeSpan.Zero ||
            options.ProcessingLeaseDuration <= TimeSpan.Zero ||
            options.BatchSize <= 0 || options.MaxAttempts <= 0)
            return ValidateOptionsResult.Fail("RefundProcessing options must be positive.");
        return ValidateOptionsResult.Success;
    }
}