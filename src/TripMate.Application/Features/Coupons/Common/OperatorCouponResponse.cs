using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Coupons.Common;

public sealed record OperatorCouponResponse(long CouponId, long OwnerOperatorUserId, string Code, string Name,
    VoucherDiscountType DiscountType, decimal DiscountValue, decimal? MaxDiscountAmount,
    decimal MinOrderAmount, int? UsageLimit, int? UsageLimitPerUser, int UsedCount,
    DateTimeOffset ValidFromUtc, DateTimeOffset ValidToUtc, VoucherStatus Status,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, IReadOnlyList<long> ApplicableTourIds)
{
    public static OperatorCouponResponse From(Voucher voucher) => new(voucher.Id,
        voucher.OwnerOperatorUserId ?? throw new InvalidOperationException("Coupon owner is required."), voucher.Code, voucher.Name,
        voucher.DiscountType, voucher.DiscountValue, voucher.MaxDiscountAmount, voucher.MinOrderAmount,
        voucher.UsageLimit, voucher.UsageLimitPerUser, voucher.UsedCount, voucher.ValidFromUtc,
        voucher.ValidToUtc, voucher.Status, voucher.CreatedAtUtc, voucher.UpdatedAtUtc,
        voucher.ApplicableTours.Select(item => item.TourId).OrderBy(id => id).ToArray());
}