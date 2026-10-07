using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Infrastructure.Services;

public sealed class SchedulingRateLimitOptionsValidator(
    IConfiguration? configuration = null)
    : IValidateOptions<SchedulingRateLimitOptions>
{
    private readonly IConfiguration? _configuration = configuration;

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

        if (!Enum.IsDefined(options.Provider))
        {
            failures.Add($"{nameof(options.Provider)} is not a supported rate limit provider.");
            return ValidateOptionsResult.Fail(failures);
        }

        if (_configuration is not null
            && string.IsNullOrWhiteSpace(_configuration[
                $"{SchedulingRateLimitOptions.SectionName}:{nameof(options.Provider)}"]))
        {
            failures.Add($"{nameof(options.Provider)} must be configured explicitly.");
        }

        switch (options.Provider)
        {
            case SchedulingRateLimitProvider.Redis:
                if (string.IsNullOrWhiteSpace(options.RedisConnectionStringName))
                {
                    failures.Add($"{nameof(options.RedisConnectionStringName)} must not be empty.");
                }
                else if (_configuration is not null)
                {
                    var connectionString = _configuration.GetConnectionString(options.RedisConnectionStringName);
                    if (string.IsNullOrWhiteSpace(connectionString))
                    {
                        failures.Add($"Connection string '{options.RedisConnectionStringName}' is missing or empty.");
                    }
                }

                if (string.IsNullOrWhiteSpace(options.KeyPrefix))
                {
                    failures.Add($"{nameof(options.KeyPrefix)} must not be empty.");
                }

                if (options.CommandTimeout <= TimeSpan.Zero)
                {
                    failures.Add($"{nameof(options.CommandTimeout)} must be greater than zero.");
                }

                if (options.TtlSafetyMargin < TimeSpan.Zero)
                {
                    failures.Add($"{nameof(options.TtlSafetyMargin)} must not be negative.");
                }
                break;

            case SchedulingRateLimitProvider.SingleInstance:
                if (options.IdleTtl < TimeSpan.FromSeconds(60))
                {
                    failures.Add($"{nameof(options.IdleTtl)} must be at least 60 seconds to cover the rolling window.");
                }

                if (options.CleanupCadence <= TimeSpan.Zero)
                {
                    failures.Add($"{nameof(options.CleanupCadence)} must be greater than zero.");
                }

                if (options.MaxTrackedUsers <= 0)
                {
                    failures.Add($"{nameof(options.MaxTrackedUsers)} must be greater than zero.");
                }
                break;
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}