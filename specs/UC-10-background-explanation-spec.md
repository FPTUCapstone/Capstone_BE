# UC-10 itinerary explanations written in the background (backend)

Follows TM-217 (itinerary explanations) and `UC-10-ai-ranking-tripmateai-spec.md`.
Status: approved by the developer on 2026-11-17, with these decisions: the waiting list stays in memory (B6); the
clients follow in the order Backend, Web, Mobile, one pull request each (section 6); `friendlyExplanation` takes
the place of `recommendationReason` on the stop card (section 6).

## 1. Why
The explanation model answers correctly but slowly. Measured on the team laptop on 2026-11-17 (fine-tuned
`tripmate-explainer`, no time limit):

| Itinerary | Median | Slowest |
|---|---|---|
| 3 items | 15.4 s | 19.0 s |
| 8 items | 39.1 s | 43.1 s |
| 12 items | 47.4 s | 53.1 s |

Today the explanation is written inside `POST /api/v1/scheduling-requests`, so the traveler waits for it. The
backend refuses to start with `AiExplanation:OverallTimeoutSeconds` above 30, and the Mobile app gives up after
30 seconds. A normal one-day itinerary therefore can never get an AI explanation, on any hardware of this class.

Found while preparing this spec: Web and Mobile do not show `friendlyExplanation` at all. Both show
`recommendationReason` only. Section 6 lists what the clients need; it is not part of this backend change.

## 2. Rules
| # | Rule |
|---|---|
| B1 | With `AiExplanation:BackgroundEnabled = true`, creating an itinerary never waits for the AI: the template sentences are saved and returned as today, and the response says `explanationPending: true`. |
| B2 | The AI explanation is then requested in the background, one itinerary at a time, with its own time budget `AiExplanation:BackgroundTimeoutSeconds` (default 100, allowed 1-300). The 30-second limit of the inline mode is unchanged. |
| B3 | A valid AI answer replaces the template sentence of each item of that itinerary. It is validated exactly like today (`ItineraryExplanationValidator`). |
| B4 | An item is only overwritten while it still holds the template sentence saved at creation. The queued work carries, for every item, its id and that template sentence, and the worker compares against them; an itinerary that was deleted, or an item changed meanwhile, is left alone. |
| B5 | Any failure (AI unreachable, timeout, invalid answer, save error) keeps the template sentences. It is logged with the same outcome names as today and never reaches the traveler. |
| B6 | The waiting list lives in memory and holds at most 50 itineraries. When it is full, or when the backend restarts, the itineraries not yet explained keep their template sentences. Nothing is retried later. |
| B7 | With `BackgroundEnabled = false` (the default) the behavior is exactly today's: inline when `Enabled`, template only otherwise. `Enabled = false` turns both modes off. |
| B8 | A replayed create (same Idempotency-Key) returns the sentences stored at that moment and does not queue the itinerary again. |

## 3. API
`POST /api/v1/scheduling-requests`, response: one new field.

```json
{ "itineraryId": 812, "explanationPending": true, "items": [ { "friendlyExplanation": "<template sentence>" } ] }
```

- `explanationPending` is `true` only when the itinerary was put on the waiting list (B1); otherwise `false`.
- `GET /api/v1/itineraries/{id}` is unchanged. Its `friendlyExplanation` values become the AI sentences once the
  background work has saved them. It has no "pending" flag: nothing about the waiting list is stored (B6).

## 4. Configuration
| Key | Default | Meaning |
|---|---|---|
| `AiExplanation:BackgroundEnabled` | `false` | B1. Has no effect while `AiExplanation:Enabled = false` (B7). |
| `AiExplanation:BackgroundTimeoutSeconds` | `100` | B2. Startup fails outside 1-300. |

The AI service's own limit moves with it: `LLM_TIMEOUT_SECONDS` becomes 90 (it was 9 by default and 60 on the
laptop). It stays 10 seconds below the backend's 100 on purpose (decided 2026-11-17): the AI no longer gives up
while the backend is waiting, and the backend is still listening when a slow answer arrives. No schema change, no migration.

## 5. Scope
- `CreateSchedulingRequestCommandHandler`: in background mode, skip the inline provider call and queue the work.
- A queue and a hosted worker in Infrastructure, following the existing `BackgroundService` classes; the worker
  opens its own scope and `DbContext`.
- `IItineraryExplanationProvider` / `TripMateAiExplanationProvider`: accept the caller's time budget for one call.
- Options, their validators, `appsettings.json`, `.env.example`.

Log outcomes, on the existing explanation log line. From the create request: `queued` (information) and
`queue-full` (warning), next to the existing `provider-skipped` and `persistence-failed`. From the worker, one line
per itinerary with the itinerary id and the number of items updated: `success`, `stale` (the answer arrived but no
item still held its template sentence), the existing failure names (`timeout`, `network`, `quota`, `server-error`,
`invalid-response`, `persistence-failed`), and `worker-error` for anything unexpected. No place name and no
sentence is logged.

Not in scope: regenerate and adjust (they do not call the explanation AI today), storing the waiting list in the
database, retries after a restart, any client change.

## 6. What the clients need afterwards (separate work, Web and Mobile)
Decided by the developer on 2026-11-17; Web first, then Mobile, one pull request each.

1. Show `friendlyExplanation` where `recommendationReason` is shown today. `recommendationReason` stays as the
   fallback when `friendlyExplanation` is null or empty; the two are never shown together.
2. Only after a create that answered `explanationPending: true`, load `GET /api/v1/itineraries/{id}` again a few
   times (for example after 20, 45, 75 and 110 seconds). Stop at once when a sentence changed, when the attempts are
   used up, or when the traveler leaves the detail screen. Without this the AI sentences only appear the next time
   the traveler opens the itinerary.

## 7. Known limits
- A restart or deploy of the backend drops the waiting list (B6). `develop` deploys on every merge.
- The worker handles one itinerary at a time: with the laptop's speed, ten itineraries created together take
  about eight minutes to be explained.
- Until the clients do section 6, travelers see no difference.

## 8. Acceptance criteria
1. Background mode on, AI slow (60 s): the create request answers without waiting, with template sentences and
   `explanationPending: true`; afterwards the stored sentences of that itinerary are the AI's.
2. Background mode on, AI failing (unreachable, timeout, invalid answer): the stored sentences stay the template;
   one log line with the outcome; the create request had already succeeded.
3. An item whose sentence changed before the AI answered is not overwritten; the other items are.
4. The waiting list is full: the create request succeeds with `explanationPending: false` and a warning is logged.
5. Background mode off: every existing explanation test passes unchanged, and `explanationPending` is `false`.
6. `BackgroundEnabled = true` with `Enabled = false`: nothing is queued and no AI call is made.
   `BackgroundTimeoutSeconds` outside 1-300: startup fails with a clear options error.
7. A replayed create does not queue the itinerary a second time.
8. `dotnet build` has no new warnings; the Application and Infrastructure unit tests are green.
