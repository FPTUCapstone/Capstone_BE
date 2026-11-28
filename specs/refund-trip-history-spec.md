# Accepted refunds in Traveler trip history

User-approved behavior: when Traveler accepts a refund proposal or authorized Staff/Admin establishes an actual refund obligation, the booking belongs to Cancelled / Refunded and exposes Refund Details. Derive this from durable refund obligations (direct or non-rejected legacy), not from a rejected/negotiating complaint. Keep the underlying completed service/booking/payment facts unchanged: approval is not proof of transfer or receipt.

List filtering, counts, completed summary, card actions and detail must agree. Display RefundPending until completion, RefundDisputed for a disputed receipt, Refunded or PartiallyRefunded only for completed money. Suppress ticket/review actions for refund-classified records. Retain original cancellation behavior for bookings without accepted refunds. Preserve ownership, filters and pagination. No new migration or manual financial state update. Existing accepted obligations are picked up on the next read.
