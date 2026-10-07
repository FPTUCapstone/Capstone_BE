using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Application.UnitTests.Features.Scheduling;

public sealed class SchedulingGenerationOptionsValidatorTests
{
    private readonly SchedulingGenerationOptionsValidator _validator = new();

    [Fact]
    public void Validate_WithDefaultOptions_Succeeds()
    {
        var options = new SchedulingGenerationOptions();

        var result = _validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    public void Validate_WithInvalidSeeds_Fails(int seeds)
    {
        var options = new SchedulingGenerationOptions
        {
            MaxRouteOptimizationSeeds = seeds,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.MaxRouteOptimizationSeeds));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(50001)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_WithInvalidEvaluations_Fails(int evaluations)
    {
        var options = new SchedulingGenerationOptions
        {
            MaxRouteEvaluations = evaluations,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.MaxRouteEvaluations));
    }

    [Fact]
    public void Validate_WithNegativeBuffers_Fails()
    {
        var options = new SchedulingGenerationOptions
        {
            TransitionBufferMinutes = -1,
            FinalReturnBufferMinutes = -2,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.TransitionBufferMinutes));
        result.FailureMessage.Should().Contain(nameof(options.FinalReturnBufferMinutes));
    }

    [Fact]
    public void Validate_WithTooSmallMaxMatrixCandidates_Fails()
    {
        var options = new SchedulingGenerationOptions
        {
            MaxMatrixCandidates = 5,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.MaxMatrixCandidates));
    }
}