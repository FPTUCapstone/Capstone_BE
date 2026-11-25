# TM-210 and TM-211 public Tour reviews

The 2026-11-15 master request authorizes completing original SRS 3.5.3 and 3.7.2, BR-91–96. This supersedes the older UC-26 scope decision that deferred the published list, only for this requested delivery.

Reuse `PublicTourRatingProjection` for Detail's existing `rating.average/count` contract. Only canonical `TripReviews` with exact TourId and Published status contribute. Legacy `social.Reviews` lack publication provenance and cannot enter the public summary. Do not change the unrelated shared legacy/POI reader, writers, schema or rounding helper.

Add `reviews` to TourDetailDto: public review ID, explicit privacy-safe display name, overall rating, title, content, created and updated UTC timestamps. No booking/user IDs, profile email, policy internals or media storage identifiers. Return confirmed published rows ordered newest first, ID descending as tie breaker. Hidden Tours retain the identical 404. Search remains lightweight. Existing clients may ignore the additive field.

Review eligibility, screening, one review per booking and seven-day editing remain owned by the existing TM-79 submission/edit pipeline. Read-time projection is not a second moderation system. Runtime aggregate calculation reflects edits immediately.
