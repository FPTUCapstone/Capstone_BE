# Operator dashboard metrics

Approved by the user in this conversation on 19 November 2026. Scope is the four
Mobile dashboard metrics only; departures and recent-booking lists are excluded.

`GET /api/v1/operator/dashboard/metrics` requires an active, approved Tour Operator.
All data belongs to the authenticated operator; no client-supplied operator ID.

- `activeTourCount`: distinct Published tour packages with at least one Scheduled,
  future departure whose registration deadline has not passed and capacity remains.
- `monthlyBookingCount`: bookings created (`BookedAtUtc`) during the current Vietnam
  calendar month, currently Confirmed/Completed, with a successful payment transaction.
  Refunds do not erase an order if the booking remains Confirmed/Completed.
- `monthlyNetAmount`: the existing revenue aggregator's Summary.NetAmount for the
  same Vietnam month. Preserve the report's payment-period and settlement/refund rules.
  Financial authority is UC-44 (`UC-44-view-revenue-report-be-spec.md`) and
  `OperatorRevenueAggregator`, not the UC-67 refund workflow. This is Settled Payout:
  settled payouts less Staff-confirmed received operator returns, within the UC-44
  successful-payment cohort. Mobile labels it "Thực nhận trong tháng" and must not
  recompute it from gross, commission or customer refunds.
- `activeCouponCount`: owned Active vouchers, start inclusive/end exclusive, with
  unlimited usage or usage remaining.

Response also includes `asOfUtc`, `monthStart`, and `monthEndExclusive` (ISO dates).
Use the injected clock and UTC+07:00 boundaries. Zero is valid; faults must not become
successful zero-valued metrics. No writes, migrations, commission/refund changes,
lists, or APK builds are part of this change.
