using System.Diagnostics;
using System.Globalization;

using Microsoft.Extensions.Logging;

using StackExchange.Redis;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Infrastructure.Services.Redis;

public sealed class RedisGenerateRateLimiter(
    IConnectionMultiplexer connectionMultiplexer,
    SchedulingRateLimitOptions options,
    GenerateRateLimiterMetrics? metrics = null,
    ILogger<RedisGenerateRateLimiter>? logger = null) : IGenerateRateLimiter
{
    private readonly IConnectionMultiplexer _multiplexer = connectionMultiplexer;
    private readonly SchedulingRateLimitOptions _options = options;
    private readonly GenerateRateLimiterMetrics? _metrics = metrics;
    private readonly ILogger<RedisGenerateRateLimiter>? _logger = logger;
    private long _lastStoreFailureLogTicks;

    internal Func<RedisKey[], RedisValue[], Task<RedisResult>>? ScriptEvaluator { get; init; }

    public async ValueTask<GenerateRateLimitDecision> TryAcquireAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var (userKey, registryKey) = RedisRateLimitLuaScript.BuildKeys(_options.KeyPrefix, userId);

            var maxPermits = _options.MaxFreshGenerationsPerMinute;
            var cooldownMs = (long)TimeSpan.FromSeconds(_options.CooldownSeconds).TotalMilliseconds;
            var windowMs = 60_000L;
            var ttlSeconds = (long)Math.Ceiling(
                (TimeSpan.FromMinutes(1) + _options.TtlSafetyMargin).TotalSeconds);
            var uniqueToken = Guid.NewGuid().ToString("N");

            RedisKey[] keys = [userKey, registryKey];
            RedisValue[] values =
            [
                maxPermits,
                cooldownMs,
                windowMs,
                ttlSeconds,
                userId.ToString(CultureInfo.InvariantCulture),
                uniqueToken,
            ];

            Task<RedisResult> evaluation = ScriptEvaluator is not null
                ? ScriptEvaluator(keys, values)
                : _multiplexer.GetDatabase().ScriptEvaluateAsync(
                    RedisRateLimitLuaScript.Script,
                    keys,
                    values);
            RedisResult rawResult = await evaluation.WaitAsync(
                _options.CommandTimeout,
                cancellationToken);

            stopwatch.Stop();
            ParsedRedisRateLimitResult parsed = ParseResult(rawResult);
            var decision = parsed.Decision;

            if (parsed.IsValid)
            {
                _metrics?.RecordActiveUsers(
                    "redis",
                    parsed.ActiveUsers,
                    TimeSpan.FromMinutes(1) + _options.TtlSafetyMargin);
                _metrics?.RecordEvictions("redis", parsed.ExpiredUsers);
            }

            string outcome = decision.Allowed
                ? "accepted"
                : decision.ErrorCode switch
                {
                    SchedulingErrorCodes.GenerationCooldown => "cooldown",
                    SchedulingErrorCodes.GenerationRateLimited => "quota",
                    _ => "store_failure",
                };

            _metrics?.RecordAcquisition("redis", outcome);
            _metrics?.RecordDuration("redis", outcome == "store_failure" ? "failure" : "success", stopwatch.Elapsed.TotalMilliseconds);

            return decision;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics?.RecordAcquisition("redis", "store_failure");
            _metrics?.RecordDuration("redis", "failure", stopwatch.Elapsed.TotalMilliseconds);

            long nowTicks = Environment.TickCount64;
            long lastTicks = Interlocked.Read(ref _lastStoreFailureLogTicks);
            if (nowTicks - lastTicks > 5000)
            {
                if (Interlocked.CompareExchange(ref _lastStoreFailureLogTicks, nowTicks, lastTicks) == lastTicks)
                {
                    _logger?.LogWarning(
                        ex,
                        "Redis rate limiter store failure; failing closed with 503.");
                }
            }

            return new GenerateRateLimitDecision(
                Allowed: false,
                ErrorCode: SchedulingErrorCodes.GenerationRateLimiterUnavailable);
        }
    }

    internal static GenerateRateLimitDecision ParseDecision(RedisResult? rawResult)
        => ParseResult(rawResult).Decision;

    private static ParsedRedisRateLimitResult ParseResult(RedisResult? rawResult)
    {
        if (rawResult is null || rawResult.IsNull)
        {
            return InvalidResult();
        }

        if ((RedisResult[]?)rawResult is not { Length: >= 4 } array)
        {
            return InvalidResult();
        }

        try
        {
            var status = (int)array[0];
            var remainingMs = (long)array[1];
            var activeUsers = (int)array[2];
            var expiredUsers = (long)array[3];
            if (remainingMs < 0 || activeUsers < 0 || expiredUsers < 0)
            {
                return InvalidResult();
            }

            GenerateRateLimitDecision decision = status switch
            {
                1 => new GenerateRateLimitDecision(Allowed: true),
                2 => new GenerateRateLimitDecision(
                    Allowed: false,
                    ErrorCode: SchedulingErrorCodes.GenerationCooldown,
                    RetryAfterSeconds: RedisRateLimitLuaScript.ParseRetryAfterSeconds(remainingMs)),
                3 => new GenerateRateLimitDecision(
                    Allowed: false,
                    ErrorCode: SchedulingErrorCodes.GenerationRateLimited,
                    RetryAfterSeconds: RedisRateLimitLuaScript.ParseRetryAfterSeconds(remainingMs)),
                _ => new GenerateRateLimitDecision(
                    Allowed: false,
                    ErrorCode: SchedulingErrorCodes.GenerationRateLimiterUnavailable),
            };

            return new ParsedRedisRateLimitResult(
                decision,
                activeUsers,
                expiredUsers,
                IsValid: status is >= 1 and <= 3);
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException)
        {
            return InvalidResult();
        }
    }

    private static ParsedRedisRateLimitResult InvalidResult() =>
        new(
            new GenerateRateLimitDecision(
                Allowed: false,
                ErrorCode: SchedulingErrorCodes.GenerationRateLimiterUnavailable),
            ActiveUsers: 0,
            ExpiredUsers: 0,
            IsValid: false);

    private readonly record struct ParsedRedisRateLimitResult(
        GenerateRateLimitDecision Decision,
        int ActiveUsers,
        long ExpiredUsers,
        bool IsValid);
}