# UC-10 — ExceedsEndTime diagnosis and confirmed extension

## Problem

`ItineraryVersionService.EvaluateStopsAsync` passed the already-compressed input (`fullCompressedInput`) to
`FixedOrderDiagnosis.ExplainAsync`, which compresses each failing prefix again. Durations shrank twice
(~56% of the catalog duration), every prefix then fit, the diagnosis returned `null`, and the evaluation became
`Failed` with no reason. Effects:

- `GET /itineraries/{id}/suggestions`: `fitsItinerary: false`, `unfitReason: null`, `overflowMinutes: null`.
- Web disables the restaurant instead of offering "extend by X minutes".
- `PUT /itineraries/{id}/items`: Tier 2 (`Reason == ExceedsEndTime`) is skipped → `ConstraintsInfeasible`.

Second defect found while tracing the save path: Tier 2 re-evaluated with `AvailableMinutes = MaxAvailableMinutes`.
The uncompressed order then fits at once, so the saved itinerary ran far past what the traveler confirmed
(example: dialog said 8 minutes, the save extended by 75).

## Agreed rules

1. Elastic compression (≤25%, floor 30/35 minutes) is applied exactly once — in the diagnosis too.
2. `overflowMinutes` for `ExceedsEndTime` is the extension the **whole trip** needs after full elastic compression
   (the dialog says "the itinerary will be extended by X minutes").
3. Confirming the extension extends the deadline by exactly `overflowMinutes` and keeps compression, so the saved
   end time equals what the traveler confirmed.
4. If no extension up to `MaxAvailableMinutes` (72 h) fits, `overflowMinutes` is `null` and the save is rejected
   with `StopInfeasible` (reason `ExceedsEndTime`); nothing is saved.

## Out of scope

- Mobile selecting `ExceedsEndTime` suggestions with a confirm dialog (separate Mobile change).
- Web behaviour when `overflowMinutes` is `null` (FE follow-up: should keep the item disabled).
- `FixedOrderDiagnosis` itself is unchanged; its per-prefix overflow is replaced only for `ExceedsEndTime`.

## Acceptance

- Suggestion that only fits with a 3-minute extension returns `ExceedsEndTime`, `overflowMinutes = 3`.
- Saving that order succeeds and ends exactly 3 minutes past the deadline with compressed durations.
- An order that cannot fit within 72 h is rejected and not saved.
