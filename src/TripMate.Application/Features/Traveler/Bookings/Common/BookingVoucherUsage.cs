using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Bookings.Common;

public static class BookingVoucherUsage
{
    /// <summary>Call exactly once after a successful pending-to-terminal transition,
    /// inside the same transaction. The retained redemption is not a release marker.</summary>
    public static async Task ReleaseAsync(IApplicationDbContext dbContext, Booking booking,
        CancellationToken cancellationToken)
    {
        if (booking.Status is not (BookingStatus.Expired or BookingStatus.Cancelled) ||
            booking.PaymentStatus != PaymentStatus.Unpaid)
            return;
        if (booking.VoucherId is not long voucherId)
            return;

        bool redeemed = await dbContext.VoucherRedemptions.AsNoTracking()
            .AnyAsync(redemption => redemption.BookingId == booking.Id && redemption.VoucherId == voucherId,
                cancellationToken);
        if (!redeemed)
            return;

        Voucher voucher = await dbContext.Vouchers.SingleAsync(candidate => candidate.Id == voucherId,
            cancellationToken);
        voucher.ReleaseUsage();
    }
}