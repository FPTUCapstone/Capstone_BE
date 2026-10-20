using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.UnitTests.Features.Scheduling.Shadow;
using TripMate.Domain.Constants;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

public class SchedulingBenchmarkScenarioFactoryTests
{
    [Fact]
    public void CreateDaNangBaseline_PreservesLegacyCandidatesIndicesAndTravelMatrix()
    {
        var data = SchedulingBenchmarkScenarioFactory.CreateDaNangBaselineData();

        var scenario = SchedulingBenchmarkScenarioFactory.CreateScenario(data);

        scenario.Id.Should().Be("danang-real-1day");
        scenario.Candidates.Select(candidate => candidate.Id).Should().Equal(Enumerable.Range(1, 16).Select(id => (long)id));
        scenario.Indices.OrderBy(pair => pair.Key).Select(pair => pair.Value).Should().Equal(Enumerable.Range(1, 16));
        scenario.Matrix.PointCount.Should().Be(18);
        MatrixFingerprint(scenario.Matrix).Should().Be("f3ce392ab8eda167a88790d329855200f8a73bb3ce309335b1b5a007a88d07a5");

        scenario.Candidates[0].CategoryName.Should().Be(PoiCategoryNames.Museum);
        scenario.Candidates[0].Tags.Should().Equal("culture", "museum", "history");
        scenario.Candidates[8].CategoryName.Should().Be(PoiCategoryNames.Beach);
        scenario.Candidates[8].Tags.Should().Equal("beach", "relaxation", "nature", "outdoor");
        scenario.Candidates[11].CategoryName.Should().Be(PoiCategoryNames.NaturalAttraction);
        scenario.Candidates[14].CategoryName.Should().Be(PoiCategoryNames.FoodAndDrink);

        data.Personas.Select(persona => persona.Id).Should().Equal(
            "CultureEnthusiast",
            "FoodLoverRelaxed",
            "ActiveExplorerFast",
            "ScenicPhotography",
            "DefaultGeneralist");
        data.Personas.Select(persona =>
                $"{persona.Id}|{persona.Pace}|{persona.Risk}|{string.Join(',', persona.Tags)}|{string.Join(',', persona.LovedCategories)}")
            .Should().Equal(
                "CultureEnthusiast|Moderate|Medium|culture,museum,history,heritage,art|Culture,Museum",
                "FoodLoverRelaxed|Relaxed|Low|food,coffee,cuisine,culinary|Food & Drink",
                "ActiveExplorerFast|Fast|High|nature,adventure,outdoor,hiking,natural attraction|Nature,Natural attraction",
                "ScenicPhotography|Moderate|Low|photography,scenic,beach,sightseeing,nature|Culture,Nature,Beach",
                "DefaultGeneralist|Moderate|Medium||");
        scenario.Candidates.Select(candidate => candidate.CategoryName).Should().Equal(
            PoiCategoryNames.Museum, PoiCategoryNames.Museum, PoiCategoryNames.Museum, PoiCategoryNames.Museum,
            PoiCategoryNames.Culture, PoiCategoryNames.Culture, PoiCategoryNames.Culture, PoiCategoryNames.Nature,
            PoiCategoryNames.Beach, PoiCategoryNames.Culture, PoiCategoryNames.Culture,
            PoiCategoryNames.NaturalAttraction, PoiCategoryNames.NaturalAttraction, PoiCategoryNames.Culture,
            PoiCategoryNames.FoodAndDrink, PoiCategoryNames.FoodAndDrink);
        scenario.Candidates.Select(candidate => string.Join(',', candidate.Tags ?? [])).Should().Equal(
            "culture,museum,history", "culture,museum,history", "culture,photography,art", "history,culture",
            "architecture,photography,sightseeing", "architecture,photography", "photography,relaxation",
            "architecture,relaxation,photography", "beach,relaxation,nature,outdoor", "food,coffee,culture,shopping",
            "heritage,culture,nature,photography", "nature,adventure,outdoor,hiking", "nature,adventure",
            "heritage,culture,shopping", "food,cuisine,coffee", "coffee,food,relaxation");
    }

