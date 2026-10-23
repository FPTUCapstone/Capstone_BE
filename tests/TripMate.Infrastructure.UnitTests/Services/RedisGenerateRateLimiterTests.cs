using FluentAssertions;

using StackExchange.Redis;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Infrastructure.Services;
using TripMate.Infrastructure.Services.Redis;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class RedisGenerateRateLimiterTests
{
    [Fact]
    public void BuildKeys_SharesConfiguredClusterHashTag()
    {
        const string prefix = "tripmate:scheduling:rate-limit";
        var (userKey, registryKey) = RedisRateLimitLuaScript.BuildKeys(prefix, 12345);

        userKey.Should().Be("{tripmate:scheduling:rate-limit}:user:12345");
        registryKey.Should().Be("{tripmate:scheduling:rate-limit}:registry");

        // Verify Redis Cluster hash tag convention: substring between first '{' and '}'
        int userTagStart = userKey.IndexOf('{');
        int userTagEnd = userKey.IndexOf('}');
        string userTag = userKey.Substring(userTagStart, userTagEnd - userTagStart + 1);

        int registryTagStart = registryKey.IndexOf('{');
        int registryTagEnd = registryKey.IndexOf('}');
        string registryTag = registryKey.Substring(registryTagStart, registryTagEnd - registryTagStart + 1);

        userTag.Should().Be(registryTag);
        userTag.Should().Be("{tripmate:scheduling:rate-limit}");
    }

    [Theory]
    [InlineData(100, 1)]
    [InlineData(999, 1)]
    [InlineData(1000, 1)]
    [InlineData(1001, 2)]
    [InlineData(14200, 15)]
    [InlineData(60000, 60)]
    [InlineData(75000, 60)] // clamped to 60
    public void ParseRetryAfterSeconds_RoundsUpToPositiveCeilingSeconds(long ms, int expectedSeconds)
    {
        RedisRateLimitLuaScript.ParseRetryAfterSeconds(ms).Should().Be(expectedSeconds);
    }

    [Fact]
    public void ParseDecision_MapsStatus1_ToAllowed()
    {
        var rawResult = RedisResult.Create(new RedisResult[]
        {
            RedisResult.Create(1),
            RedisResult.Create(0),
            RedisResult.Create(5),
            RedisResult.Create(0)
        });

        var decision = RedisGenerateRateLimiter.ParseDecision(rawResult);

        decision.Allowed.Should().BeTrue();
        decision.ErrorCode.Should().BeNull();
        decision.RetryAfterSeconds.Should().Be(0);
    }

    [Fact]
    public void ParseDecision_MapsStatus2_ToCooldownWithRetryAfter()
    {
        var rawResult = RedisResult.Create(new RedisResult[]
        {
            RedisResult.Create(2),
            RedisResult.Create(14200),
            RedisResult.Create(3),
            RedisResult.Create(1)
        });

        var decision = RedisGenerateRateLimiter.ParseDecision(rawResult);

        decision.Allowed.Should().BeFalse();
        decision.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationCooldown);
        decision.RetryAfterSeconds.Should().Be(15);
    }

    [Fact]
    public void ParseDecision_MapsStatus3_ToQuotaWithRetryAfter()
    {
        var rawResult = RedisResult.Create(new RedisResult[]
        {
            RedisResult.Create(3),
            RedisResult.Create(45000),
            RedisResult.Create(2),
            RedisResult.Create(0)
        });

        var decision = RedisGenerateRateLimiter.ParseDecision(rawResult);

        decision.Allowed.Should().BeFalse();
        decision.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationRateLimited);
        decision.RetryAfterSeconds.Should().Be(45);
    }

    [Fact]
    public void ParseDecision_MapsNullOrMalformed_ToRateLimiterUnavailable()
    {
        var decisionNull = RedisGenerateRateLimiter.ParseDecision(null);
        decisionNull.Allowed.Should().BeFalse();
        decisionNull.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationRateLimiterUnavailable);

        var malformed = RedisResult.Create(new RedisResult[] { RedisResult.Create(99) });
        var decisionMalformed = RedisGenerateRateLimiter.ParseDecision(malformed);
        decisionMalformed.Allowed.Should().BeFalse();
        decisionMalformed.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationRateLimiterUnavailable);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenRedisThrowsException_FailsClosedWithRateLimiterUnavailable()
    {
        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            KeyPrefix = "tripmate:scheduling:rate-limit",
        };

        var limiter = new RedisGenerateRateLimiter(null!, options)
        {
            ScriptEvaluator = (_, _) => Task.FromException<RedisResult>(
                new TimeoutException("Redis command timed out")),
        };

        var decision = await limiter.TryAcquireAsync(42, CancellationToken.None);

        decision.Allowed.Should().BeFalse();
        decision.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationRateLimiterUnavailable);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenCancellationRequested_PropagatesOperationCanceledException()
    {
        var options = new SchedulingRateLimitOptions();
        var limiter = new RedisGenerateRateLimiter(null!, options);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var action = () => limiter.TryAcquireAsync(42, cts.Token).AsTask();

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenCancelledDuringRedisCommand_PropagatesOperationCanceledException()
    {
        var limiter = new RedisGenerateRateLimiter(null!, new SchedulingRateLimitOptions
        {
            CommandTimeout = TimeSpan.FromSeconds(5),
        })
        {
            ScriptEvaluator = (_, _) => new TaskCompletionSource<RedisResult>().Task,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        var action = () => limiter.TryAcquireAsync(42, cts.Token).AsTask();

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenCommandExceedsConfiguredTimeout_FailsClosed()
    {
        var limiter = new RedisGenerateRateLimiter(null!, new SchedulingRateLimitOptions
        {
            CommandTimeout = TimeSpan.FromMilliseconds(25),
        })
        {
            ScriptEvaluator = (_, _) => new TaskCompletionSource<RedisResult>().Task,
        };

        var decision = await limiter.TryAcquireAsync(42, CancellationToken.None);

        decision.Allowed.Should().BeFalse();
        decision.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationRateLimiterUnavailable);
    }

    [Fact]
    public async Task TryAcquireAsync_RoundsUserTtlUpSoSafetyMarginIsNeverTruncated()
    {
        RedisValue[]? capturedValues = null;
        var limiter = new RedisGenerateRateLimiter(null!, new SchedulingRateLimitOptions
        {
            TtlSafetyMargin = TimeSpan.FromMilliseconds(500),
        })
        {
            ScriptEvaluator = (_, values) =>
            {
                capturedValues = values;
                return Task.FromResult(RedisResult.Create(new RedisResult[]
                {
                    RedisResult.Create(1),
                    RedisResult.Create(0),
                    RedisResult.Create(1),
                    RedisResult.Create(0),
                }));
            },
        };

        await limiter.TryAcquireAsync(42, CancellationToken.None);

        ((long)capturedValues![3]).Should().Be(61);
    }
}