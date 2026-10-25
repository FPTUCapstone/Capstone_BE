using FluentAssertions;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Analysis;

public class BgeM3CanonicalFixtureTests
{
    private const string ExpectedHash = "D50E09CC6900D908D93B62068BDE138EDB35DE12FB5B4241D3EB8110986BC4AC";

    [Fact]
    public void CanonicalFixtureFile_Exists_AndMatchesCanonicalSha256()
    {
        var fixturePath = BgeM3BenchmarkFixture.ResolveFixturePath();
        File.Exists(fixturePath).Should().BeTrue("canonical raw fixture must exist on disk");
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();

        var fixture = BgeM3BenchmarkFixture.LoadAndVerify(cityData);

        fixture.Sha256.Should().Be(ExpectedHash, "canonical fixture content must match expected cryptographic SHA-256");
        fixture.Sha256.Should().Be(BgeM3BenchmarkFixture.ExpectedFixtureSha256);
    }

    [Fact]
    public void LoadAndVerify_ReturnsCanonicalHashAndValidatedDocument()
    {
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();

        var fixture = BgeM3BenchmarkFixture.LoadAndVerify(cityData);

        fixture.Sha256.Should().Be(ExpectedHash);
        fixture.Document.ScenarioId.Should().Be(cityData.Id);
    }

