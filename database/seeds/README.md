# Planning POI seed data

`20260914_seed_danang_planning_pois.sql` is an idempotent, curated UC-10 seed.
It is intentionally not a catalogue of every place in Da Nang: automatic
itinerary selection must use only POIs with coordinates, a verified source and
date, recorded opening hours, realistic visit duration, and an estimated cost
or an explicit unknown cost.

Run the schema migration first, then apply the seed once to a local Docker SQL
Server database. Re-running the seed updates the same normalized POI/name and
coordinate rows and their seven daily opening-hour rows.

Coordinates and categories are derived from OpenStreetMap reference data. The
seed does not copy Google Maps/Places content, photos, ratings, or reviews.
Opening hours and admission prices are sourced from the Da Nang Tourism
Promotion Center's 2026 reference article, whose URL is stored in every seeded
POI row together with the UTC verification timestamp.

## UC-51 operator application fixtures

`20260928_seed_uc51_operator_applications.sql` is an idempotent manual seed for
development and test environments. It resets six `@tripmate.local` Tour
Operator fixtures: four independent PendingApproval records for ordinary,
500-character-boundary, whitespace-trimming, and extra manual tests, plus Approved and
Rejected records for conflict-state tests. This lets UC-51 flows run without
repeating registration or resetting a record between successful decisions.
It deliberately creates no usable passwords and is not included in the normal
schema application path. Use an existing development Administrator session to
review the seeded records.
