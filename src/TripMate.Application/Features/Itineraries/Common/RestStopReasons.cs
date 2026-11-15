using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>
/// The wording travelers read for a rest item. The solver labels rest items with two English codes that the
/// invariant validator pins; Web and Mobile show a stop's reason as-is, next to visit reasons that are already
/// Vietnamese, so the codes are translated where a plan leaves the solver: when it is saved and when it is returned.
/// </summary>
public static class RestStopReasons
{
    /// <summary>The solver's code for a rest at a named place.</summary>
    public const string SuggestedStopCode = "Suggested rest stop";

    /// <summary>The solver's code for free rest time with no place.</summary>
    public const string FreeTimeCode = "Free/rest time";

    public const string SuggestedStop = "Điểm nghỉ gợi ý";

    public const string FreeTime = "Nghỉ tự do";

    /// <summary>The text to show; anything that is not one of the two rest codes is returned unchanged.</summary>
    public static string? ForTraveler(ItineraryItemKind kind, string? reason) =>
        kind != ItineraryItemKind.Rest
            ? reason
            : reason switch
            {
                SuggestedStopCode => SuggestedStop,
                FreeTimeCode => FreeTime,
                _ => reason,
            };
}