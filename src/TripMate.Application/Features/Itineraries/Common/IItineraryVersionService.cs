using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Itineraries.Common;

public interface IItineraryVersionService
{
    Task<Result<Itinerary>> CreateRegeneratedVersionAsync(
        Itinerary source,
        SchedulingRequest request,
        CancellationToken cancellationToken);

    Task<Result<Itinerary>> CreateAdjustedVersionAsync(
        Itinerary source,
        SchedulingRequest request,
        IReadOnlyList<long> orderedVisitPoiIds,
        CancellationToken cancellationToken);

    Task<Result<Itinerary>> CreateAdjustedVersionAsync(
        Itinerary source,
        SchedulingRequest request,
        IReadOnlyList<ItineraryStop> stops,
        CancellationToken cancellationToken);

    /// <param name="anchor">
    /// UC-15: when given, evaluates the remaining route from the anchor's time and point (it takes precedence over
    /// <paramref name="availableMinutesOverride"/>); when null the behaviour is unchanged.
    /// </param>
    Task<Result<IReadOnlyList<StopsEvaluation>>> EvaluateStopVariantsAsync(
        SchedulingRequest request,
        IReadOnlyList<IReadOnlyList<ItineraryStop>> variants,
        CancellationToken cancellationToken,
        int? availableMinutesOverride = null,
        EvaluationAnchor? anchor = null);

    /// <summary>
    /// UC-15: builds the successor version of a trip that is being navigated, in the same scheduling series, without
    /// saving. It holds copies of <paramref name="keptItems"/> (stops already reached or skipped, original numbers
    /// and times) followed by the items of <paramref name="remainingPlan"/> numbered from
    /// <paramref name="firstNewSequenceNo"/>. The new version is Active and <paramref name="source"/> is not changed.
    /// </summary>
    Task<Result<Itinerary>> CreateReroutedVersionAsync(
        Itinerary source,
        SchedulingRequest request,
        IReadOnlyList<ItineraryItem> keptItems,
        GeneratedItineraryPlan remainingPlan,
        int firstNewSequenceNo,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
}