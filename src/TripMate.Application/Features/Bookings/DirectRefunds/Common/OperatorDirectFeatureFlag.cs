using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Application.Features.Bookings.DirectRefunds.Common;

/// <summary>
/// Feature flag: OperatorDirect.Enabled = "true" (case-insensitive) in SystemConfigs enables
/// the new direct-payment / direct-refund flow. When absent or false, the OperatorDirect payment
/// flow is blocked and new bookings fall back to PlatformCollectedLegacy.
///
/// Coexistence rule: existing OperatorDirect bookings continue to be processed regardless of this
/// flag; it only gates *new* channel registrations and *new* bookings.
/// </summary>
public static class OperatorDirectFeatureFlag
{
    public const string ConfigKey = "OperatorDirect.Enabled";

    public static async Task<bool> IsEnabledAsync(
        IApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        string? raw = await dbContext.SystemConfigs
            .AsNoTracking()
            .Where(c => c.ConfigKey == ConfigKey)
            .Select(c => c.ConfigValue)
            .SingleOrDefaultAsync(cancellationToken);

        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }
}
