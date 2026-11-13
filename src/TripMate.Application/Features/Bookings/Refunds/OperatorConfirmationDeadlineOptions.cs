using Microsoft.Extensions.Options;

namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>BR-187 (SRS 3.8.4.3, spec v1.2): how often <see cref="OperatorConfirmationDeadlineProcessor"/>
/// scans for refunds the Tour Operator has not confirmed within 3 working days.</summary>
public sealed class OperatorConfirmationDeadlineOptions
{
    public const string SectionName = "OperatorConfirmationDeadline";
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromMinutes(15);
    public int BatchSize { get; init; } = 50;
}

public sealed class OperatorConfirmationDeadlineOptionsValidator : IValidateOptions<OperatorConfirmationDeadlineOptions>
{
    public ValidateOptionsResult Validate(string? name, OperatorConfirmationDeadlineOptions options)
    {
        if (options.ScanInterval <= TimeSpan.Zero || options.BatchSize <= 0)
            return ValidateOptionsResult.Fail("OperatorConfirmationDeadline options must be positive.");
        return ValidateOptionsResult.Success;
    }
}