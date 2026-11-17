namespace TripMate.Application.Features.Scheduling.Explanation;

/// <summary>
/// One item of a queued itinerary: its id, its place in the itinerary, and the template sentence saved for it when
/// the itinerary was created. The background worker overwrites the item only while it still holds exactly that
/// sentence (specs/UC-10-background-explanation-spec.md, B4).
/// </summary>
public sealed record ItineraryExplanationJobItem(long ItineraryItemId, int SequenceNo, string TemplateText);

/// <summary>An itinerary waiting for its AI explanation: what to ask the provider and which items to update.</summary>
public sealed record ItineraryExplanationJob(
    long ItineraryId,
    ItineraryExplanationInput Input,
    IReadOnlyList<ItineraryExplanationJobItem> Items);

/// <summary>
/// The waiting list of background explanations (B1, B6). It lives in memory: an itinerary that is refused, or still
/// waiting when the backend restarts, keeps its template sentences.
/// </summary>
public interface IItineraryExplanationQueue
{
    /// <summary>Adds the job without waiting. False when the waiting list is full.</summary>
    bool TryEnqueue(ItineraryExplanationJob job);
}