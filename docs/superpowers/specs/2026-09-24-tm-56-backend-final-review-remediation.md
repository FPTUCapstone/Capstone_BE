# TM-56 Backend Final Review Remediation Design

## Scope

Resolve the remaining UC-10 backend review findings without changing the
published scheduling API contract.

## Decisions

- A missing TM-56-owned SQL default is a schema-contract violation. The
  migration must deterministically restore its canonical default, then verify
  its definition; a same-name wrong-shape default must fail the upgrade.
- The canonical default inventory includes TimeZoneId, RestPreference,
  MandatoryPoiIds, ReturnToStart, TransportMode, and ItineraryItem ItemKind.
- POI search and scheduling use the same equirectangular distance calculation,
  radius comparison, and inclusive boundary (`distance <= radiusKm`).
- Search ordering ends with POI ID so equal name and distance rows have stable
  pages.
- Multi-day is the product source of truth. One-day is the special case where
  start and end dates are the same. This PR retains the already-reviewed
  one-day implementation; multi-day generation is a separately planned scope.

## Verification

- SQL Server migration tests cover absent, correct-shape, same-name wrong-shape
  and missing-default repair for every owned default.
- SQL Server endpoint tests cover north, south, east and west radius boundaries
  and prove GET search and POST scheduling agree for inside, exact-boundary and
  outside POIs.
- SQL Server tests prove disabled/untrusted non-negative-cost checks are
  rejected and paging stays deterministic.
