namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>
/// Serializes owner mutations that create a successor for the same itinerary series.
/// Its lifetime is the surrounding serializable database transaction.
/// </summary>
public interface IItineraryMutationLock
{
    Task AcquireAsync(long mutationResourceId, CancellationToken cancellationToken);
}