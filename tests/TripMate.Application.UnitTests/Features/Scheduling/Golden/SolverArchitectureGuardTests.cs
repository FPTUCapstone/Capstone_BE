using System.Reflection;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Csp;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden;

public class SolverArchitectureGuardTests
{
    [Fact]
    public void SearchRun_does_not_contain_inclusion_or_optimistic_bitmasks()
    {
        var searchRunType = typeof(CspItinerarySolver).GetNestedType("SearchRun", BindingFlags.NonPublic);
        searchRunType.Should().NotBeNull("SearchRun nested class must exist in CspItinerarySolver");

        var fields = searchRunType!.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        // 1. SearchRun must NOT have ulong[] fields (no _inclusion or _optimistic)
        fields.Should().NotContain(
            f => f.FieldType == typeof(ulong[]),
            "SearchRun should not maintain raw ulong[] bitmask fields; bitmasks belong in IObjectiveKernel");

        fields.Should().NotContain(
            f => f.Name.Contains("inclusion", StringComparison.OrdinalIgnoreCase)
                 || f.Name.Contains("optimistic", StringComparison.OrdinalIgnoreCase),
            "SearchRun should not have fields named after inclusion or optimistic bounds");
    }

    [Fact]
    public void SearchRun_holds_objective_kernel_and_exposes_incumbent_token()
    {
        var searchRunType = typeof(CspItinerarySolver).GetNestedType("SearchRun", BindingFlags.NonPublic);
        searchRunType.Should().NotBeNull();

        var fields = searchRunType!.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        // 2. SearchRun must have IObjectiveKernel field
        fields.Should().Contain(
            f => typeof(IObjectiveKernel).IsAssignableFrom(f.FieldType),
            "SearchRun must delegate objective calculations to an IObjectiveKernel implementation");

        // 3. SearchRun must expose Incumbent property returning IncumbentToken?
        var incumbentProp = searchRunType.GetProperty("Incumbent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        incumbentProp.Should().NotBeNull("SearchRun must expose Incumbent property");
        incumbentProp!.PropertyType.Should().Be(typeof(IncumbentToken), "Incumbent property must be of type IncumbentToken");
    }

    [Fact]
    public void ObjectiveKernel_has_no_temporary_scaffolding_properties()
    {
        var kernelType = typeof(IObjectiveKernel);

        // 4. Scaffolding properties must be completely removed
        kernelType.GetProperty("CurrentInclusion", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().BeNull("Temporary scaffolding property CurrentInclusion must not exist on IObjectiveKernel");

        kernelType.GetProperty("TemporaryIncumbent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().BeNull("Temporary scaffolding property TemporaryIncumbent must not exist on IObjectiveKernel");
    }

    [Fact]
    public void Source_code_has_no_direct_calls_to_CompareInclusion_in_SearchRun()
    {
        var solverSourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "TripMate.Application", "Features", "Scheduling", "Csp", "CspItinerarySolver.cs");

        var normalizedPath = Path.GetFullPath(solverSourcePath);
        File.Exists(normalizedPath).Should().BeTrue($"CspItinerarySolver.cs must exist at {normalizedPath}");

        var sourceText = File.ReadAllText(normalizedPath);

        // SearchRun class substring
        var searchRunStart = sourceText.IndexOf("class SearchRun", StringComparison.Ordinal);
        searchRunStart.Should().BeGreaterThan(0, "SearchRun class must be found in CspItinerarySolver.cs");

        var searchRunCode = sourceText.Substring(searchRunStart);

        // 5. Must not call CompareInclusion in SearchRun
        searchRunCode.Should().NotContain("CompareInclusion",
            "SearchRun must not call CspProductRule.CompareInclusion directly; comparisons must go through IObjectiveKernel");
    }
}