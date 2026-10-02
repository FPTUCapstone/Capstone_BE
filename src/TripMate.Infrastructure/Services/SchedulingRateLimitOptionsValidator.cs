using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Infrastructure.Services;

public sealed class SchedulingRateLimitOptionsValidator
    : IValidateOptions<SchedulingRateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, SchedulingRateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.MaxFreshGenerationsPerMinute is < 1 or > 1000)
        {
            failures.Add($"{nameof(options.MaxFreshGenerationsPerMinute)} must be between 1 and 1000.");
        }

        if (options.CooldownSeconds is < 0 or >= 60)
        {
            failures.Add($"{nameof(options.CooldownSeconds)} must be between 0 and 59.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
