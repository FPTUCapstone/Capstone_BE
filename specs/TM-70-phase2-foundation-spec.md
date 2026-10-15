# TM-70 Phase 2 isolated foundation

Authorized by the 2026-10-15 autonomous implementation request. Frozen authority:
Docs UC-24 V2 addendum approved 2026-10-07. Base: bdb70d8a74002dc5c99f124c7dfa46f4a648c82b.

Scope: canonical V2 parser, normalized request and validation; independently usable
SQL date/duration/category/price sorting and response projection; anonymous active
taxonomy endpoint. No migration or production seed. The existing `/api/v1/tours`
route keeps its current parser and handler: complete V2 activation is blocked by
keyword/relevance replacement approval and shared Review/POI eligibility. New V2
infrastructure has no HTTP route and no MediatR handler registration.

Acceptance: all 12 canonical keys, ordinal key handling, single values, NFC text,
whole-VND technical maximum 9,999,999,999, independent ISO dates, MSG157/MSG158,
active category validation, dynamic sorting, page 1/size 20 and bounds. SQL
selection uses one clock snapshot, inclusive Vietnam dates and upcoming equality,
Cancelled exclusion, earliest departure then numeric schedule identity, preserves
past/Completed capacity, known identity for unknown capacity, active primary Tour
media, required nullable fields and exact pagination. Rating projection is supplied
only by a future approved provider: no legacy Review aggregation is introduced.
Price/departure sorting is prepared; keyword/rating execution explicitly blocked.

Taxonomy is anonymous, no-store, active only, English name then code ordering;
empty server taxonomy is a valid empty array. No fabricated seed categories.
