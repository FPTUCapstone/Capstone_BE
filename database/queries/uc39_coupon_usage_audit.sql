-- UC-39: read-only audit before deploying coupon slot release.
-- Run against the intended database and review every returned row. This script changes no data.
-- An occupied slot is a pending-payment redemption or a redemption for a booking
-- that reached payment; expired/cancelled bookings still unpaid occupy no slot.

WITH Occupied AS (
    SELECT redemption.voucher_id,
           COUNT_BIG(*) AS occupied_count
    FROM commerce.VoucherRedemptions AS redemption
    JOIN commerce.Bookings AS booking ON booking.booking_id = redemption.booking_id
    WHERE NOT (booking.status IN ('Expired', 'Cancelled')
               AND booking.payment_status = 'Unpaid')
    GROUP BY redemption.voucher_id
), ReleasedUnpaid AS (
    SELECT redemption.voucher_id,
           COUNT_BIG(*) AS terminal_unpaid_count
    FROM commerce.VoucherRedemptions AS redemption
    JOIN commerce.Bookings AS booking ON booking.booking_id = redemption.booking_id
    WHERE booking.status IN ('Expired', 'Cancelled')
      AND booking.payment_status = 'Unpaid'
    GROUP BY redemption.voucher_id
)
SELECT voucher.voucher_id,
       voucher.code,
       voucher.used_count AS stored_used_count,
       COALESCE(occupied.occupied_count, 0) AS expected_occupied_count,
       voucher.used_count - COALESCE(occupied.occupied_count, 0) AS count_difference,
       COALESCE(released.terminal_unpaid_count, 0) AS historical_terminal_unpaid_redemptions,
       voucher.usage_limit
FROM commerce.Vouchers AS voucher
LEFT JOIN Occupied AS occupied ON occupied.voucher_id = voucher.voucher_id
LEFT JOIN ReleasedUnpaid AS released ON released.voucher_id = voucher.voucher_id
WHERE voucher.used_count <> COALESCE(occupied.occupied_count, 0)
ORDER BY voucher.voucher_id;
