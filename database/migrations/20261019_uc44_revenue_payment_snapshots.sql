/* UC-44: preserve the accounting event time and commission rate that applied to a successful payment. */
IF COL_LENGTH('payment.PaymentTransactions', 'succeeded_at') IS NULL
    ALTER TABLE payment.PaymentTransactions ADD succeeded_at datetime2 NULL;
IF COL_LENGTH('payment.PaymentTransactions', 'commission_rate_applied') IS NULL
    ALTER TABLE payment.PaymentTransactions ADD commission_rate_applied decimal(5,2) NULL;
GO
UPDATE pt
SET succeeded_at = COALESCE(pt.updated_at, pt.created_at),
    commission_rate_applied = op.commission_rate
FROM payment.PaymentTransactions pt
JOIN commerce.Bookings b ON b.booking_id = pt.booking_id
JOIN commerce.TourSchedules ts ON ts.tour_schedule_id = b.tour_schedule_id
JOIN commerce.Tours t ON t.tour_id = ts.tour_id
JOIN dbo.OperatorProfiles op ON op.user_id = t.operator_user_id
WHERE pt.status = 'Success' AND (pt.succeeded_at IS NULL OR pt.commission_rate_applied IS NULL);
GO
