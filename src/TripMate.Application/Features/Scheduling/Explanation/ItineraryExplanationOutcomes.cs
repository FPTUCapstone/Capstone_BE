namespace TripMate.Application.Features.Scheduling.Explanation;

/// <summary>
/// The outcome names written to the explanation log line, shared by the create request (inline mode) and the
/// background worker so dashboards and alerts read one vocabulary.
/// </summary>
internal static class ItineraryExplanationOutcomes
{
    public const string Success = "success";
    public const string ProviderSkipped = "provider-skipped";
    public const string Timeout = "timeout";
    public const string InvalidResponse = "invalid-response";
    public const string PersistenceFailed = "persistence-failed";

    /// <summary>Background mode: the create request put the itinerary on the waiting list.</summary>
    public const string Queued = "queued";

    /// <summary>Background mode: the waiting list was full, so the itinerary keeps its template sentences.</summary>
    public const string QueueFull = "queue-full";

    /// <summary>Background mode: the answer arrived, but no item still held its template sentence.</summary>
    public const string Stale = "stale";

    public static string FromErrorCode(string? errorCode) => errorCode switch
    {
        ExplanationProviderErrorCodes.Network => "network",
        ExplanationProviderErrorCodes.Quota => "quota",
        ExplanationProviderErrorCodes.ServerError => "server-error",
        ExplanationProviderErrorCodes.Timeout => Timeout,
        _ => InvalidResponse,
    };
}