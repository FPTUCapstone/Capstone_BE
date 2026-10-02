using TripMate.Application.Common.Interfaces;
using TripMate.Infrastructure.AiExplanation;
using TripMate.Infrastructure.AiRanking;

namespace TripMate.Infrastructure.Ai;

/// <summary>
/// Process-local safety gate. It intentionally does not claim to coordinate replicas.
/// </summary>
public sealed class InMemoryAiProviderBudget(
    IDateTimeProvider clock,
    AiProviderBudgetSettings ranking,
    AiProviderBudgetSettings explanation) : IAiProviderBudget
{
    private readonly ProviderWindow _ranking = new(ranking, clock);
    private readonly ProviderWindow _explanation = new(explanation, clock);

    public bool TryAcquire(string provider, out IDisposable? permit)
    {
        ProviderWindow window = provider switch
        {
            AiProviderNames.Ranking => _ranking,
            AiProviderNames.Explanation => _explanation,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
        return window.TryAcquire(out permit);
    }

    private sealed class ProviderWindow(AiProviderBudgetSettings settings, IDateTimeProvider clock)
    {
        private readonly object _sync = new();
        private readonly Queue<DateTimeOffset> _permits = new();
        private readonly SemaphoreSlim _concurrency = new(settings.MaxConcurrency, settings.MaxConcurrency);
        private readonly AiProviderBudgetSettings _settings = settings;
        private readonly IDateTimeProvider _clock = clock;

        public bool TryAcquire(out IDisposable? permit)
        {
            permit = null;
            if (!_concurrency.Wait(0))
            {
                return false;
            }

            lock (_sync)
            {
                DateTimeOffset now = _clock.UtcNow;
                while (_permits.Count > 0 && now - _permits.Peek() >= TimeSpan.FromMinutes(1))
                {
                    _permits.Dequeue();
                }

                if (_permits.Count >= _settings.RateLimitPerMinute)
                {
                    _concurrency.Release();
                    return false;
                }

                _permits.Enqueue(now);
                permit = new Permit(_concurrency);
                return true;
            }
        }
    }

    private sealed class Permit(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}

public static class AiProviderNames
{
    public const string Ranking = "ranking";
    public const string Explanation = "explanation";
}

public sealed record AiProviderBudgetSettings(int RateLimitPerMinute, int MaxConcurrency);
