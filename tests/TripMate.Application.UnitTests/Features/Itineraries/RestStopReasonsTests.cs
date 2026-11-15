using FluentAssertions;

using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Itineraries;

/// <summary>
/// Decision 2026-11-11: travelers read the reason of a stop as-is, and visit reasons are already Vietnamese, so the
/// two rest reasons are shown in Vietnamese too. The solver keeps its English codes (the invariant validator pins them).
/// </summary>
public sealed class RestStopReasonsTests
{
    private static readonly DateTimeOffset Arrival = new(2026, 11, 11, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Free/rest time", "Nghỉ tự do")]
    [InlineData("Suggested rest stop", "Điểm nghỉ gợi ý")]
    public void ForTraveler_TranslatesTheTwoSolverCodesOfARestItem(string solverCode, string shown)
    {
        RestStopReasons.ForTraveler(ItineraryItemKind.Rest, solverCode).Should().Be(shown);
    }

    [Theory]
    [InlineData(ItineraryItemKind.Rest, "Some other reason")]
    [InlineData(ItineraryItemKind.Rest, null)]
    [InlineData(ItineraryItemKind.Visit, "Free/rest time")]
    [InlineData(ItineraryItemKind.Visit, "Hợp sở thích của bạn")]
    public void ForTraveler_LeavesEverythingElseUntouched(ItineraryItemKind kind, string? reason)
    {
        RestStopReasons.ForTraveler(kind, reason).Should().Be(reason);
    }

    [Theory]
    [InlineData("Free/rest time", "Nghỉ tự do")]
    [InlineData("Suggested rest stop", "Điểm nghỉ gợi ý")]
    public void SavedRestItem_CarriesTheVietnameseReason(string solverCode, string shown)
    {
        var rest = new GeneratedItineraryItem(
            SequenceNo: 2,
            PointOfInterestId: null,
            PointOfInterestName: null,
            Kind: ItineraryItemKind.Rest,
            PlannedArrivalUtc: Arrival,
            PlannedDepartureUtc: Arrival.AddMinutes(30),
            TravelDurationToNextMinutes: null,
            EstimatedCost: null,
            IsMandatory: false,
            RecommendationReason: solverCode);

        GeneratedItineraryItemMapper.ToEntity(rest, isMandatory: false).RecommendationReason.Should().Be(shown);
    }

    [Fact]
    public void SavedVisit_KeepsItsReason()
    {
        var visit = new GeneratedItineraryItem(
            SequenceNo: 1,
            PointOfInterestId: 7,
            PointOfInterestName: "Museum",
            Kind: ItineraryItemKind.Visit,
            PlannedArrivalUtc: Arrival,
            PlannedDepartureUtc: Arrival.AddMinutes(60),
            TravelDurationToNextMinutes: null,
            EstimatedCost: null,
            IsMandatory: false,
            RecommendationReason: "Hợp sở thích của bạn");

        GeneratedItineraryItemMapper.ToEntity(visit, isMandatory: false).RecommendationReason
            .Should().Be("Hợp sở thích của bạn");
    }
}