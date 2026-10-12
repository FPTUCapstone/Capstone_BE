# UC-13 navigation progress rules decision

## Status

**Proposed — approval pending.** This record exists because the 2026-10-12 review of
`078935ddc37514e53f8df23a077e9c3f306a9322` found that the UC-13 specification deviated from
Report 3 V2 §3.3.4 without a verifiable approval. The rules below are implemented on
`feature/phuctv-uc13-navigation-backend`; they are not approved until the approval table at the end
links to an authoritative artifact (a Jira TM ticket, meeting minutes, or a signed-off Report 3
change). If approval is refused, the fallback is to enforce planned order and terminal skips (the
revision-1 behavior), which also requires a Mobile change.

## Context

Report 3 V2 §3.3.4 (Navigate Route) states that the Traveler follows the planned route "so that
the scheduled points of interest can be reached in the planned order", that "arrival at a scheduled
item advances the navigation to the next item", and (PC-03) that "the navigation session is closed
when the last item is reached or when the Traveler stops the navigation".

The same SRS also defines, outside §3.3.4:

- BR-29: the trip FSM includes an `Exploring` state between `Navigating` and `Completed`;
- MSG41, shown when the Traveler arrives at a scheduled POI: "Welcome to {POI_Name}! Enjoy your
  visit. Estimated stay duration: {Duration} mins.";
- MSG44, a confirmation before the Traveler ends or completes the trip.

§3.3.4 does not say what happens when a Traveler cannot or will not visit a stop, visits stops in a
different order, or spends time at a stop.

On 2026-10-09 the UC-13 developer replaced the revision-1 rules (strict order, terminal skips,
automatic completion) with flexible order, skip and revisit, and the `Exploring` phase, recorded as
specification revision 2. That decision was not linked to an approval outside the specification.

## Decision

| Topic | Report 3 V2 §3.3.4 | UC-13 implementation | Relation |
| --- | --- | --- | --- |
| Visit order | Planned order; arrival advances to the next item | The next item is suggested in planned order (`nextItemId`); any `Pending` or `Skipped` item may be reached | Deviation |
| Skipping | Not specified | Any `Pending` item, mandatory or not, may be skipped after a Mobile confirmation; a `Skipped` item may be reached later | Addition |
| Visiting phase | Not specified in §3.3.4; BR-29 and MSG41 | Reaching an item enters `Exploring`; departing returns to `Navigating` | Clarification |
| Automatic completion | PC-03: closed when the last item is reached | The session closes automatically, with `RouteFinished`, on the reach that leaves every item `Reached` | Aligned |
| Traveler stop | PC-03: closed when the Traveler stops | Always `TravelerStopped`, including when only skipped items remain or nothing was reached | Aligned |
| Expiry | Not specified | `lastPlannedDeparture + ExpiryGracePeriod`, closes with `Expired` | Addition |
| `nextItemId` | — | Lowest-sequence `Pending` item of an open session; `null` when the session is `Completed` or only `Skipped` items remain | Clarification |

`RouteFinished` therefore always means every scheduled item was reached. A route with a skipped
item that was never reached, or with no reached item at all, ends as `TravelerStopped` or
`Expired`, never as `RouteFinished`.

## Rationale

- Travelers reorder stops in practice (for example, stop 4 turns out to be closer than stop 3).
  Under strict order the Traveler must skip stop 3, and a terminal skip loses it for the rest of the
  trip.
- The Backend cannot make a Traveler visit a place. Refusing to skip a mandatory stop leaves only
  "stop the whole navigation" as an exit, which records less accurate progress. "Mandatory" stays
  a planning constraint (BR-26) and is shown to the Traveler; Mobile asks for confirmation.
- Staying 1–2 hours at a stop is the normal case (MSG41 announces the stay duration), so guidance
  must not jump to the next stop on arrival.
- Completion follows PC-03 exactly, so the only deviation is ordering and skipping, not when a route
  counts as finished.

## Consequences

- Mobile UC-13 keeps its skip and revisit actions. It must treat a reach response whose `state` is
  `Completed` as "route finished", and show skipped items as revisitable when `nextItemId` is
  `null` but the session is still open.
- Reporting and analytics can read `RouteFinished` as "all scheduled items reached".
- If approved, Report 3 V2 §3.3.4 should be updated to describe flexible order, skip and revisit,
  the `Exploring` phase, and expiry.

## Approval

| Role | Name | Date | Evidence (link) |
| --- | --- | --- | --- |
| Requirement owner | | | |
| Reviewer | | | |