    [Fact]
    public void BothConsumers_AreLockedToTheReviewedSharedFactoryVariants()
    {
        var benchmarkData = LocalShadowBenchmarkRunner.CreateCityDataForBenchmark();
        var measurementData = DaNangScoreDistributionMeasurement.CreateCityDataForMeasurement();

        benchmarkData.Pois.Take(16).Should().BeEquivalentTo(
            measurementData.Pois,
            options => options.WithStrictOrdering());
        benchmarkData.Personas.Should().BeEquivalentTo(
            measurementData.Personas,
            options => options.WithStrictOrdering());
        benchmarkData.Pois.Should().HaveCount(19);
        measurementData.Pois.Should().HaveCount(16, "bge-m3 scores exist only for the reviewed baseline IDs 1..16");
    }

    [Fact]
    public void CreateScenario_AcceptsTwoCityDataInstances_WithoutCitySpecificRunnerTypes()
    {
        var first = CreateTinyCityData("tiny-city-a", 101, 16.05m);
        var second = CreateTinyCityData("tiny-city-b", 202, 16.15m);

        var scenarios = new[] { first, second }
            .Select(SchedulingBenchmarkScenarioFactory.CreateScenario)
            .ToList();

        scenarios.Select(scenario => scenario.Id).Should().Equal("tiny-city-a", "tiny-city-b");
        scenarios.Select(scenario => scenario.Candidates.Single().Id).Should().Equal(101, 202);
        scenarios.Should().OnlyContain(scenario => scenario.Matrix.PointCount == 3);
        scenarios.Should().OnlyContain(scenario => scenario.Indices.Values.Single() == 1);
    }

    [Fact]
    public void CreateDaNangV3b_AddsOnlyTrustedNatureSeedRecords()
    {
        var data = SchedulingBenchmarkScenarioFactory.CreateDaNangV3bData();

        data.Pois.Should().HaveCount(19);
        data.Pois.Skip(16).Select(poi => poi.Id).Should().Equal(17, 18, 19);
        data.Pois.Skip(16).Should().OnlyContain(poi => poi.CategoryName == PoiCategoryNames.NaturalAttraction);
        data.Pois.Skip(16).Select(poi => string.Join(',', poi.Tags)).Should().Equal(
            "nature", "nature,outdoor", "nature,outdoor");
        data.Source.Should().Contain("20260914_seed_danang_planning_pois.sql");
    }

    private static CityBenchmarkScenarioData CreateTinyCityData(string id, long poiId, decimal latitude)
    {
        var hotel = new RoutePoint(latitude, 108.20m);
        return new CityBenchmarkScenarioData(
            Id: id,
            Source: "Synthetic test fixture",
            ExpectedFeasible: true,
            StartAtUtc: new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero),
            TimeZoneId: "Asia/Ho_Chi_Minh",
            Start: hotel,
            End: hotel,
            AvailableMinutes: 120,
            TransportMode: TransportMode.Walking,
            RestPreference: RestPreference.None,
            BudgetVnd: 100_000m,
            MandatoryPoiIds: [poiId],
            Pois:
            [
                new BenchmarkPoiData(
                    poiId,
                    $"POI {poiId}",
                    new RoutePoint(latitude + 0.01m, 108.21m),
                    PoiCategoryNames.Culture,
                    30,
                    0m,
                    IndoorOutdoorType.Outdoor,
                    false,
                    7m,
                    7m,
                    ["culture"],
                    [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(21, 0))])
            ],
            Personas: []);
    }

    private static string MatrixFingerprint(RouteDurationMatrix matrix)
    {
        var values = new StringBuilder();
        for (var from = 0; from < matrix.PointCount; from++)
        {
            for (var to = 0; to < matrix.PointCount; to++)
            {
                values.Append(matrix.GetMinutes(from, to)).Append('/');
                values.Append(matrix.GetMeters(from, to)).Append(';');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(values.ToString()))).ToLowerInvariant();
    }
}