    [Fact]
    public void LoadAndVerify_AcceptsCanonicalFixtureAcrossPlatformLineEndings()
    {
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();
        var canonicalJson = File.ReadAllText(BgeM3BenchmarkFixture.ResolveFixturePath());
        var lfJson = canonicalJson.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lfFixturePath = Path.GetTempFileName();
        var crlfFixturePath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(lfFixturePath, lfJson);
            File.WriteAllText(crlfFixturePath, lfJson.Replace("\n", "\r\n", StringComparison.Ordinal));

            var lfFixture = BgeM3BenchmarkFixture.LoadAndVerify(cityData, lfFixturePath);
            var crlfFixture = BgeM3BenchmarkFixture.LoadAndVerify(cityData, crlfFixturePath);

            lfFixture.Sha256.Should().Be(BgeM3BenchmarkFixture.ExpectedFixtureSha256);
            crlfFixture.Sha256.Should().Be(lfFixture.Sha256);
        }
        finally
        {
            File.Delete(lfFixturePath);
            File.Delete(crlfFixturePath);
        }
    }

    [Fact]
    public void LoadAndVerify_WhenScenarioDoesNotMatch_RejectsFixture()
    {
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData() with
        {
            Id = "different-scenario"
        };

        var act = () => BgeM3BenchmarkFixture.LoadAndVerify(cityData);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Scenario ID mismatch*");
    }

    [Fact]
    public void CanonicalFixture_MatchesScenarioFactoryData_AsSingleSourceOfTruth()
    {
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();
        var doc = BgeM3BenchmarkFixture.LoadAndVerify(cityData).Document;

        doc.SchemaVersion.Should().Be("1.0");
        doc.ModelName.Should().Be("bge-m3:latest");
        doc.ScenarioId.Should().Be(cityData.Id);
        doc.Personas.Should().HaveCount(cityData.Personas.Count);

        var expectedPersonaIds = cityData.Personas.Select(p => p.Id).ToHashSet();
        doc.Personas.Select(p => p.PersonaId).Should().BeEquivalentTo(expectedPersonaIds);

        var expectedPoiIds = cityData.Pois.Select(p => p.Id).ToHashSet();

        foreach (var personaEntry in doc.Personas)
        {
            var expectedPersona = cityData.Personas.Single(p => p.Id == personaEntry.PersonaId);
            personaEntry.PreferenceTokens.Should().BeEquivalentTo(expectedPersona.Tags,
                $"persona {personaEntry.PersonaId} must match scenario factory preference tokens");

            personaEntry.Scores.Should().HaveCount(cityData.Pois.Count);
            var poiIds = personaEntry.Scores.Keys.Select(long.Parse).ToHashSet();
            poiIds.Should().BeEquivalentTo(expectedPoiIds);

            foreach (var (poiKey, score) in personaEntry.Scores)
            {
                score.Should().BeInRange(0.0m, 1.0m, $"score for POI {poiKey} in {personaEntry.PersonaId} must be in [0, 1]");
            }
        }
    }

    [Fact]
    public async Task RecordedPoiRankingProvider_ThrowsException_WhenCandidateScoreIsMissing()
    {
        var provider = new RecordedPoiRankingProvider();
        // Candidate with invalid POI ID 9999 that does not exist in the fixture
        var missingCandidate = new PoiRankingCandidate(9999L, "Nonexistent POI", "Culture", ["culture"]);
        var request = new PoiRankingRequest([missingCandidate], new PoiRankingContext(["culture", "museum", "history", "heritage", "art"]));

        var act = () => provider.RankAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*9999*");
    }

    [Theory]
    [InlineData("shopping", "mall")]
    [InlineData("unknown_tag")]
    [InlineData("museum")] // partial token set of CultureEnthusiast
    public async Task RecordedProvider_ThrowsException_WhenTokensDoNotMatchAnyVerifiedPersona(params string[] tokens)
    {
        var provider = new RecordedPoiRankingProvider();
        var candidate = new PoiRankingCandidate(1L, "Cham Museum", "Museum", ["culture"]);
        var request = new PoiRankingRequest([candidate], new PoiRankingContext(tokens));
        var act = () => provider.RankAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No recorded persona matches the provided preference token set*");
    }

    [Fact]
    public void BgeM3CanonicalFixtureValidator_Rejects_MissingPoi()
    {
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();
        var validDoc = BgeM3BenchmarkFixture.LoadAndVerify(cityData).Document;

        // Mutate: remove POI 1 from CultureEnthusiast
        var corruptedEntries = validDoc.Personas.Select(p =>
        {
            if (p.PersonaId == "CultureEnthusiast")
            {
                var partialScores = p.Scores.Where(kv => kv.Key != "1").ToDictionary(kv => kv.Key, kv => kv.Value);
                return p with { Scores = partialScores };
            }
            return p;
        }).ToList();

        var corruptedDoc = validDoc with { Personas = corruptedEntries };

        var act = () => BgeM3CanonicalFixtureValidator.Validate(cityData, corruptedDoc);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Missing*POI*");
    }

    [Fact]
    public void BgeM3CanonicalFixtureValidator_Rejects_ScoreOutOfRange()
    {
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();
        var validDoc = BgeM3BenchmarkFixture.LoadAndVerify(cityData).Document;

        // Mutate: score > 1.0
        var corruptedEntries = validDoc.Personas.Select(p =>
        {
            if (p.PersonaId == "CultureEnthusiast")
            {
                var corruptedScores = p.Scores.ToDictionary(kv => kv.Key, kv => kv.Key == "1" ? 1.50m : kv.Value);
                return p with { Scores = corruptedScores };
            }
            return p;
        }).ToList();

        var corruptedDoc = validDoc with { Personas = corruptedEntries };

        var act = () => BgeM3CanonicalFixtureValidator.Validate(cityData, corruptedDoc);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*outside [0, 1]*");
    }

    [Fact]
    public void BgeM3CanonicalFixtureValidator_Rejects_MissingPersona()
    {
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();
        var validDoc = BgeM3BenchmarkFixture.LoadAndVerify(cityData).Document;

        var corruptedDoc = validDoc with { Personas = validDoc.Personas.Where(p => p.PersonaId != "CultureEnthusiast").ToList() };

        var act = () => BgeM3CanonicalFixtureValidator.Validate(cityData, corruptedDoc);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*persona*");
    }

    [Fact]
    public void BgeM3CanonicalFixtureValidator_Rejects_WrongTokenSet()
    {
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();
        var validDoc = BgeM3BenchmarkFixture.LoadAndVerify(cityData).Document;

        var corruptedEntries = validDoc.Personas.Select(p =>
        {
            if (p.PersonaId == "CultureEnthusiast")
            {
                return p with { PreferenceTokens = ["wrong_tag"] };
            }
            return p;
        }).ToList();

        var corruptedDoc = validDoc with { Personas = corruptedEntries };

        var act = () => BgeM3CanonicalFixtureValidator.Validate(cityData, corruptedDoc);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*token*");
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("model")]
    [InlineData("endpoint")]
    [InlineData("provenance")]
    [InlineData("timestamp")]
    public void BgeM3CanonicalFixtureValidator_Rejects_InvalidRequiredMetadata(string field)
    {
        var cityData = SchedulingBenchmarkScenarioFactory.CreateDaNangPersonalizationData();
        var valid = BgeM3BenchmarkFixture.LoadAndVerify(cityData).Document;
        var invalid = field switch
        {
            "schema" => valid with { SchemaVersion = "2.0" },
            "model" => valid with { ModelName = "" },
            "endpoint" => valid with { EndpointMode = "" },
            "provenance" => valid with { Provenance = "" },
            "timestamp" => valid with { CaptureTimestamp = default },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        var act = () => BgeM3CanonicalFixtureValidator.Validate(cityData, invalid);

        act.Should().Throw<InvalidOperationException>();
    }
}