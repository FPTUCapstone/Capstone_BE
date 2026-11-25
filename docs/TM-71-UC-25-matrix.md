# TM-71 / UC-25 implementation and test matrix

Baseline date: 2026-11-13 (Asia/Saigon). Source of truth: original `Report3_Software-Requirement-Specification.docx`, UC-25/UC-09/UC-26/CR-01 plus approved D1-D7.

| Requirement | Backend implementation/test | Web implementation/test | Mobile implementation/test | Status |
|---|---|---|---|---|
| Active Traveler only | `ActiveTraveler` policy; endpoint tests cover inactive/role rejection | Authenticated token required; 401/403 fail closed | Auth interceptor; Backend remains authority | PASS contract; live client auth E2E pending |
| Strict score `>80.00` | scorer + SQL boundary tests | parser rejects 80.00, accepts 80.01 | parser/repository reject 80.00, accept 80.01 | PASS |
| Approved/published/bookable and current availability | `TourVisibility`, fresh serializable projection, schedule/slot tests | rejects unavailable/missing schedule/non-positive slots | same defensive validation | PASS Backend/contract |
| POST rereads schedule, price, slots, time | refresh final projection + SQL tests (old schedule full/new schedule, 8→1, time drift) | displays returned current fields | displays returned current fields | PASS |
| Score order and fixed 20-page pagination | deterministic SQL order/page/count tests | server page controls and total count | server page controls and total count | PASS component/contract |
| First screen open | GET with `criteria:null` then deduplicated POST | implemented and service-tested | implemented and data-source-tested | PASS mocked contract |
| Explicit refresh after criteria/preferences | POST current screen criteria; explicit-only score recomputation | Refresh button posts current filters | pull refresh; returning from Preferences triggers POST | PASS component/contract |
| D7 reasons | POST/GET always non-null `[]`; OpenAPI tests | parser requires array; explanation hidden when empty | parser requires array; explanation hidden when empty | PASS |
| Missing interest tags | 422 `tour_recommendation.interest_tags_required` | actionable Travel Preferences link | actionable Travel Preferences route | MSG183 + Travel Preferences; current SRS confirmed by Owner |
| No result | 200 empty page | MSG64 + Tour Search link | MSG64 + Tour Search route | PASS |
| MSG127 retains displayed list | error leaves server snapshot unchanged | state preserves previous response | Cubit preserves prior recommendations; test | PASS |
| Select recommendation continues UC-26 | no Booking/preference mutation | Tour Detail link | Tour Detail route | PASS component routing |
| Atomic snapshot/log rollback | SQL Server rollback test | N/A | N/A | PASS |
| Catalogue memory/query bound | batches of 100, tracker cleared, no cap, SQL query-count test | N/A | N/A | PASS |
| Concurrent refresh/latest snapshot | snapshot identity/request-order SQL tests | in-flight POST dedupe | in-flight POST dedupe test | PASS |
| Live Web → API → SQL | SQL disposable seeded with 23 tours | Chrome verified first GET/POST, pagination and return, MSG183/64/127, refresh, sold-out exclusion, UC-26 and unchanged Booking/profile state | N/A | PASS |
| Live Mobile → API → SQL | Same API and disposable SQL database | N/A | APK verified login, first refresh, MSG183/64/127, preference refresh, 20/3 pagination with page restored after UC-26, sold-out exclusion, unchanged Booking/profile | PASS |

Known contract limitations remain binding: GET without a snapshot is empty and therefore clients initiate POST on first open; persisted scores can become stale after price/preferences change and are recomputed only by explicit refresh.
