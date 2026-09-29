using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services;

/// <summary>
/// Pragmatic enumeration-timing normalization: every password-reset request response is
/// held until a fixed minimum duration has elapsed since the request started, closing the
/// fast-path oracle between unknown/ineligible accounts and the full SMTP path. Uses the
/// application clock (never Thread.Sleep); the delay is cancelled with the request.
/// </summary>
public sealed class ResponseTimingNormalizer(IDateTimeProvider dateTimeProvider) : IRequestTimingNormalizer
{
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(500);

    public async Task EnsureMinimumDurationAsync(DateTimeOffset startedAtUtc, CancellationToken cancellationToken)
    {
        var remaining = MinimumDuration - (dateTimeProvider.UtcNow - startedAtUtc);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }
}