using System.Globalization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services;

public class DateTimeProvider : IDateTimeProvider
{
    private const string LocalOverrideConfigurationKey = "TRIPMATE_CLOCK_OVERRIDE_UTC";

    private readonly DateTimeOffset? localOverrideUtc;

    public DateTimeProvider() : this(localOverrideUtc: null) { }

    public DateTimeProvider(IHostEnvironment environment, IConfiguration configuration)
        : this(environment.IsDevelopment() ? configuration[LocalOverrideConfigurationKey] : null) { }

    public DateTimeProvider(string? localOverrideUtc)
    {
        if (string.IsNullOrWhiteSpace(localOverrideUtc))
            return;

        if (!DateTimeOffset.TryParse(localOverrideUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            throw new InvalidOperationException($"{LocalOverrideConfigurationKey} must be a valid UTC timestamp.");

        this.localOverrideUtc = parsed.ToUniversalTime();
    }

    public DateTimeOffset UtcNow => localOverrideUtc ?? DateTimeOffset.UtcNow;
}