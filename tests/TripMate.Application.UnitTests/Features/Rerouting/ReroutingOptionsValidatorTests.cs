using FluentAssertions;

using TripMate.Application.Features.Rerouting.Common;

namespace TripMate.Application.UnitTests.Features.Rerouting;

public sealed class ReroutingOptionsValidatorTests
{
    private readonly ReroutingOptionsValidator _validator = new();

    [Fact]
    public void Defaults_MatchApprovedSpec()
    {
        var options = new ReroutingOptions();

        options.ProposalTtl.Should().Be(TimeSpan.FromMinutes(15));
        options.MaxVariants.Should().Be(8);
        options.MaxReplacementsPerStop.Should().Be(3);
        options.ProposalRetryWindow.Should().Be(TimeSpan.FromMinutes(30));
        ReroutingOptions.SectionName.Should().Be("Rerouting");
    }

    [Fact]
    public void Validate_WithDefaults_Succeeds() =>
        _validator.Validate(null, new ReroutingOptions()).Succeeded.Should().BeTrue();

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(60)]
    public void Validate_ProposalTtlInsideRange_Succeeds(int minutes) =>
        _validator.Validate(null, new ReroutingOptions { ProposalTtl = TimeSpan.FromMinutes(minutes) })
            .Succeeded.Should().BeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(61)]
    public void Validate_ProposalTtlOutsideRange_Fails(int minutes)
    {
        var result = _validator.Validate(null, new ReroutingOptions { ProposalTtl = TimeSpan.FromMinutes(minutes) });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle(failure => failure.Contains(nameof(ReroutingOptions.ProposalTtl)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(240)]
    public void Validate_RetryWindowInsideRange_Succeeds(int minutes) =>
        _validator.Validate(null, new ReroutingOptions { ProposalRetryWindow = TimeSpan.FromMinutes(minutes) })
            .Succeeded.Should().BeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(241)]
    public void Validate_RetryWindowOutsideRange_Fails(int minutes)
    {
        var result = _validator.Validate(null, new ReroutingOptions { ProposalRetryWindow = TimeSpan.FromMinutes(minutes) });

        result.Failures.Should().ContainSingle(failure => failure.Contains(nameof(ReroutingOptions.ProposalRetryWindow)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Validate_MaxVariantsOutsideRange_Fails(int value)
    {
        var result = _validator.Validate(null, new ReroutingOptions { MaxVariants = value });

        result.Failures.Should().ContainSingle(failure => failure.Contains(nameof(ReroutingOptions.MaxVariants)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Validate_MaxReplacementsPerStopOutsideRange_Fails(int value)
    {
        var result = _validator.Validate(null, new ReroutingOptions { MaxReplacementsPerStop = value });

        result.Failures.Should().ContainSingle(failure => failure.Contains(nameof(ReroutingOptions.MaxReplacementsPerStop)));
    }

    [Fact]
    public void Validate_ReportsEveryFailureAtOnce()
    {
        var result = _validator.Validate(null, new ReroutingOptions
        {
            ProposalTtl = TimeSpan.Zero,
            MaxVariants = 0,
            MaxReplacementsPerStop = 0,
            ProposalRetryWindow = TimeSpan.Zero,
        });

        result.Failures.Should().HaveCount(4);
    }

    [Fact]
    public void ErrorCodes_AreTheContractNamesFromTheSpec()
    {
        ReroutingErrorCodes.NotFound.Should().Be("rerouting.not_found");
        ReroutingErrorCodes.ProposalExpired.Should().Be("rerouting.proposal_expired");
        ReroutingErrorCodes.ProposalClosed.Should().Be("rerouting.proposal_closed");
        ReroutingErrorCodes.ProposalInfeasible.Should().Be("rerouting.proposal_infeasible");
    }
}