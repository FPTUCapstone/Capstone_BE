# TM-208 — Nullable public Tour search thumbnail

Status: **spec and plan approved; implementation delivered in BE PR #29 and Docs PR #8; independent review and merge pending**

## Sources and dependency

- Jira [TM-208](https://tripmate-capstone.atlassian.net/browse/TM-208): extend
  `GET /api/v1/tours` with nullable `thumbnailUrl`, preserving TM-70 search
  behavior and a bounded projection.
- The approved Tour Catalogue Enrichment addendum, section 4.1 and invariant
  3, says the **public primary image only** may be a search thumbnail. If no
  public primary exists, return null; do not silently choose another image.
- TM-207 adds `commerce.TourMedia`, its EF mapping and operator write workflow.
  This branch starts from TM-207 commit `ca9ec3a` while PR #28 awaits merge.
  Reconcile with `develop` after TM-207 is merged, before final merge.

## Scope

Add one nullable `thumbnailUrl` field to each existing `TourSearchItemDto`
within the direct paged response of anonymous `GET /api/v1/tours`. Source it
only from Tour-owned media. Preserve all TM-70 fields, validation, destination,
date and price filters, availability calculation, title/ID ordering, page
boundaries, total count and public access. A Tour without an eligible image
still appears in results with `thumbnailUrl: null`.

Do not return gallery data, Cloudinary public ID, upload credentials, internal
storage path, or POI photos. No new endpoint, database schema change, media
mutation, publication workflow, UI change, rating or recommendation belongs
to TM-208.

## Approved decision — thumbnail selection

Jira describes selection as the "first ordered Tour photo", whereas the
approved addendum permits **only the public primary image** and explicitly
prohibits an arbitrary fallback after primary removal. The two rules produce
different results when active media exists but none is primary, or when the
primary is not first in `sort_order`.

**Confirmed resolution:** use the active primary image of an
eligible public Tour, and return null if it is absent. `sort_order` does not
override `is_primary`; there is no automatic fallback to the first active
image. The owner requested implementation after this distinction was stated.
This matches the approved addendum and the TM-206 database guarantee of zero
or one active primary per Tour. Jira's "first ordered" wording must be aligned
with the approved source of truth in the API contract; it does not authorize
an order-based fallback.

An image is eligible only when its `lifecycle_status` is `Active`, it is
primary, and its parent Tour satisfies the existing public TM-70 predicate.
TM-207 locks material edits to Approved Tours and has no separately versioned
media-approval flag. Accordingly, Jira's "approved media" means the eligible
active primary image attached to a Tour that passes TM-70's existing
Approved/published/operator predicates; TM-208 does not invent a media-level
approval state or expose a Draft Tour's media.

## Proposed contract and compatibility

- Response property: `thumbnailUrl: string | null`; present on every item.
- Non-null values come from the stored, valid HTTPS Cloudinary delivery URL.
- No change to the paged response envelope, other item properties, query
  parameters, status codes or ProblemDetails behavior.
- Adding an optional response property is compatible with TM-70 clients that
  ignore unknown JSON fields. Web/Mobile adoption is separate work; this task
  does not force them to display the new field.
- Update OpenAPI operation/schema documentation and the public API contract.

## Verification

1. Runtime response/OpenAPI tests cover the new nullable property and old
   fields, filters, anonymous access and error behavior.
2. SQL-backed tests cover no media, active primary, deleted/non-primary media,
   primary not first by `sort_order`, and no POI-photo fallback.
3. SQL-backed paging tests cover page size, stable order, total count, and a
   bounded media lookup for page IDs only (no full-gallery projection or
   per-item query loop).
4. Existing TM-70 and TM-207 tests remain green.

The approved selection/publication interpretation and implementation are
covered by `docs/TM-208-verification.md`. Independent review and merge remain
outside the implementation gate.
