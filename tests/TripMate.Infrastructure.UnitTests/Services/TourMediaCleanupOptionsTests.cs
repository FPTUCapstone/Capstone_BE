using System.ComponentModel.DataAnnotations;

using FluentAssertions;

using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class TourMediaCleanupOptionsTests
{
    [Fact]
    public void Defaults_AreSafeAndEnableTheBackgroundWorkerByDefault()
    {
        var options = new TourMediaCleanupOptions();
        var validationResults = new List<ValidationResult>();

        Validator.TryValidateObject(options, new ValidationContext(options), validationResults, validateAllProperties: true)
            .Should().BeTrue();
        options.PollInterval.Should().Be(TimeSpan.FromSeconds(30));
        options.LeaseDuration.Should().Be(TimeSpan.FromMinutes(2));
        options.BatchSize.Should().Be(20);
    }

    [Fact]
    public void InvalidPollInterval_IsRejected()
    {
        var options = new TourMediaCleanupOptions { PollInterval = TimeSpan.FromSeconds(1) };
        var validationResults = new List<ValidationResult>();

        Validator.TryValidateObject(options, new ValidationContext(options), validationResults, validateAllProperties: true)
            .Should().BeFalse();
    }

    [Fact]
    public void InvalidBatchSize_IsRejected()
    {
        var options = new TourMediaCleanupOptions { BatchSize = 101 };
        var validationResults = new List<ValidationResult>();

        Validator.TryValidateObject(options, new ValidationContext(options), validationResults, validateAllProperties: true)
            .Should().BeFalse();
    }
}