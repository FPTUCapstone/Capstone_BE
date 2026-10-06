namespace TripMate.Domain.Entities;

public sealed class VoucherApplicableTour
{
    private VoucherApplicableTour() { }
    internal VoucherApplicableTour(Voucher voucher, Tour tour) { Voucher = voucher; Tour = tour; }
    public long VoucherId { get; private set; }
    public Voucher Voucher { get; private set; } = null!;
    public long TourId { get; private set; }
    public Tour Tour { get; private set; } = null!;
}