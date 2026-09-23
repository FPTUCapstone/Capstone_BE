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
}