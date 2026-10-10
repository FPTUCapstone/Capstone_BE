using FluentAssertions;

using Microsoft.Extensions.Configuration;

using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Application.UnitTests.Features.Scheduling;

public sealed class SchedulingGenerationOptionsBindingTests
{
    [Theory]
    [InlineData("Heuristic", SchedulingSolverMode.Heuristic)]
    [InlineData("Csp", SchedulingSolverMode.Csp)]
    [InlineData("MiniRouting", SchedulingSolverMode.MiniRouting)]
    public void Bind_SolverModeName_SelectsTheSolver(string value, SchedulingSolverMode expected)
    {
        var options = Bind(new Dictionary<string, string?> { ["SolverMode"] = value });

        options.SolverMode.Should().Be(expected);
    }

    [Fact]
    public void Bind_CspSection_BindsNestedLimits()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Csp:MaxNodes"] = "1234",
            ["Csp:TimeLimitMilliseconds"] = "900",
            ["Csp:MaxOptionalDomainSize"] = "12",
            ["Csp:MaxStops"] = "8",
        });

        options.Csp.Should().BeEquivalentTo(new
        {
            MaxNodes = 1234,
            TimeLimitMilliseconds = 900,
            MaxOptionalDomainSize = 12,
            MaxStops = 8,
        });
    }

    [Fact]
    public void Bind_UnknownSolverModeName_FailsInsteadOfFallingBack()
    {
        Action bind = () => Bind(new Dictionary<string, string?> { ["SolverMode"] = "Fastest" });

        bind.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Bind_UndefinedNumericSolverMode_IsRejectedByTheValidator()
    {
        var options = Bind(new Dictionary<string, string?> { ["SolverMode"] = "99" });

        new SchedulingGenerationOptionsValidator().Validate(null, options).Failed.Should().BeTrue();
    }

    private static SchedulingGenerationOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(
                pair => $"{SchedulingGenerationOptions.SectionName}:{pair.Key}",
                pair => pair.Value))
            .Build();
        return configuration.GetSection(SchedulingGenerationOptions.SectionName).Get<SchedulingGenerationOptions>()
            ?? new SchedulingGenerationOptions();
    }
}