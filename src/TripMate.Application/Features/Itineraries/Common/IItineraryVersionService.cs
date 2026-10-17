using TripMate.Application.Common.Models;
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

    Task<Result<IReadOnlyList<StopsEvaluation>>> EvaluateStopVariantsAsync(
        SchedulingRequest request,
        IReadOnlyList<IReadOnlyList<ItineraryStop>> variants,
        CancellationToken cancellationToken,
        int? availableMinutesOverride = null);
}