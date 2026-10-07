using FluentAssertions;

using Microsoft.Extensions.Configuration;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class SchedulingRateLimitOptionsValidatorTests
{
    private readonly SchedulingRateLimitOptionsValidator _validator = new();

    [Fact]
    public void Validate_ValidRedisOptions_Succeeds()
    {
        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            MaxFreshGenerationsPerMinute = 3,
            CooldownSeconds = 15,
            RedisConnectionStringName = "Redis",
            KeyPrefix = "tripmate:scheduling:rate-limit",
            CommandTimeout = TimeSpan.FromSeconds(2),
            TtlSafetyMargin = TimeSpan.FromSeconds(15),
        };

        var result = _validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ValidSingleInstanceOptions_Succeeds()
    {
        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.SingleInstance,
            MaxFreshGenerationsPerMinute = 5,
            CooldownSeconds = 10,
            IdleTtl = TimeSpan.FromMinutes(5),
            CleanupCadence = TimeSpan.FromMinutes(1),
            MaxTrackedUsers = 5_000,
        };

        var result = _validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void Validate_InvalidMaxFreshGenerationsPerMinute_Fails(int max)
    {
        var options = new SchedulingRateLimitOptions
        {
            MaxFreshGenerationsPerMinute = max,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.MaxFreshGenerationsPerMinute));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60)]
    [InlineData(100)]
    public void Validate_InvalidCooldownSeconds_Fails(int cooldown)
    {
        var options = new SchedulingRateLimitOptions
        {
            CooldownSeconds = cooldown,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.CooldownSeconds));
    }

    [Fact]
    public void Validate_UnknownProvider_Fails()
    {
        var options = new SchedulingRateLimitOptions
        {
            Provider = (SchedulingRateLimitProvider)999,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.Provider));
    }

    [Fact]
    public void Validate_MissingProviderSetting_FailsEvenWhenRedisConnectionExists()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = "localhost:6379",
            })
            .Build();
        var validator = new SchedulingRateLimitOptionsValidator(configuration);
        var options = new SchedulingRateLimitOptions();

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.Provider));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RedisMode_EmptyConnectionStringName_Fails(string name)
    {
        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            RedisConnectionStringName = name,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.RedisConnectionStringName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RedisMode_EmptyKeyPrefix_Fails(string prefix)
    {
        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            KeyPrefix = prefix,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.KeyPrefix));
    }

    [Fact]
    public void Validate_RedisMode_ZeroCommandTimeout_Fails()
    {
        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            CommandTimeout = TimeSpan.Zero,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.CommandTimeout));
    }

    [Fact]
    public void Validate_RedisMode_NegativeTtlSafetyMargin_Fails()
    {
        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.Redis,
            TtlSafetyMargin = TimeSpan.FromSeconds(-1),
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.TtlSafetyMargin));
    }

    [Fact]
    public void Validate_SingleInstanceMode_IdleTtlShorterThan60Seconds_Fails()
    {
        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.SingleInstance,
            IdleTtl = TimeSpan.FromSeconds(59),
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.IdleTtl));
    }

    [Fact]
    public void Validate_SingleInstanceMode_ZeroCleanupCadence_Fails()
    {
        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.SingleInstance,
            CleanupCadence = TimeSpan.Zero,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.CleanupCadence));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_SingleInstanceMode_NonPositiveMaxTrackedUsers_Fails(int maxTrackedUsers)
    {
        var options = new SchedulingRateLimitOptions
        {
            Provider = SchedulingRateLimitProvider.SingleInstance,
            MaxTrackedUsers = maxTrackedUsers,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.MaxTrackedUsers));
    }
}