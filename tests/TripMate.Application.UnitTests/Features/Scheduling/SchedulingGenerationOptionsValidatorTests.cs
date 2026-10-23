using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Routing;

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
    public void Validate_WithUndefinedSolverMode_Fails()
    {
        var options = new SchedulingGenerationOptions
        {
            SolverMode = (SchedulingSolverMode)99,
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(options.SolverMode));
    }

    [Theory]
    [InlineData(nameof(CspOptions.MaxNodes))]
    [InlineData(nameof(CspOptions.TimeLimitMilliseconds))]
    [InlineData(nameof(CspOptions.MaxOptionalDomainSize))]
    [InlineData(nameof(CspOptions.MaxStops))]
    [InlineData(nameof(CspOptions.FinalCandidatesToValidate))]
    public void Validate_WithNonPositiveCspLimit_Fails(string limit)
    {
        var csp = limit switch
        {
            nameof(CspOptions.MaxNodes) => new CspOptions { MaxNodes = 0 },
            nameof(CspOptions.TimeLimitMilliseconds) => new CspOptions { TimeLimitMilliseconds = 0 },
            nameof(CspOptions.MaxOptionalDomainSize) => new CspOptions { MaxOptionalDomainSize = 0 },
            nameof(CspOptions.FinalCandidatesToValidate) => new CspOptions { FinalCandidatesToValidate = 0 },
            _ => new CspOptions { MaxStops = 0 },
        };

        var result = _validator.Validate(null, new SchedulingGenerationOptions { Csp = csp });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain($"{nameof(SchedulingGenerationOptions.Csp)}.{limit}");
    }

    [Theory]
    [InlineData(nameof(MiniRoutingOptions.MaxSkipPenaltyMinutes))]
    [InlineData(nameof(MiniRoutingOptions.MaxGlsIterations))]
    [InlineData(nameof(MiniRoutingOptions.TimeLimitMilliseconds))]
    [InlineData(nameof(MiniRoutingOptions.MaxOrOptSegmentLength))]
    [InlineData(nameof(MiniRoutingOptions.FinalCandidatesToValidate))]
    [InlineData(nameof(MiniRoutingOptions.GlsLambdaFactor))]
    public void Validate_WithInvalidMiniRoutingOption_Fails(string option)
    {
        var routing = option switch
        {
            nameof(MiniRoutingOptions.MaxSkipPenaltyMinutes) => new MiniRoutingOptions { MaxSkipPenaltyMinutes = 0 },
            nameof(MiniRoutingOptions.MaxGlsIterations) => new MiniRoutingOptions { MaxGlsIterations = 0 },
            nameof(MiniRoutingOptions.TimeLimitMilliseconds) => new MiniRoutingOptions { TimeLimitMilliseconds = -1 },
            nameof(MiniRoutingOptions.MaxOrOptSegmentLength) => new MiniRoutingOptions { MaxOrOptSegmentLength = 0 },
            nameof(MiniRoutingOptions.FinalCandidatesToValidate) => new MiniRoutingOptions { FinalCandidatesToValidate = 0 },
            _ => new MiniRoutingOptions { GlsLambdaFactor = double.NaN },
        };

        var result = _validator.Validate(null, new SchedulingGenerationOptions { MiniRouting = routing });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain($"{nameof(SchedulingGenerationOptions.MiniRouting)}.{option}");
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