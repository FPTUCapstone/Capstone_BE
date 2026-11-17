# UC-10 traveler feedback: removal reason, favorites and visited places (backend)

Follows `UC-10-behavior-learning-spec.md` (item 6). Decisions D10-D20 were taken by the developer on 2026-11-11.

## 1. Why
Item 6 learns from what travelers do, so it guesses:
- a stop removed for lack of time counted as a dislike and was blocked for 14 days;
- a place that is never suggested could never be liked, so the profile learned what to avoid much better than
  what to look for;
- a place the traveler had just been to was suggested again the next day.

The clients now ask (Web + Mobile): why a stop is removed, a heart on the place detail page, and thumbs for
visited stops at the end of a trip (Mobile).

## 2. Rules
| # | Rule |
|---|---|
| D10 | Removing a stop asks one reason: `NotInterested` ("Không thích"), `NoTime` ("Không đủ thời gian"), `AlreadyVisited` ("Đã đi rồi"). |
| D15 | Only `NotInterested` is a dislike. `NoTime` records nothing. |
| D16 | A removal without a reason (older app, or "replace") stays a dislike, as in item 6. |
| D19 | `AlreadyVisited` marks the place as visited: no like, no dislike, and it is not suggested again for 14 days. |
| D20 | A stop reached while navigating rests for 14 days too. The rest ends when the traveler chooses the place again afterwards (adds it by hand, marks it mandatory, likes or favorites it); a thumbs-up for the visit does not end it. When fewer than 5 other places would be left, visited places are suggested again; places removed as `NotInterested` stay hidden. |
| D12 | A traveler can mark a place as favorite and unmark it, any number of times. |
| D17 | A favorite is an explicit like (weight 1.0) that does not fade while the heart is on. In the cooldown rules it is a like dated when the heart was tapped, so the latest signal still wins. |
| D13 | Trip-end thumbs use the existing `POST /api/v1/recommendation-feedback` with `source = Itinerary`. |

What each removal reason does:

| Reason | Hidden for 14 days | Counts as a dislike |
|---|---|---|
| `NotInterested` | yes | yes |
| `AlreadyVisited` | yes | no |
| `NoTime` | no | no |

## 3. API

### 3.1 `PUT /api/v1/itineraries/{itineraryId}/items`
New optional field:
```json
{ "stops": [{ "poiId": 12 }], "removedStops": [{ "poiId": 34, "reason": "NoTime" }] }
```
- 400 when: an entry is null, the reason is unknown, more than 20 entries, a `poiId` is not positive, a `poiId`
  appears twice, or a `poiId` is also in `stops`.
- An entry for a place that was not a visit of the current version is ignored.
- The field is not part of the idempotency hash: a retry with the same key and stops replays the first result.

### 3.2 `/api/v1/pois/{poiId}/favorite` (Traveler only)
| Method | Result |
|---|---|
| `GET` | 200 `{ "poiId": 12, "isFavorite": false }` |
| `PUT` | 200 `{ "poiId": 12, "isFavorite": true }`; 404 `feedback.poi_not_found` when the place does not exist or is not active |
| `DELETE` | 200 `{ "poiId": 12, "isFavorite": false }`, also when it was not a favorite or the place was deactivated |

### 3.3 `POST /api/v1/recommendation-feedback`
Unchanged for clients. The sources only the backend writes (`ItineraryEdit`, `Navigation`, `SchedulingRequest`)
are now rejected with 400; before, such a request failed later with a server error.

## 4. Data and configuration
`database/migrations/20261112_uc10_traveler_feedback.sql` creates two tables, each with one row per traveler
and place. Additive, safe to rerun.
- `social.PoiFavorites` (`favorite_id`, `traveler_user_id`, `poi_id`, `created_at_utc`)
- `social.PoiVisitedMarks` (`visited_mark_id`, `traveler_user_id`, `poi_id`, `marked_at_utc`)

Until it is applied the favorite endpoints fail and an `AlreadyVisited` removal is not remembered
(`derived_visited_mark_failed`); itinerary generation keeps working without both tables
(`behavior_table_unavailable`).

`Personalization:VisitedStopCooldownDays` (default 14, 0 = off) sits next to `RemovedStopCooldownDays`.

## 5. Code
- `StopRemovalReason`, `PoiFavorite`, `PoiVisitedMark` (Domain).
- `AdjustItineraryItemsCommand.RemovedStops` + validator; `DerivedBehaviorSignalRecorder.RecordItineraryEditAsync`
  skips the Dislike for `NoTime` and `AlreadyVisited` and saves the visited mark for `AlreadyVisited`.
- `Features/RecommendationFeedback/Favorites`: `GetPoiFavoriteQuery`, `SetPoiFavoriteCommand`.
- `BehaviorProfileBuilder`: favorites in the profile and in the cooldown; `VisitedRecently(Async)` for D19/D20.
- `PoiRankingOrchestrator`: hides removed and visited places, visited ones first to come back.
- Nothing under `Features/Scheduling/Common`, `Csp` or `Routing` changes, so the solver-comparison report stays valid.

## 6. Known limits
- A stop removed with `NoTime` and then restored with undo counts as a like (an added stop is a like), where
  before the two cancelled out.
- A favorite always weighs 1.0: a place removed with `NotInterested` while its heart is on nets to zero in the
  profile, and the cooldown still applies because the removal is the later signal.
- Tapping a heart that is already on does not end a rest: only a heart turned on after the visit does.
- The learning benchmark (`weighted-gate --behavior-learning`) does not model D20: its simulated traveler plans
  the same city every day, so its numbers describe the removal cooldown only.
- No endpoint lists a traveler's favorites; no screen needs one yet.

## 7. Verification
Test classes: `DerivedBehaviorSignalRecorderTests`, `AdjustItineraryItemsCommandValidatorTests`,
`PoiFavoriteHandlersTests`, `BehaviorProfileBuilderTests`, `PoiRankingOrchestratorTests`,
`PoiFavoriteEndpointTests`, `Review2EndpointTests` and `PoiFavoritesSqlServerTests` (SQL Server: migration,
unique keys, profile before the migration).

Live check on 2026-11-11 against a database copy, with the real Web app (headless Chromium) and the Mobile app
on an Android emulator: a stop removed as `NoTime` came back in the next trip; stops removed as `NotInterested`
or `AlreadyVisited`, and a stop reached while navigating, did not.
