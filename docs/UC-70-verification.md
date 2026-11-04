# UC-70 verification

Verified on 2026-11-04 against local refs only, with no fetch, commit or deployment.

| Repository | Base |
| --- | --- |
| Backend | `f6a0a498` |
| Web | `4702f4f` |
| Android | `896bcf1` |

## Scope

UC-70 records a Tour Operator's manual decision: `CancelBeforeDeparture`, or `EmergencyStopActive`
on a running tour. It updates TourSchedule, Booking and TripSession, and stores the cancellation
context, evidence, audit entry and notifications.

Payment and refund handling are managed separately under the applicable booking cancellation
policy. UC-70 creates no `Refund` or `RefundAllocation`, calculates no refund, payout or commission,
and calls neither UC-42 nor UC-71. Weather alerts only create reviews for the Operator.

| Decision | TourSchedule | Booking | TripSession |
| --- | --- | --- | --- |
| `CancelBeforeDeparture` | `Cancelled` | `Cancelled` | linked open session `Interrupted` |
| `EmergencyStopActive` | `EmergencyStopped` | `Interrupted` | linked open session `Interrupted` |

An open session without an exact booking link blocks both decisions with `TripSessionLinkUnverified`.

## Evidence

| Check | Result |
| --- | --- |
| Backend Integration | 1526 passed, 0 failed, 11 skipped |
| Backend Application | 4564 passed, 0 failed |
| Backend Infrastructure | 1036 passed, 0 failed, 3 skipped |
| CandidatePool | 13 passed, 0 failed |
| Canonical migrations on new `TripMateUc70Canonical03` | 59/59 first run, 59/59 rerun; `payment_status` CHECK kept |
| Android | 2367 passed, 0 failed, 4 skipped; format and analyze clean; APK built |
| Web | 3099 passed, 0 failed; `tsc`, `eslint`, `next build` clean |
| Web browser smoke | 8 of 8 passed on the canonical database |

The Backend ran alone, then Android, then Web. Skips need external services: Redis rate limiter (9),
Cloudinary (4), Qdrant (1), UC-20 live Backend (3), dev search live Backend (1).

Boundary tests are in `Uc70RecoveryAndEvidenceEndpointTests`, `EmergencyTourCommandHandlerTests`,
`EmergencyTourSqlServerTests` and `EmergencyTourMigrationSqlServerTests`.

## Final APK

APK with SHA-256 starting `790cebda8fcc2c8b`, checked by hash on AVD `Pixel_10_Pro_XL`, passed all 21
rows of `plans/cross-module/uc70-final-apk-acceptance.md` in the workspace, including the 21 device
scenarios and real FCM. The database showed no Refund or payout change after any decision.

## Open

`BLOCKED_UC42_HANDOFF_CONTRACT`: UC-42's `RefundProcessor` only processes existing `Refund` rows, so no
contract yet picks up a booking cancelled or interrupted by UC-70. `Booking.ApplyRefund` also still
accepts only `Cancelled`. UC-70 does not invent this contract; UC-42 owns it.
