using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Bookings.Common;

/// <summary>
/// Shared BR-63 pricing calculation used by both the UC-27 quote and create-booking handlers.
/// Pure calculation over already-fetched state; callers own fetching the schedule/tour/voucher
/// and any DB-backed eligibility counts (e.g. the voucher's per-user usage count).
/// </summary>
public static class TourBookingPricingCalculator
{
    public static Result<TourBookingPricingResult> Calculate(
        TourSchedule schedule,
        decimal tourBasePrice,
        IReadOnlyCollection<ParticipantType> participantTypes,
        Voucher? voucher,
        long tourId,
        int voucherRedemptionCountForTraveler,
        DateTimeOffset nowUtc)
    {
        var lines = participantTypes
            .GroupBy(type => type)
            .Select(group => new TourBookingPricingLine(
                group.Key,
                group.Count(),
                schedule.GetEffectivePrice(group.Key, tourBasePrice)))
            .OrderBy(line => line.ParticipantType)
            .ToList();

        var subtotal = lines.Sum(line => line.LineAmount);

        if (voucher is null)
            return Result.Success(new TourBookingPricingResult(lines, subtotal, 0m, subtotal, null));

        var eligibility = CheckVoucherEligibility(voucher, tourId, subtotal, voucherRedemptionCountForTraveler, nowUtc);
        if (eligibility is not null) return Result.Failure<TourBookingPricingResult>(eligibility.Value.Code, eligibility.Value.Message);

        var discount = ComputeDiscount(voucher, subtotal);
        var total = subtotal - discount;
        return Result.Success(new TourBookingPricingResult(lines, subtotal, discount, total, voucher.Code));
    }

    private static (string Code, string Message)? CheckVoucherEligibility(
        Voucher voucher, long tourId, decimal subtotal, int redemptionCountForTraveler, DateTimeOffset nowUtc)
    {
        if (voucher.Status != VoucherStatus.Active || nowUtc < voucher.ValidFromUtc || nowUtc >= voucher.ValidToUtc)
            return (TourBookingErrorCodes.VoucherNotEligible, "This voucher is not currently active.");

        if (voucher.ApplicableTours.Count > 0 && voucher.ApplicableTours.All(applicable => applicable.TourId != tourId))
            return (TourBookingErrorCodes.VoucherNotEligible, "This voucher does not apply to the selected tour.");

        if (voucher.UsageLimit.HasValue && voucher.UsedCount >= voucher.UsageLimit.Value)
            return (TourBookingErrorCodes.VoucherUsageLimitReached, "This voucher has reached its usage limit.");

        if (voucher.UsageLimitPerUser.HasValue && redemptionCountForTraveler >= voucher.UsageLimitPerUser.Value)
            return (TourBookingErrorCodes.VoucherUsageLimitReached, "You have already used this voucher the maximum number of times.");

        if (subtotal < voucher.MinOrderAmount)
            return (TourBookingErrorCodes.VoucherMinimumNotMet,
                $"This voucher requires a minimum order amount of {voucher.MinOrderAmount:0.##}.");

        return null;
    }

    private static decimal ComputeDiscount(Voucher voucher, decimal subtotal)
    {
        var rawDiscount = voucher.DiscountType switch
        {
            VoucherDiscountType.Percentage => subtotal * voucher.DiscountValue / 100m,
            VoucherDiscountType.Flat => voucher.DiscountValue,
            _ => throw new ArgumentOutOfRangeException(nameof(voucher)),
        };
        if (voucher.MaxDiscountAmount.HasValue) rawDiscount = Math.Min(rawDiscount, voucher.MaxDiscountAmount.Value);
        return Math.Min(decimal.Round(rawDiscount, 2, MidpointRounding.AwayFromZero), subtotal);
    }
}