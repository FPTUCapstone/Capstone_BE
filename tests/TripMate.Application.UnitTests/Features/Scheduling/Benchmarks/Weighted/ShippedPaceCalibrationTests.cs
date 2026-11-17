using FluentAssertions;

using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Weighted;

/// <summary>
/// ADR-002 D2 (G4c): the shipped <c>appsettings.json</c> calibration, run as WeightedUtility on the recorded Da Nang day
/// (8 hours), keeps a relaxed traveler at 3 ± 1 stops whatever their preferences.
/// </summary>
public sealed class ShippedPaceCalibrationTests
{
    [Fact]
    public async Task ShippedCalibration_KeepsEveryRelaxedPersonaWithinTheRelaxedStopTarget()
    {
        var configuration = WeightedGateConfiguration.LoadShipped(
            WeightedGateConfiguration.FindRepositoryRoot(AppContext.BaseDirectory));
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData();
        var scenario = SchedulingBenchmarkScenarioFactory.CreateScenario(cityData);
        var (min, max) = WeightedGateSummary.StopTargets[TravelerPace.Relaxed];
        var personas = cityData.Personas
            .Where(persona => SchedulingBenchmarkScenarioFactory.CreateProfile(persona, cityData.StartAtUtc) is not null)
            .ToArray();
        personas.Should().NotBeEmpty();

        foreach (var persona in personas)
        {
            var run = await WeightedGateExecutor.RunAsync(
                scenario, persona with { Pace = TravelerPace.Relaxed }, configuration, warmupIterations: 0, measuredIterations: 1);

            run.Pace.Should().Be(TravelerPace.Relaxed);
            run.Feasible.Should().BeTrue(persona.Id);
            run.StopCount.Should().BeInRange(min, max, "{0} is a relaxed traveler", persona.Id);
        }
    }

    /// <summary>PLAN G4: a traveler without a profile gets a varied day (at least two POI categories), not one theme.</summary>
    [Fact]
    public async Task ShippedCalibration_GivesATravelerWithoutAProfileAtLeastTwoCategories()
    {
        var configuration = WeightedGateConfiguration.LoadShipped(
            WeightedGateConfiguration.FindRepositoryRoot(AppContext.BaseDirectory));
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData();
        var scenario = SchedulingBenchmarkScenarioFactory.CreateScenario(cityData);
        var coldStart = cityData.Personas
            .Where(persona => SchedulingBenchmarkScenarioFactory.CreateProfile(persona, cityData.StartAtUtc) is null)
            .ToArray();
        coldStart.Should().NotBeEmpty();

        foreach (var persona in coldStart)
        {
            var run = await WeightedGateExecutor.RunAsync(
                scenario, persona, configuration, warmupIterations: 0, measuredIterations: 1);

            run.Feasible.Should().BeTrue(persona.Id);
            run.VisitCategories.Distinct().Should().HaveCountGreaterThanOrEqualTo(2, "{0} has no profile", persona.Id);
        }
    }

    /// <summary>
    /// PLAN G4: at every pace, personas with different interests get different optional stops. A high Relaxed travel
    /// cost once gave three of four personas the same two nearest POIs whatever they liked.
    /// </summary>
    [Fact]
    public async Task ShippedCalibration_GivesPersonasDistinctOptionalStopsAtEveryPace()
    {
        var configuration = WeightedGateConfiguration.LoadShipped(
            WeightedGateConfiguration.FindRepositoryRoot(AppContext.BaseDirectory));
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData();
        var scenario = SchedulingBenchmarkScenarioFactory.CreateScenario(cityData);
        var runs = new List<WeightedGateRun>();
        foreach (var persona in cityData.Personas
            .Where(persona => SchedulingBenchmarkScenarioFactory.CreateProfile(persona, cityData.StartAtUtc) is not null))
        {
            foreach (var pace in new[] { TravelerPace.Relaxed, TravelerPace.Moderate, TravelerPace.Fast })
            {
                runs.Add(await WeightedGateExecutor.RunAsync(
                    scenario, persona with { Pace = pace }, configuration, warmupIterations: 0, measuredIterations: 1));
            }
        }

        var overlap = WeightedGateSummary.Create([], runs).OverlapByPace;

        overlap.Should().HaveCount(3);
        overlap.Should().OnlyContain(
            pace => pace.MeanJaccard < WeightedGateSummary.MaxMeanJaccard,
            "personas must not share most optional stops at any pace");
    }

    /// <summary>
    /// Product decision 2026-11-11: choosing Fast adds stops over Moderate unless the day is already full. A Fast plan
    /// with the same stop count must use at least 90% of the window, so no further visit fits; forcing one would mean
    /// swapping liked long visits for short unliked ones (measured: travel cost near zero does exactly that).
    /// </summary>
    [Fact]
    public async Task ShippedCalibration_GivesEveryPersonaMoreStopsOnFastThanOnModerate_UnlessTheDayIsFull()
    {
        var configuration = WeightedGateConfiguration.LoadShipped(
            WeightedGateConfiguration.FindRepositoryRoot(AppContext.BaseDirectory));
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData();
        var scenario = SchedulingBenchmarkScenarioFactory.CreateScenario(cityData);
        var personas = cityData.Personas
            .Where(persona => SchedulingBenchmarkScenarioFactory.CreateProfile(persona, cityData.StartAtUtc) is not null)
            .ToArray();

        foreach (var persona in personas)
        {
            var moderate = await WeightedGateExecutor.RunAsync(
                scenario, persona with { Pace = TravelerPace.Moderate }, configuration, warmupIterations: 0, measuredIterations: 1);
            var fast = await WeightedGateExecutor.RunAsync(
                scenario, persona with { Pace = TravelerPace.Fast }, configuration, warmupIterations: 0, measuredIterations: 1);

            fast.Feasible.Should().BeTrue(persona.Id);
            fast.StopCount.Should().BeGreaterThanOrEqualTo(moderate.StopCount, "{0} chose Fast over Moderate", persona.Id);
            if (fast.StopCount == moderate.StopCount)
            {
                fast.PlanMinutes.Should().BeGreaterThanOrEqualTo(
                    fast.AvailableMinutes * 9 / 10, "{0} gets no extra stop on Fast only when the day is full", persona.Id);
            }
        }
    }
}