namespace TripMate.Infrastructure.Services;

internal static class TourMediaCleanupRetryPolicy
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromHours(24);

    public static TimeSpan GetDelayForAttempt(int attemptCount)
    {
        if (attemptCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptCount));
        }

        if (attemptCount >= 12)
        {
            return MaximumDelay;
        }

        var exponent = attemptCount - 1;
        return TimeSpan.FromTicks(InitialDelay.Ticks * (1L << exponent));
    }
}