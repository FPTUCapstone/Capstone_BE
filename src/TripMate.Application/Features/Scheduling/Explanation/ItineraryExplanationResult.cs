namespace TripMate.Application.Features.Scheduling.Explanation;

public sealed record ItineraryExplanationResult(
    IReadOnlyCollection<ItineraryExplanationItemResult> Items);

public sealed record ItineraryExplanationItemResult(
    int SequenceNo,
    long? PoiId,
    string FriendlyExplanation);