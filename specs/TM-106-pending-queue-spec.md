# TM-106 follow-up — Pending Tour Queue & Review Detail (UC-60 Screens 13/14), Backend

Status: design agreed with the requester in the 2026-11-19 session ("oke"); builds on PR #188 (branch `feature/tm-106-approve-tour-post`).

## Why
Staff can approve/reject a Tour (PR #188) but cannot find which Tours are waiting, or inspect one before deciding. The Frontend already ships Screen 13 (queue) and Screen 14 (detail) at `/admin/tours/reviews`, but returns `NO_BACKEND` because Backend has no read contract. Without it, Approve is usable only by calling the API by hand.

## Goals
1. `GET /api/v1/admin/tours/pending` — paginated queue of Tours in `Pending` status.
2. `GET /api/v1/admin/tours/{tourId}/review` — everything a reviewer needs to fill the five-item checklist (content, **images**, price, policy, itinerary).

## Non-goals / anti-goals
- No writes, no schema change, no migration, no change to Approve/Reject/Publish/Unpublish.
- No operator contact data (phone, address, tax code, email) in any response.
- No invented data: fields Backend does not store (`languages`) are omitted, not faked.
- No Administrator access: Staff only, identical to Approve (the SRS Staff-vs-Administrator conflict stays open and is not decided here).

## Contract
Both endpoints: `[Authorize]`, handler enforces `Role == Staff` (same as Approve) → otherwise `AdminAccessRequired` (403). `Cache-Control: no-store`.

### Queue
Query: `PageNumber` (>=1, default 1), `PageSize` (1..100, default 20, FE uses 20), `Keyword` (optional, <=100 chars, trimmed; matches Tour title OR operator company name, `LIKE %kw%`).
Order: submitted time ascending (oldest first), then `Id` ascending (deterministic paging).
Response: repository-standard `PaginatedList<PendingTourSummaryDto>` (`items, pageNumber, pageSize, totalCount, totalPages, hasPreviousPage, hasNextPage`).

`PendingTourSummaryDto`: `tourId`, `tourName`, `operatorName` (company name), `location` (`DestinationSummary`), `category` (name or null), `submittedAtUtc`, `durationDays`, `priceVnd` (`BasePrice`), `nextDepartureAtUtc` + `nextDepartureCapacity` (earliest schedule by start time, null if none), `imageCount` (active, non-deleted media).

### Detail
`PendingTourReviewDto` = the summary fields plus: `overview` (Description), `highlights`, `includedServices[]` / `excludedServices[]` (stored text split on line breaks, trimmed, blanks dropped), `travellerRequirements`, `cancellationPolicy[]` (`daysBeforeDeparture`, `feePercent`), `schedules[]` (`startAtUtc`, `endAtUtc`, `meetingPoint`, `capacity`, `adultPrice`, `childPrice`, `infantPrice`), `itinerary[]` (`dayNo`, `sequenceNo`, `poiName`, `plannedTime`, `stayMinutes`, `notes`), `images[]` (`url`, `altText`, `caption`, `isPrimary`, ordered by `sortOrder`).
Errors: unknown id → `TourNotFound` (404); status != `Pending` → `InvalidState` (409, same code/message family as Approve).

## Decisions made by the implementer (not by the requester)
- `submittedAtUtc` = `Tour.UpdatedAtUtc`. `SubmitForReview` stamps it and a Pending tour is immutable for the operator, so it equals the submission time. No dedicated column exists.
- `durationDays` instead of FE's `durationHours`, `languages` omitted: avoids false precision / invented data. Frontend adapts in its proxy/types.
- Queue stays lean (no overview/itinerary) to keep payload and joins small; detail carries the heavy data.
- Reuse existing patterns: `PaginatedList<T>`, FluentValidation, `Result<T>` + `HandleFailure`, `AsNoTracking`.

## Acceptance
- Only `Pending` tours appear; Draft/Approved/Rejected/Inactive/Published never do.
- Keyword, ordering and paging behave as above; totals are correct.
- Detail exposes images and every checklist input; no operator contact data.
- Non-Staff (Administrator, Traveler, Operator, anonymous) cannot read either endpoint.
- Works on real SQL Server (LIKE, ordering, projection translate) — proven by a disposable-DB test.

> ## Revision 2026-11-19 — rebased on develop after TM-83 (#189) and extended instead of duplicated
> TM-83 landed `GET /api/v1/admin/tours` (queue) and `GET /api/v1/admin/tours/{id}` (operator-view detail) plus a real `Tour.SubmittedAtUtc`. This design was therefore changed:
> - **Queue:** the separate `GET .../pending` endpoint is **removed**. The TM-83 queue gains an optional `keyword` (title or operator company, literal LIKE) and the Screen 13 columns (`operatorName`, `location`, `category`, `durationDays`, `priceVnd`, `nextDepartureAtUtc`, `nextDepartureCapacity`, `imageCount`) as **additive, defaulted** members; its original contract (`id, operatorUserId, title, status, submittedAtUtc, submissionNote`, `page/pageSize/totalCount`, ordering by `SubmittedAtUtc` then `Id`) is unchanged. Response is TM-83's envelope (`items, page, pageSize, totalCount`); there is no `totalPages` (clients compute it).
> - **Detail:** `GET .../{id}/review` is **kept**, because the TM-83 detail is the operator's view and lacks what the checklist needs: image URLs, POI names, operator company name, and a Pending gate (409 otherwise).
> - **Submitted time:** now the real `SubmittedAtUtc` (the earlier `UpdatedAtUtc` stand-in is gone).
> - **Access rule:** both reads now use TM-83's rule — the caller must be an **active Staff account in the database**, not just a Staff token (a locked Staff gets 403).
> Sections below describing `GET /pending`, `PageNumber` and `PaginatedList` are superseded by this note.
