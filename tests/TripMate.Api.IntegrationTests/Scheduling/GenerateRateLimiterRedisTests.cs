using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using StackExchange.Redis;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Infrastructure.Services;
using TripMate.Infrastructure.Services.Redis;

namespace TripMate.Api.IntegrationTests.Scheduling;

[Trait("Category", "Redis")]
public sealed class GenerateRateLimiterRedisTests
{
    [RedisFact]
    public async Task TwoIndependentInstances_SharingRedis_EnforceSharedRollingQuota()
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        string keyPrefix = fixture.CreateUniquePrefix();

        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            KeyPrefix = keyPrefix,
            MaxFreshGenerationsPerMinute = 3,
            CooldownSeconds = 0, // Disable cooldown to isolate quota check
        };

        IConnectionMultiplexer multiplexer1 = await fixture.CreateIndependentMultiplexerAsync();
        IConnectionMultiplexer multiplexer2 = await fixture.CreateIndependentMultiplexerAsync();
        await using ServiceProvider provider1 = BuildIndependentProvider(multiplexer1, options);
        await using ServiceProvider provider2 = BuildIndependentProvider(multiplexer2, options);
        var instance1 = provider1.GetRequiredService<IGenerateRateLimiter>();
        var instance2 = provider2.GetRequiredService<IGenerateRateLimiter>();
        long userId = 1001;

        // Instance 1 consumes 2 permits
        (await instance1.TryAcquireAsync(userId, CancellationToken.None)).Allowed.Should().BeTrue();
        (await instance1.TryAcquireAsync(userId, CancellationToken.None)).Allowed.Should().BeTrue();

        // Instance 2 consumes 1 permit (total = 3, at capacity)
        (await instance2.TryAcquireAsync(userId, CancellationToken.None)).Allowed.Should().BeTrue();

        // 4th request on Instance 1 is denied
        var result1 = await instance1.TryAcquireAsync(userId, CancellationToken.None);
        result1.Allowed.Should().BeFalse();
        result1.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationRateLimited);

        // 5th request on Instance 2 is also denied
        var result2 = await instance2.TryAcquireAsync(userId, CancellationToken.None);
        result2.Allowed.Should().BeFalse();
        result2.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationRateLimited);
    }

    [RedisFact]
    public async Task SimultaneousSameMillisecondCalls_GrantExactlyOnePermitDuringCooldown()
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        string keyPrefix = fixture.CreateUniquePrefix();

        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            KeyPrefix = keyPrefix,
            MaxFreshGenerationsPerMinute = 5,
            CooldownSeconds = 15,
        };

        var limiter = new RedisGenerateRateLimiter(fixture.Multiplexer, options);
        long userId = 2002;

        // Fire 10 concurrent requests simultaneously
        var tasks = Enumerable.Range(0, 10).Select(_ =>
            limiter.TryAcquireAsync(userId, CancellationToken.None).AsTask());

        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Allowed).Should().Be(1);
        results.Count(r => !r.Allowed && r.ErrorCode == SchedulingErrorCodes.GenerationCooldown).Should().Be(9);
    }

    [RedisFact]
    public async Task RejectedCall_DoesNotExtendUserKeyTtl()
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        string keyPrefix = fixture.CreateUniquePrefix();

        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            KeyPrefix = keyPrefix,
            MaxFreshGenerationsPerMinute = 3,
            CooldownSeconds = 15,
            TtlSafetyMargin = TimeSpan.FromSeconds(15), // total TTL = 75s
        };

        var limiter = new RedisGenerateRateLimiter(fixture.Multiplexer, options);
        long userId = 3003;
        var (userKey, _) = RedisRateLimitLuaScript.BuildKeys(keyPrefix, userId);
        IDatabase db = fixture.Multiplexer.GetDatabase();

        // 1. Initial accepted request
        var first = await limiter.TryAcquireAsync(userId, CancellationToken.None);
        first.Allowed.Should().BeTrue();

        TimeSpan? initialTtl = await db.KeyTimeToLiveAsync(userKey);
        initialTtl.Should().NotBeNull();
        initialTtl!.Value.TotalSeconds.Should().BeInRange(70, 75);

        // 2. Wait 2 seconds
        await Task.Delay(2000);

        // 3. Rejected call during cooldown
        var rejected = await limiter.TryAcquireAsync(userId, CancellationToken.None);
        rejected.Allowed.Should().BeFalse();
        rejected.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationCooldown);

        // 4. TTL must NOT have been refreshed back to 75s
        TimeSpan? ttlAfterRejection = await db.KeyTimeToLiveAsync(userKey);
        ttlAfterRejection.Should().NotBeNull();
        ttlAfterRejection!.Value.TotalSeconds.Should().BeLessThan(initialTtl.Value.TotalSeconds - 1.5);
    }

    [RedisFact]
    public async Task AcceptedEvent_ExactlyAtRollingWindowBoundary_NoLongerConsumesQuota()
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        string keyPrefix = fixture.CreateUniquePrefix();
        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            KeyPrefix = keyPrefix,
            MaxFreshGenerationsPerMinute = 1,
            CooldownSeconds = 0,
        };
        const long userId = 4004;
        var (userKey, _) = RedisRateLimitLuaScript.BuildKeys(keyPrefix, userId);
        IDatabase db = fixture.Multiplexer.GetDatabase();
        RedisResult[] redisTime = (RedisResult[])(await db.ExecuteAsync("TIME"))!;
        long nowMs = ((long)redisTime[0] * 1000) + ((long)redisTime[1] / 1000);
        await db.SortedSetAddAsync(userKey, "boundary-event", nowMs - 60_000);
        await db.KeyExpireAsync(userKey, TimeSpan.FromSeconds(75));

        var decision = await new RedisGenerateRateLimiter(fixture.Multiplexer, options)
            .TryAcquireAsync(userId, CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        (await db.SortedSetLengthAsync(userKey)).Should().Be(1);
    }

    [RedisFact]
    public async Task Acquisition_PrunesExpiredRegistryMembers()
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        string keyPrefix = fixture.CreateUniquePrefix();
        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            KeyPrefix = keyPrefix,
            TtlSafetyMargin = TimeSpan.FromSeconds(15),
        };
        const long userId = 4104;
        var (_, registryKey) = RedisRateLimitLuaScript.BuildKeys(keyPrefix, userId);
        IDatabase db = fixture.Multiplexer.GetDatabase();
        RedisResult[] redisTime = (RedisResult[])(await db.ExecuteAsync("TIME"))!;
        long nowMs = ((long)redisTime[0] * 1000) + ((long)redisTime[1] / 1000);
        await db.SortedSetAddAsync(registryKey, "expired-user", nowMs - 75_001);

        var decision = await new RedisGenerateRateLimiter(fixture.Multiplexer, options)
            .TryAcquireAsync(userId, CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        (await db.SortedSetScoreAsync(registryKey, "expired-user")).Should().BeNull();
        (await db.SortedSetLengthAsync(registryKey)).Should().Be(1);
    }

    [RedisFact]
    public async Task AcceptedIdleUserKey_ExpiresAndRegistryConvergesOnNextSample()
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        string keyPrefix = fixture.CreateUniquePrefix();
        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            KeyPrefix = keyPrefix,
            TtlSafetyMargin = TimeSpan.Zero,
        };
        const long expiredUserId = 4204;
        const long samplingUserId = 4205;
        var (expiredUserKey, registryKey) = RedisRateLimitLuaScript.BuildKeys(
            keyPrefix,
            expiredUserId);
        IDatabase db = fixture.Multiplexer.GetDatabase();
        var limiter = new RedisGenerateRateLimiter(fixture.Multiplexer, options);

        (await limiter.TryAcquireAsync(expiredUserId, CancellationToken.None))
            .Allowed.Should().BeTrue();

        await Task.Delay(TimeSpan.FromSeconds(61));
        (await db.KeyExistsAsync(expiredUserKey)).Should().BeFalse();

        (await limiter.TryAcquireAsync(samplingUserId, CancellationToken.None))
            .Allowed.Should().BeTrue();
        (await db.SortedSetScoreAsync(registryKey, expiredUserId.ToString()))
            .Should().BeNull();
        (await db.SortedSetLengthAsync(registryKey)).Should().Be(1);
    }

    [RedisFact]
    public async Task UnavailableRedis_FailsClosedWithoutLocalFallback()
    {
        // 1. Disconnected multiplexer
        var deadConfig = ConfigurationOptions.Parse("127.0.0.1:59999");
        deadConfig.AbortOnConnectFail = false;
        deadConfig.ConnectTimeout = 200;
        deadConfig.SyncTimeout = 200;
        deadConfig.AsyncTimeout = 200;

        using var deadMultiplexer = await ConnectionMultiplexer.ConnectAsync(deadConfig);
        var deadOptions = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            CommandTimeout = TimeSpan.FromMilliseconds(300),
        };

        var deadLimiter = new RedisGenerateRateLimiter(deadMultiplexer, deadOptions);

        var deadDecision = await deadLimiter.TryAcquireAsync(5005, CancellationToken.None);
        deadDecision.Allowed.Should().BeFalse();
        deadDecision.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationRateLimiterUnavailable);
    }

    [RedisFact]
    public async Task TemporarilyPausedRedis_SameLimiterRecoversWithoutProcessRestart()
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            KeyPrefix = fixture.CreateUniquePrefix(),
            CommandTimeout = TimeSpan.FromMilliseconds(100),
            CooldownSeconds = 0,
        };
        var limiter = new RedisGenerateRateLimiter(fixture.Multiplexer, options);
        IDatabase db = fixture.Multiplexer.GetDatabase();

        await db.ExecuteAsync("CLIENT", "PAUSE", 500, "ALL");

        var unavailable = await limiter.TryAcquireAsync(5005, CancellationToken.None);
        unavailable.Allowed.Should().BeFalse();
        unavailable.ErrorCode.Should().Be(
            SchedulingErrorCodes.GenerationRateLimiterUnavailable);

        await Task.Delay(700);

        var recoveredDecision = await limiter.TryAcquireAsync(5006, CancellationToken.None);
        recoveredDecision.Allowed.Should().BeTrue();
    }

    [RedisFact]
    public async Task KeyIsolation_EnsuresDifferentPrefixesDoNotInterfere()
    {
        await using var fixture = await RedisTestFixture.CreateAsync();
        string prefixA = fixture.CreateUniquePrefix();
        string prefixB = fixture.CreateUniquePrefix();

        var limiterA = new RedisGenerateRateLimiter(fixture.Multiplexer, new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            KeyPrefix = prefixA,
            MaxFreshGenerationsPerMinute = 1,
            CooldownSeconds = 0,
        });

        var limiterB = new RedisGenerateRateLimiter(fixture.Multiplexer, new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            KeyPrefix = prefixB,
            MaxFreshGenerationsPerMinute = 1,
            CooldownSeconds = 0,
        });

        long userId = 6006;

        // Limiter A reaches quota
        (await limiterA.TryAcquireAsync(userId, CancellationToken.None)).Allowed.Should().BeTrue();
        (await limiterA.TryAcquireAsync(userId, CancellationToken.None)).Allowed.Should().BeFalse();

        // Limiter B for same user still has quota because prefix is isolated
        (await limiterB.TryAcquireAsync(userId, CancellationToken.None)).Allowed.Should().BeTrue();
    }

    private static ServiceProvider BuildIndependentProvider(
        IConnectionMultiplexer multiplexer,
        SchedulingRateLimitOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(multiplexer);
        services.AddSingleton<IGenerateRateLimiter, RedisGenerateRateLimiter>();
        return services.BuildServiceProvider();
    }
}