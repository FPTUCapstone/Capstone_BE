namespace TripMate.Domain.Entities;

/// <summary>Ordered region selected by the tour operator for a tour.</summary>
public class TourDestination
{
    private TourDestination()
    {
    }

    public long TourId { get; private set; }

    public Tour Tour { get; private set; } = null!;

    public long DestinationId { get; private set; }

    public Destination Destination { get; private set; } = null!;

    public int SequenceNo { get; private set; }

    internal static TourDestination Create(Tour tour, long destinationId, int sequenceNo)
    {
        ArgumentNullException.ThrowIfNull(tour);
        if (destinationId <= 0) throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (sequenceNo <= 0) throw new ArgumentOutOfRangeException(nameof(sequenceNo));
        return new TourDestination { Tour = tour, DestinationId = destinationId, SequenceNo = sequenceNo };
    }
}