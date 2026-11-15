# UC-10 behavior signals: taking an edit back, and silent failures (backend)

Follows `UC-10-traveler-feedback-spec.md`. Found by the post-merge review of the UC-10 PRs on 2026-11-15.
Status: rules U1-U7 approved by the developer on 2026-11-15 (backend only; the visited mark is removed; the
dislike of a taken-back add is blocked in the recorder).

## 1. Why
1. **Undo of a removal counts as a like.** `DerivedBehaviorSignalRecorder.RecordItineraryEditAsync` records a
   `Like` (`ItineraryEdit`, weight 1.0) for every stop that is in the new list and not in the source version. Undo
   on Web and Mobile re-sends the previous stop list, so a stop removed as `NoTime` or `AlreadyVisited` and then
   put back gets a full like the traveler never gave.
2. **Undo of an add counts as a dislike.** Undo sends no removal reason, so the stop the traveler had just added
   is recorded as a `Dislike` (D16) and is not suggested for 14 days.
3. **Failures leave no trace.** `AdjustItineraryItemsCommandHandler` and `NavigationSessionMutator` build the
   recorder without a logger, so `derived_behavior_signal_failed` and `derived_visited_mark_failed` are never
   written for itinerary edits and navigation. Only scheduling-request creation passes one.

## 2. Rules
The **previous version** of the source itinerary is the source of the `Adjust` operation whose result is the
source itinerary (`ItineraryVersionOperations`). A source that no adjust produced (first version, regenerate) has
none, and every rule below falls back to today's behavior.

| # | Rule |
|---|---|
| U1 | A stop is **put back** when an adjust adds it and it was a visit in the previous version. Undo is the common case; adding the same place by hand right after removing it is treated the same. |
| U2 | A put-back stop is a `Like` only when its removal was recorded as a `Dislike` (`NotInterested`, or no reason). The like cancels that dislike and lifts the 14-day cooldown, as today. |
| U3 | A put-back stop whose removal recorded no dislike (`NoTime`, `AlreadyVisited`) records nothing. |
| U4 | A put-back stop that was removed as `AlreadyVisited` loses the visited mark that removal set: the mark is deleted when it is dated at or after that removal. An older mark (an earlier trip) is kept. |
| U5 | A stop is a **taken-back add** when an adjust removes it without a reason and it was not a visit in the previous version (the traveler added it in the edit just before). It records no `Dislike`. |
| U6 | Every other added stop is a `Like` and every other removal follows D10-D16, unchanged. A removal with a reason always follows its reason, also for a stop added in the edit just before. |
| U7 | The recorder logs its failures on every path (itinerary edit, navigation, scheduling request). |

What taking an edit back records:

| The edit taken back | Today | After |
|---|---|---|
| Removal as `NotInterested` / no reason | Like (cancels the dislike) | Like (unchanged) |
| Removal as `NoTime` | Like | nothing |
| Removal as `AlreadyVisited` | Like, visited mark stays | nothing, visited mark removed |
| Add | Dislike, hidden for 14 days | nothing |

## 3. Scope
- Backend only. No API, contract, schema or client change; older apps get the fix too. The event table stays
  append-only: no event is updated or deleted, the recorder only decides not to write one.
- `DerivedBehaviorSignalRecorder`: U1-U5 inside `RecordItineraryEditAsync`. The extra reads (the previous
  operation, its visits, its dislikes, the visited marks) follow the recorder's rule: a storage failure is logged
  and the traveler's action still succeeds; the edit then records what it records today.
- `AdjustItineraryItemsCommandHandler`, `NavigationSessionMutator` and the Reach/Skip handlers: take an optional
  `ILogger` and pass it to the recorder (U7).

## 4. Known limits
- **The like of a taken-back add stays.** The add wrote a `Like` (weight 1.0, fading with the 90-day half-life)
  and U5 writes nothing against it, so the place still counts as liked. Cancelling it without starting the
  cooldown needs a new event kind and a CHECK-constraint migration; not done here.
- Removing a place right after adding it, from an app that sends no reasons, is treated as a taken-back add even
  when the traveler meant a dislike.
- Events already stored are not corrected.

## 5. Acceptance criteria
1. Remove B as `NoTime`, then adjust with B back: no behavior event is written for B.
2. Remove B as `AlreadyVisited`, then adjust with B back: no event for B and no visited mark for B.
3. Remove B as `NotInterested` (or with no reason), then adjust with B back: one `Like` for B, as today.
4. A visited mark dated before the removal is kept when the stop is put back.
5. Add D, then adjust without D and without a reason: no `Dislike` for D.
6. Add D, then remove D as `NotInterested`: one `Dislike` for D.
7. Add a place that was not in the previous version: one `Like`, as today. Remove, without a reason, a stop that
   was in the previous version: one `Dislike`, as today.
8. A source version that no adjust produced: adds are likes and removals without a reason are dislikes, as today.
9. A failed insert during an itinerary edit or a navigation reach/skip writes the warning log and the action
   succeeds.
10. `dotnet build` has no new warnings; the Application and Infrastructure unit tests are green.
