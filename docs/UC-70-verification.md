# UC-70 verification

Verified on 2026-11-05 against local refs only, with no fetch or deployment.

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

## Invariants added after review

- **Webhook body:** read only up to `MaxPayloadSizeBytes`, then HTTP 413. A signed empty or malformed body gets
  HTTP 400, an unsigned one still gets HTTP 401.
- **Stop re-authentication:** 10 attempts in 5 minutes per Operator, then HTTP 429. Each wrong password leaves a
  failed audit entry without the password.

## Deployment checklist

- Set `NEXT_PUBLIC_SIGNALR_URL` to the production Backend hub URL on the Web app. Without it the Web app only
  polls, so production realtime must not be claimed.

## Out of scope

Late payment and payment reconciliation after an emergency cancellation
are owned by the Payment module and are outside UC-70.

## Evidence

| Check | Result |
| --- | --- |
| Backend Application | 4565 passed, 0 failed, 0 skipped |
| Backend Infrastructure | 1036 passed, 0 failed, 3 skipped |
| Backend CandidatePool | 13 passed, 0 failed, 0 skipped |
| Backend Integration | 1534 passed, 0 failed, 11 skipped |
| Canonical migrations on new `TripMateUc70Canonical03` | 59/59 first run, 59/59 rerun; `payment_status` CHECK kept |
| Android | 2367 passed, 0 failed, 4 skipped; format and analyze clean; APK built |
| Web | 3100 passed, 0 failed; `tsc`, `eslint`, `next build` clean |
| Web browser smoke | 8 of 8 passed on the canonical database |
| Web `AuthorityDirective` smoke | Cancel and Stop: HTTP 200, correct states, money delta 0 |
| Android S20 on the AVD | 5 of 5 consecutive runs passed, each on fresh data |
| Android Emergency Stop 429 | `PASS_WITH_UX_LIMITATION`: HTTP 429 on the 11th attempt, no retry, password cleared, no state change; the message sits above the viewport (`UC70-MOBILE-UX`) |

Skips need external services: Redis rate limiter (9), Cloudinary (4), Qdrant (1), UC-20 live Backend (3),
dev search live Backend (1).

Boundary tests are in `Uc70RecoveryAndEvidenceEndpointTests`, `EmergencyTourCommandHandlerTests`,
`EmergencyTourSqlServerTests` and `EmergencyTourMigrationSqlServerTests`.

## Final APK

Android code did not change after this run, so the APK and its hash stay valid. On the final Backend, S20 and
the Emergency Stop 429 were re-run on the device; the other rows were not.

APK with SHA-256 starting `790cebda8fcc2c8b`, checked by hash on AVD `Pixel_10_Pro_XL`, passed all 21
rows of `plans/cross-module/uc70-final-apk-acceptance.md` in the workspace, including the 21 device
scenarios and real FCM. The database showed no Refund or payout change after any decision.

## Open

`UC70-MOBILE-UX` and `UC70-TEST-FLAKE` are tracked separately and do not block the merge.

UC-70 has no refund handoff by design: refunds follow the booking cancellation policy outside UC-70.

The Tomorrow.io production webhook is verified locally only (`LOCAL_PASS`: documented signature vector,
documented payloads, signed endpoint to review end to end on SQL Server). A real delivery has not been
received yet; the staging gate in `docs/UC-70-tomorrow-webhook-runbook.md` must pass before production.
