using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>Labels the legs of a generated plan with the transport the traveler chose for them.</summary>
public static class ChosenLegModes
{
    /// <summary>
    /// A leg runs from one visit to the next visit. When a rest sits between them, the chosen mode labels both
    /// the visit's leg to the rest and the rest's leg onward.
    /// </summary>
    public static GeneratedItineraryItem[] Label(
        IReadOnlyList<GeneratedItineraryItem> orderedItems,
        IReadOnlyDictionary<(long FromPoiId, long ToPoiId), TransportMode> chosenModes)
    {
        var items = orderedItems.ToArray();
        for (var from = 0; from < items.Length; from++)
        {
            if (items[from].Kind != ItineraryItemKind.Visit || items[from].PointOfInterestId is not { } fromId)
            {
                continue;
            }

            var to = from + 1;
            while (to < items.Length && items[to].Kind != ItineraryItemKind.Visit)
            {
                to++;
            }

            if (to < items.Length
                && items[to].PointOfInterestId is { } toId
                && chosenModes.TryGetValue((fromId, toId), out var chosenMode))
            {
                for (var index = from; index < to; index++)
                {
                    items[index] = items[index] with { TransportModeToNext = chosenMode };
                }
            }
        }

        return items;
    }
}