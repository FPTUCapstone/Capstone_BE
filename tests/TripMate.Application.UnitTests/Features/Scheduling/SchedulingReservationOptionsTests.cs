using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.UnitTests.Features.Scheduling;

public sealed class SchedulingReservationOptionsTests
{
    [Fact]
    public void Defaults_CoverConfiguredProviderTimeouts()
    {
        var options = new SchedulingReservationOptions();
        var validator = CreateValidator();

        var result = validator.Validate(null, options);

        options.LeaseDuration.Should().Be(TimeSpan.FromSeconds(60));
        options.PollInterval.Should().Be(TimeSpan.FromMilliseconds(100));
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(60_000, 0)]
    [InlineData(60_000, 60_000)]
    [InlineData(25_000, 100)]
    public void Validate_WithUnsafeDurations_Fails(int leaseMilliseconds, int pollMilliseconds)
    {
        var options = new SchedulingReservationOptions
        {
            LeaseDuration = TimeSpan.FromMilliseconds(leaseMilliseconds),
            PollInterval = TimeSpan.FromMilliseconds(pollMilliseconds),
        };

        var result = CreateValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    private static SchedulingReservationOptionsValidator CreateValidator() =>
        new(Options.Create(new PersonalizationRankingOptions
        {
            ProviderTimeout = TimeSpan.FromSeconds(5),
        }));
}