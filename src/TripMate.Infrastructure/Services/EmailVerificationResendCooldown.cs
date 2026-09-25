using System.Collections.Concurrent;
using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services;

public sealed class EmailVerificationResendCooldown : IEmailVerificationResendCooldown
{
    private readonly ConcurrentDictionary<long, DateTimeOffset> nextAllowed = new();

    public bool TryAcquire(long userId, DateTimeOffset now, TimeSpan cooldown)
    {
        while (true)
        {
            if (!nextAllowed.TryGetValue(userId, out var existing))
            {
                if (nextAllowed.TryAdd(userId, now.Add(cooldown))) return true;
                continue;
            }

            if (existing > now) return false;
            if (nextAllowed.TryUpdate(userId, now.Add(cooldown), existing)) return true;
        }
    }
}
