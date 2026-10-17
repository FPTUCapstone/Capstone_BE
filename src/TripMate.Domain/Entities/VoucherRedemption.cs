using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// One-to-one record of a voucher applied to a booking, stored in commerce.VoucherRedemptions.
/// </summary>
public class VoucherRedemption : BaseEntity
{
    private VoucherRedemption()
    {
    }

    public long VoucherId { get; private set; }

    public Voucher Voucher { get; private set; } = null!;

    public long BookingId { get; private set; }

    public Booking Booking { get; private set; } = null!;

    public decimal DiscountAmount { get; private set; }

    public DateTimeOffset RedeemedAtUtc { get; private set; }

    public static VoucherRedemption Create(Voucher voucher, Booking booking, decimal discountAmount,
        DateTimeOffset redeemedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(voucher);
        ArgumentNullException.ThrowIfNull(booking);
        if (discountAmount <= 0) throw new ArgumentOutOfRangeException(nameof(discountAmount));

        return new VoucherRedemption
        {
            Voucher = voucher,
            Booking = booking,
            DiscountAmount = discountAmount,
            RedeemedAtUtc = redeemedAtUtc.ToUniversalTime(),
        };
    }
}