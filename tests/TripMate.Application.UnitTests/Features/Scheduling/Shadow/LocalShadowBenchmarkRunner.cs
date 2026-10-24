using FluentAssertions;

using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

namespace TripMate.Application.UnitTests.Features.Scheduling.Shadow;

public class LocalShadowBenchmarkRunner
{
    [Fact]
    public void Build_Local_Shadow_Benchmark_V3b_InMemory()
    {
        var cityData = CreateCityDataForBenchmark();

        var scenarios = SchedulingBenchmarkScenarioFactory.CreateV3bScenarios(cityData);

        scenarios.Should().HaveCount(21, "v3b preserves the reviewed 20-scenario corpus plus one Da Nang scenario");
        scenarios[^1].Id.Should().Be("danang-real-1day");
        scenarios[^1].Candidates.Should().HaveCount(19);
        cityData.Personas.Should().HaveCount(5);
    }

    internal static CityBenchmarkScenarioData CreateCityDataForBenchmark() =>
        SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData();
}