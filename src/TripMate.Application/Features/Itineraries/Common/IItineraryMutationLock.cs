namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>
/// Serializes owner mutations that create a successor for the same itinerary.
/// Its lifetime is the surrounding serializable database transaction.
/// </summary>
public interface IItineraryMutationLock
{
    Task AcquireAsync(long itineraryId, CancellationToken cancellationToken);
}