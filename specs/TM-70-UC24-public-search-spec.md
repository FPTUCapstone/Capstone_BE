# TM-70 / UC-24 Public Lexical Search Specification

## Authority

- Canonical SRS: `Report3_Software-Requirement-Specification.docx`, UC-24, CR-01, PC-01–03.
- Frozen contract: `TM70-UC24-PUBLIC-LEXICAL-V1` version 1.1.
- Owner decisions: RD-01/02/03, RATING-D01, TESTDB-D01.

## Scope

Upgrade anonymous `GET /api/v1/tours` to the frozen public lexical contract. Preserve existing route and legacy fields/parameters. Do not enable or modify dev Semantic Search, E5 or Qdrant.

The request supports keyword, destination, start/end date, legacy exact-day `departureDate`, price range, duration, active category, minimum rating, sort and one-based paging. Unknown, duplicate, noncanonical and malformed parameters fail before dispatch. `departureDate` conflicts with either range bound.

Only approved/public tours of approved operators with active accounts are eligible. Hard filters and Published-only canonical rating eligibility are SQL-authoritative. Search is read-only and `no-store`.

## Rating

UC-24 uses a dedicated projection over canonical `TripReviews` where `TourId` exists and `PublicationStatus == TripReview.PublishedStatus`. Legacy `social.Reviews` is excluded. Raw average is used for inclusive minimum filter and descending sort; null sorts last; display uses one-decimal midpoint-away-from-zero rounding. Unrated is excluded only when minimum rating is present. Shared `TripReviewAggregateReader` and Detail/POI remain unchanged.

## Result and availability

The existing page and item shape is preserved and `aggregateRating` is added as an always-present nullable property. Representative schedule, availability, remaining slots and `asOfUtc` follow the frozen contract. Search does not write Tour or Booking data.

## Acceptance

- Contract/parser/validator tests cover every request field, alias conflict and MSG157/MSG158.
- SQL provider tests cover visibility, filters, deterministic sorting/paging, Published-only rating cases, availability and no mutation.
- Endpoint tests prove anonymous access, no-store and nullable response fields.
- Existing Backend suites remain green.
- TESTDB-D01 preflight and ownership manifest are required before any provider write.
