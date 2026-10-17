namespace TripMate.Domain.Entities;

public sealed class TourCancellationPolicyRule
{
    private TourCancellationPolicyRule()
    {
    }

    public long Id { get; private set; }
    public long TourId { get; private set; }
    public Tour Tour { get; private set; } = null!;
    public int DaysBeforeDeparture { get; private set; }
    public decimal CancellationFeePercent { get; private set; }

    public static TourCancellationPolicyRule Create(Tour tour, int daysBeforeDeparture, decimal cancellationFeePercent)
    {
        ArgumentNullException.ThrowIfNull(tour);
        if (daysBeforeDeparture < 0) throw new ArgumentOutOfRangeException(nameof(daysBeforeDeparture));
        if (cancellationFeePercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(cancellationFeePercent));
        return new TourCancellationPolicyRule
        {
            Tour = tour,
            DaysBeforeDeparture = daysBeforeDeparture,
            CancellationFeePercent = cancellationFeePercent,
        };
    }
}