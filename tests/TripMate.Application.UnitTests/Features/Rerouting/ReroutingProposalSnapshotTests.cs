using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;

using TripMate.Application.Features.Rerouting.Common;

namespace TripMate.Application.UnitTests.Features.Rerouting;

public sealed class ReroutingProposalSnapshotTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private static ReroutingProposalSnapshot Sample(string strategy = ReroutingStrategies.ReplaceWithIndoor) => new(
        ReroutingProposalSnapshot.CurrentSchemaVersion,
        strategy,
        "trip_alert.severe_weather",
        BaseItineraryId: 100,
        BaseItineraryVersion: 2,
        new ReroutingAnchorSnapshot(At, 16.05m, 108.2m, 300),
        [new ReroutingBaseStop(11, 5, "Ba Na Hills", 16.0m, 108.0m, At.AddHours(1), At.AddHours(2), false)],
        [new ReroutingProposedStop(
            9, "Museum of Cham Sculpture", 16.06m, 108.22m, At.AddHours(1), At.AddHours(2),
            false, "Motorbike", ReroutingStopChanges.Added)],
        RemovedPoiIds: [5],
        AffectedItineraryItemIds: [11],
        TravelTimeDiffMinutes: -12,
        ArrivalTimeDiffMinutes: 20,
        EndAtUtc: At.AddHours(5));

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var snapshot = Sample();

        var parsed = ReroutingProposalSnapshot.TryParse(ReroutingProposalSnapshot.Serialize(snapshot));

        parsed.Should().BeEquivalentTo(snapshot);
    }

    [Fact]
    public void Serialize_UsesCamelCaseNamesAndStringStrategy()
    {
        using var document = JsonDocument.Parse(ReroutingProposalSnapshot.Serialize(Sample(ReroutingStrategies.Reorder)));
        var root = document.RootElement;

        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("strategy").GetString().Should().Be("Reorder");
        root.GetProperty("proposedStops")[0].GetProperty("change").GetString().Should().Be("Added");
        root.GetProperty("anchor").GetProperty("startAtUtc").GetString().Should().EndWith("Z");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void TryParse_NullBlankOrMalformed_ReturnsNull(string? json) =>
        ReroutingProposalSnapshot.TryParse(json).Should().BeNull();

    [Theory]
    [InlineData("{\"demo\":true,\"route\":\"bridge detour\"}")]
    [InlineData("{\"demo\":true,\"proposal\":\"indoor alternative\"}")]
    public void TryParse_UatDemoRowWithoutSchemaVersion_ReturnsNull(string json) =>
        ReroutingProposalSnapshot.TryParse(json).Should().BeNull();

    [Fact]
    public void TryParse_UnknownSchemaVersion_ReturnsNull()
    {
        var json = ReroutingProposalSnapshot.Serialize(Sample()).Replace("\"schemaVersion\":1", "\"schemaVersion\":2");

        ReroutingProposalSnapshot.TryParse(json).Should().BeNull();
    }

    [Fact]
    public void TryParse_InvalidInstant_ReturnsNull()
    {
        var json = ReroutingProposalSnapshot.Serialize(Sample()).Replace("\"endAtUtc\":\"2026-11-24T14:00:00Z\"", "\"endAtUtc\":\"tomorrow\"");

        json.Should().Contain("tomorrow");
        ReroutingProposalSnapshot.TryParse(json).Should().BeNull();
    }

    [Fact]
    public void TryParse_OffsetInstant_IsNormalizedToUtc()
    {
        var json = ReroutingProposalSnapshot.Serialize(Sample()).Replace("\"endAtUtc\":\"2026-11-24T14:00:00Z\"", "\"endAtUtc\":\"2026-11-24T21:00:00+07:00\"");

        ReroutingProposalSnapshot.TryParse(json)!.EndAtUtc.Should().Be(At.AddHours(5));
        ReroutingProposalSnapshot.TryParse(json)!.EndAtUtc.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TryParse_UnknownStrategy_ReturnsNull()
    {
        var json = ReroutingProposalSnapshot.Serialize(Sample()).Replace("ReplaceWithIndoor", "Teleport");

        ReroutingProposalSnapshot.TryParse(json).Should().BeNull();
    }

    [Theory]
    [InlineData("proposedStops")]
    [InlineData("baseStops")]
    public void TryParse_MissingOrEmptyStopLists_ReturnsNull(string property)
    {
        var node = JsonNode.Parse(ReroutingProposalSnapshot.Serialize(Sample()))!.AsObject();
        node.Remove(property);
        ReroutingProposalSnapshot.TryParse(node.ToJsonString()).Should().BeNull();

        node[property] = new JsonArray();
        ReroutingProposalSnapshot.TryParse(node.ToJsonString()).Should().BeNull();
    }

    [Fact]
    public void Strategies_ListEveryApprovedStrategy() =>
        ReroutingStrategies.All.Should().BeEquivalentTo(
            "ReplaceWithIndoor", "ReplaceSameCategory", "Drop", "Postpone", "ReduceStops", "Reorder");
}