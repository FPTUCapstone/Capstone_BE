# UC-10 TripMate AI Provider — Implementation Plan

## 1. Document Control and Status

| Field | Value |
| --- | --- |
| Plan ID | `UC-10-tripmate-ai-provider` (provisional; spec Q1) |
| Spec | [`specs/UC-10-tripmate-ai-provider-spec.md`](../specs/UC-10-tripmate-ai-provider-spec.md) (implemented and verified) |
| Status | **Done 2026-10-12**: T1–T8 complete, the runbook approved and committed. Approved the same day (spec §6.1 D9: A1, A2, D3 extension, Q8 Option A). |
| Date | 2026-10-12 |
| Baseline | `origin/develop` @ `31f9a5a`; merged with `origin/develop` @ `b66e22b` (PR #65, PR #56) in T8. The local `develop` ref of this clone is stale. |
| Branch and worktree | `feature/phuctv-tripmate-ai-provider` at `D:\FPTUCapstone\Capstone_BE_AI` |
| Companion | `Capstone_AI`, branch `feature/phuctv-tripmate-ai-service` (`812831a`, committed, not pushed), in its own PR (spec Q3, Q4) |
| Patch | `C:\Users\han12\Downloads\CSP (2)\CSP\0003-tripmate-ai-providers (1).patch`; `P:<n>` is its line n |

Progress:

| Task | State | Evidence |
| --- | --- | --- |
| T0 | Done: spec and plan approved 2026-10-12 | spec §1, §6.1 D9 |
| T1 | Done 2026-10-12: develop still `31f9a5a`; patch applies except `.env.example`; baseline green | baseline table below |
| T2 | Done 2026-10-12: patch applied unchanged except A1; reverse check OK; 0 warnings; format OK; 26/26 patch cases; full suite 0 failed | commit `949058c` |
| T3 | Done 2026-10-12: RT1–RT7 (16 cases) green; break checks: 422→ServerError failed RT1 (422 case), catch OCE→Network failed RT2 (send cancellation), no `item is null` check failed RT5 (`[null]`), no permit release failed RT7; all reverted, patch fidelity OK | new test file |
| T3 commit | `a5ebafe` | — |
| T4 | Done 2026-10-12: E1–E10 (18 cases) green; break checks: budget once per call failed E2; retry after attempt timeout failed E3; no 503 retry failed E4 (also E2 and E7 retry-delay); case-insensitive root failed E9 (`Items`); request body in the log failed E10; all reverted, patch fidelity OK | new test file |
| T5 | Done 2026-10-12: C1–C8 (33 cases) green; Infrastructure 807 passed / 3 skipped; break checks: explanation reads `AiRanking:Provider` failed C3 (both directions; C5 for `AiExplanation` too); any scheme for TripMateAi failed C7 (`ftp://`); shipped `AiRanking:Provider` = TripMateAi failed C1; no `Enum.IsDefined` failed C6 (enabled); all reverted, patch fidelity OK | new test file |
| T6 | Done 2026-10-12 (evidence: `$env:TEMP\tripmate-ai-smoke\results-fake.md`, `results-real.md`, `s*.json`): S0–S10 pass with the fake Ollama. Deviations recorded: S7 ranking stays `success` while every embedding is cached in the AI service (`app/ranking.py:47`); S7b (Ollama stopped + AI restarted) reproduces the spec's path; with Ollama down each AI call takes ~3.85 s (request ~12 s). Real models (laptop GPU): S11 201 / 5.8 s, explanation 4.4 s; S12 needs ≥ 8 visits but the 12-POI seed fits at most 5 (201, explanation 3.8–4.8 s); direct 8-stop call 504 at 9.0 s once, then 7.1 s / 6.1 s; 30 km radius hit an OpenRouteService invalid-duration 503 (pre-existing, not AI); S13 accent restoration identical scores for all 12 POIs; S14 CSP + both providers 201 / 5,484 ms, no solver-outcome line until PR #65 | none in the repository |
| T7 | Draft 2026-10-12, awaiting the developer's approval: F5 resolved in `Capstone_AI` `812831a` (developer chose "add samples to the AI repo"): `scripts/make_backend_samples.py`, `samples/rank_request_60.json`, `explain_request_8.json`, `explain_request_12.json`, benchmark prints sample name and max; AI tests 358 passed; training generator output unchanged (hash check). Laptop GPU benchmark: rank 60 OK; explain 8 items median 5.6 s, 0 errors; 12 items 6/11 and 2/4 calls 504. Every runbook command run on the laptop: preflight in T6; Part A standalone `ai` with host override, benchmark via `--network container:` (also `--network host`), `evaluate_model.py --limit 3` (needs `--user root`); temporary AI `.env` and container removed. Whitespace check OK | `docs/operations/uc10-tripmate-ai-measurement-runbook.md` |
| T8 | Done 2026-10-12. Before the merge: format of the 18 branch `.cs` files clean, build 0 warnings, CandidatePoolReplay 13, Infrastructure 807 / 3 skipped, Application 1,779, integration 446 / 334 skipped, `git diff --check` clean. The whole-solution `dotnet format --verify-no-changes` reports whitespace errors only in files this branch does not touch (for example `GetActiveTripDetailsQueryHandler.cs`, `CreateSchedulingRequestCommandHandler.cs`); the CI-equivalent command below now diffs against the merge base. Diff against `origin/develop`: the 13 patch files, the 3 new test files, the runbook, spec, plan, the A2 orchestrator and test setups, nothing else. Merge of `origin/develop` @ `b66e22b`: no conflicts; then build 0 warnings, Infrastructure 808 / 3, Application 1,861, integration 474 / 349 skipped, Scheduling SQL Server classes 61 / 9 skipped (Redis) on a disposable SQL Server. S14 repeated on the merged code with real models: `Scheduling solver outcome csp`, CSP 20 ms then 0.8 ms, both providers `success`, 8.5 s then 3.6 s. Developer accepted L14 (12-item explanations time out about half the time on the laptop GPU). Spec §1, §6.4 (L14–L16), §8.8, §9.3 and §13.3 updated | spec, plan |
| T5a | Done 2026-10-12: O1 failed before the change (provider called once) and passes after; Application 1,779 passed. Integration against a throwaway SQL Server 2022 container: 871 passed / 10 skipped / 1 failed (`ConcurrentSameKey…`, A2 cause, fixed), then both scheduling SQL Server classes 36/36. Updated test setups (review notes): 8 `PoiRankingOrchestratorTests` inputs get token `culture`; 8 `CreateSchedulingRequestCommandHandlerTests` (14 cases) get traveler 42 interest `unmatched-interest`; `CreateSchedulingRequestRankingSqlServerTests.SeedAsync` and `CreateSchedulingRequestSqlServerTests.ConcurrentSameKey…` get the same interest. No POI matches these tokens, so base scores are unchanged | orchestrator + 4 test files |

Baseline numbers, filled in by T1:

| Gate | Command | Result |
| --- | --- | --- |
| Build | `dotnet build TripMate.slnx -c Release --no-restore --nologo` | 0 warnings (2026-10-12) |
| Tests | `dotnet test TripMate.slnx -c Release --no-build --nologo` | CandidatePoolReplay 13/0/0; Infrastructure 714/0/3; Application 1,778/0/0; Api.IntegrationTests 446/0/334 (SQL Server and Redis not configured locally; they run in T6 and CI) |

## 2. Execution Rules

- One task at a time, in order. After each task, review it against the spec, then for code quality.
  A Critical finding blocks the next task (AGENTS.md:24-25).
- Patch 0003 stays unchanged (spec D2). T2 edits no code. Anything that fails on the patched tree
  stops the plan until the developer decides.
- TDD (AGENTS.md:24). No new production code is planned. If the developer later asks for some, for
  example spec Q5 option B, its failing test comes first.
- New tests over existing patch code must pass the break check of spec §12.3: each test fails when
  the behavior it covers is broken locally, and passes again after the revert. The breaking change is
  never committed.
- No automated test calls a real endpoint (spec §12.3).
- The agent does not commit, push, create or switch branches, or stash unless the developer asks. The
  developer commits (§7) and pushes.
- Never commit `.env`, keys or tokens (AGENTS.md:105-107), and never paste them into evidence.
- Ask the developer before stopping another Docker stack or copying their local `.env` (T6).

## 3. Verification Commands

Run in PowerShell from `D:\FPTUCapstone\Capstone_BE_AI`. Restore once with
`dotnet restore TripMate.slnx`.

Focused:

```powershell
# TripMate AI tests: the patch's file and the three new files
dotnet test tests/TripMate.Infrastructure.UnitTests -c Release --no-restore --filter "FullyQualifiedName~TripMate.Infrastructure.UnitTests.TripMateAi"

# Existing AI adapter, DI and budget tests
dotnet test tests/TripMate.Infrastructure.UnitTests -c Release --no-restore --filter "FullyQualifiedName~TripMate.Infrastructure.UnitTests.AiRanking|FullyQualifiedName~TripMate.Infrastructure.UnitTests.AiExplanation|FullyQualifiedName~TripMate.Infrastructure.UnitTests.Ai."

# Scheduling application tests: orchestrator, explanation, handler
dotnet test tests/TripMate.Application.UnitTests -c Release --no-restore --filter "FullyQualifiedName~Scheduling"
```

Final (as in `plans/UC-10-csp-itinerary-solver-plan.md:38-45`):

```powershell
dotnet format TripMate.slnx --verify-no-changes --no-restore
dotnet build TripMate.slnx -c Release --no-restore --nologo
dotnet test TripMate.slnx -c Release --no-build --nologo
git diff --check
```

CI-equivalent format check of changed and new C# files (`.github/workflows/backend-tests.yml:58-75`):

```powershell
$changed = @(git diff --name-only --diff-filter=ACMR origin/develop...HEAD -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs')
if ($changed.Count -gt 0) { dotnet format TripMate.slnx --verify-no-changes --no-restore --include $changed }
```

`git diff --check` covers tracked files only. For a new, untracked file:

```powershell
Select-String -Path <new file> -Pattern '[ \t]+$'   # must print nothing
```

Without `TRIPMATE_SQLSERVER_TEST_CONNECTION` and `TRIPMATE_REDIS_TEST_CONNECTION`, the SQL Server and
Redis categories are skipped locally; record the skip counts. CI runs them without skips
(`.github/workflows/backend-tests.yml:35-37`, `:77-125`).

## 4. Tasks

### T0 — Spec and plan (documentation only)

- **Goal:** the developer approves the spec, then this plan.
- **Files (new):** `specs/UC-10-tripmate-ai-provider-spec.md`, `plans/UC-10-tripmate-ai-provider-plan.md`.
- **Steps:**
  1. The developer reviews the spec and answers or defers each open question (spec §16). Record each
     decision with its date in the spec, and set the spec Status to Approved.
  2. The same for this plan.
  3. When the official task ID is known (Q1), rename both files and update their references.
- **Verify:** `git status --short` lists only the two files; the whitespace check (§3) prints nothing
  for either.
- **Definition of Done:** both documents approved in writing; no other file changed.
- **Commit (developer):** `docs(trip): add UC-10 TripMate AI provider spec and plan`.

### T1 — Baseline (no changes)

- **Goal:** a green, recorded baseline before any change (AGENTS.md:22).
- **Files:** none.
- **Steps:**
  1. Check the starting point:
     - `git fetch origin`, then `git rev-parse origin/develop` still prints `31f9a5a…`;
     - `git log --oneline origin/develop..HEAD` shows at most the T0 commit;
     - `git status --short` shows at most the two T0 files, if they are not committed yet.
  2. Run `dotnet restore TripMate.slnx` and the final build and test commands (§3). Record the warning
     count, and passed/failed/skipped per test project, in §1.
  3. Re-check the patch:
     ```powershell
     $patch = "C:\Users\han12\Downloads\CSP (2)\CSP\0003-tripmate-ai-providers (1).patch"
     git apply --check -v $patch                           # fails on .env.example only
     git apply --check -v --exclude=.env.example $patch    # exit code 0
     ```
- **Verify:** the commands of steps 2 and 3.
- **Definition of Done:** 0 failed tests; numbers recorded; the apply check matches spec §5.6.
- **STOP:**
  - a red baseline: report it and do not start T2;
  - `origin/develop` has moved past `31f9a5a`: ask the developer whether to bring the branch up to
    date first. Spec §5.6 and step 3 must then be redone on the new base.

### T2 — Apply patch 0003 unchanged (no code edits)

- **Goal:** the patch's 13 files in the working tree, identical to the patch, with `.env.example`
  merged by hand as spec A1 says.
- **Files:**
  - modified: `.env.example`, `docker-compose.yml`, `src/TripMate.Api/appsettings.json`,
    `src/TripMate.Infrastructure/DependencyInjection.cs`,
    `src/TripMate.Infrastructure/AiExplanation/ExplanationProviderOptions.cs`,
    `src/TripMate.Infrastructure/AiExplanation/ExplanationProviderOptionsValidator.cs`,
    `src/TripMate.Infrastructure/AiRanking/PoiRankingProviderOptions.cs`,
    `src/TripMate.Infrastructure/AiRanking/PoiRankingProviderOptionsValidator.cs`;
  - new: `src/TripMate.Infrastructure/Ai/AiProviderKind.cs`,
    `src/TripMate.Infrastructure/Ai/AiProviderRegistration.cs`,
    `src/TripMate.Infrastructure/TripMateAi/TripMateAiExplanationProvider.cs`,
    `src/TripMate.Infrastructure/TripMateAi/TripMateAiPoiRankingProvider.cs`,
    `tests/TripMate.Infrastructure.UnitTests/TripMateAi/TripMateAiProviderTests.cs`.
- **Steps:**
  1. Apply every hunk except the `.env.example` one:
     ```powershell
     $patch = "C:\Users\han12\Downloads\CSP (2)\CSP\0003-tripmate-ai-providers (1).patch"
     git apply --exclude=.env.example $patch
     ```
  2. In `.env.example`, insert P:52-64 after line 29 (the blank line after
     `AiExplanation__MaxConcurrency=2`) and before line 30 (`# Shared Cloudinary account credentials …`).
     Take the 13 lines from the patch without their leading `+`: 12 commented lines, then one blank
     line. Change nothing else.
  3. Check fidelity:
     - `git apply --reverse --check --exclude=.env.example $patch` exits 0;
     - `git diff --numstat -- .env.example` prints `13`, `0`, `.env.example`, and the inserted lines
       equal P:52-64;
     - `git status --short` lists exactly the 13 files, 5 of them new (plus the T0 documents if they
       are not committed yet).
  4. Build, check the format of the patch's 10 C# files, then run the tests:
     ```powershell
     dotnet build TripMate.slnx -c Release --no-restore --nologo
     $patchFiles = @(
       'src/TripMate.Infrastructure/Ai/AiProviderKind.cs',
       'src/TripMate.Infrastructure/Ai/AiProviderRegistration.cs',
       'src/TripMate.Infrastructure/AiExplanation/ExplanationProviderOptions.cs',
       'src/TripMate.Infrastructure/AiExplanation/ExplanationProviderOptionsValidator.cs',
       'src/TripMate.Infrastructure/AiRanking/PoiRankingProviderOptions.cs',
       'src/TripMate.Infrastructure/AiRanking/PoiRankingProviderOptionsValidator.cs',
       'src/TripMate.Infrastructure/DependencyInjection.cs',
       'src/TripMate.Infrastructure/TripMateAi/TripMateAiExplanationProvider.cs',
       'src/TripMate.Infrastructure/TripMateAi/TripMateAiPoiRankingProvider.cs',
       'tests/TripMate.Infrastructure.UnitTests/TripMateAi/TripMateAiProviderTests.cs')
     dotnet format TripMate.slnx --verify-no-changes --no-restore --include $patchFiles
     dotnet test tests/TripMate.Infrastructure.UnitTests -c Release --no-build --filter "FullyQualifiedName~TripMate.Infrastructure.UnitTests.TripMateAi"
     dotnet test TripMate.slnx -c Release --no-build --nologo
     ```
- **Verify:** the commands of steps 3 and 4.
- **Definition of Done:**
  - the fidelity checks pass;
  - the build has no warning beyond the T1 count;
  - the format check passes;
  - the 26 TripMate AI test cases pass;
  - the full suite has 0 failures, and no existing test was edited.
- **STOP:** any failure in step 3 or 4. Do not edit patch content. Report the exact output; the
  developer decides, for example on a separate formatting commit, which would then be recorded as a
  new adaptation in spec §6.2.
- **Commit (developer):**
  - message: the patch subject without `[PATCH 3/4]` (P:4-5, joined into one line), then the body
    P:7-19, unchanged; trailers (P:21-22) as the developer chooses;
  - author date `Fri, 9 Oct 2026 16:19:46 +0700`, as the commits of patches 0001, 0002 and 0004
    (`63309cc`, `1ca8b57`, `c864aa3`) kept their patches' subjects and dates;
  - for example: `git commit --date "Fri, 9 Oct 2026 16:19:46 +0700" -F <message file>`;
  - the subject's scope `ai` is outside AGENTS.md:99. The CSP precedent kept its `scheduling` scope;
    keep `ai` unless the developer says otherwise.

### T3 — TripMateAi ranking provider tests

- **Goal:** tests RT1–RT7 of spec §12.2.
- **Files (new):** `tests/TripMate.Infrastructure.UnitTests/TripMateAi/TripMateAiPoiRankingProviderTests.cs`.
  No production file changes.
- **Steps:**
  1. Write the tests with private controlled handlers (stub, throwing, cancellation-observing, and
     throwing content), mirroring
     `tests/TripMate.Infrastructure.UnitTests/AiRanking/HttpPoiRankingProviderTests.cs`. RT7 uses a real
     `InMemoryAiProviderBudget` with a fixed `IDateTimeProvider`.
  2. Run them; they pass on the patch code.
  3. Break check, one change at a time, each reverted before the next:
     - map 422 to `ServerError` (RT1 must fail);
     - catch `OperationCanceledException` and return `Network` (RT2 must fail);
     - remove the `item is null` check (RT5 must fail);
     - remove the `finally` block that releases the permit (RT7 must fail).

     Record which tests failed.
- **Verify:** the first focused command (§3). Every break-check change was reverted:
  `git apply --reverse --check --exclude=.env.example $patch` still exits 0, and `git status --short`
  shows only the new test file beyond earlier tasks.
- **Definition of Done:** RT1–RT7 green; break checks recorded; no production change; review passed.
- **STOP:** a test exposes a real defect in patch code. Report it; no fix without the developer's
  decision.
- **Commit (developer):** `test(trip): cover TripMate AI ranking provider edge cases`.

### T4 — TripMateAi explanation provider tests

- **Goal:** tests E1–E10 of spec §12.2.
- **Files (new):** `tests/TripMate.Infrastructure.UnitTests/TripMateAi/TripMateAiExplanationProviderTests.cs`.
- **Steps:**
  1. Write the tests:
     - use the timing margins of the Gemini tests
       (`tests/TripMate.Infrastructure.UnitTests/AiExplanation/HttpItineraryExplanationProviderTests.cs:325-345`);
     - capture logs with a private logger like the one in
       `tests/TripMate.Infrastructure.UnitTests/Media/CloudinaryTourMediaStorageTests.cs:237`;
     - E2 uses a real `InMemoryAiProviderBudget` limited to one call per minute;
     - no test depends on a tight race against the overall budget.
  2. Run them; they pass.
  3. Break check, one change at a time, each reverted:
     - acquire the budget once per call instead of once per attempt (E2 must fail);
     - retry after an attempt timeout (E3 must fail);
     - remove 503 from the retry set (E4 must fail);
     - compare root property names case-insensitively (E9 must fail);
     - add the request body to the log message (E10 must fail).
- **Verify:** the first focused command (§3), and the revert checks of T3.
- **Definition of Done:** E1–E10 green; break checks recorded; no production change; review passed.
- **STOP:** as T3.
- **Commit (developer):** `test(trip): cover TripMate AI explanation retry, timeout and budget paths`.

### T5 — Provider switch, validation and DI tests

- **Goal:** tests C1–C8 of spec §12.2.
- **Files (new):** `tests/TripMate.Infrastructure.UnitTests/TripMateAi/TripMateAiConfigurationTests.cs`.
- **Steps:**
  1. Build configurations the way
     `tests/TripMate.Infrastructure.UnitTests/AiRanking/AiRankingDependencyInjectionTests.cs:133-183`
     does: baseline values, the repository's `appsettings.json` for C1, and `IStartupValidator` for
     the startup checks.
  2. Run them; they pass.
  3. Break check, one change at a time, each reverted:
     - read `AiRanking:Provider` for both features in `AiProviderRegistration` (C3 must fail);
     - accept any absolute URI scheme for TripMateAi (C7 must fail);
     - set `"Provider": "TripMateAi"` in `src/TripMate.Api/appsettings.json` (C1 must fail);
     - remove the `Enum.IsDefined` check (C6 must fail).
- **Verify:** the first two focused commands (§3), and the revert checks of T3. `appsettings.json`
  must again match the patch.
- **Definition of Done:** C1–C8 green; break checks recorded; no production change; review passed.
- **STOP:** as T3.
- **Commit (developer):** `test(trip): cover TripMate AI provider selection and startup validation`.

### T5a — No ranking call without preference tokens (A2, only if confirmed)

- **Goal:** spec §6.2 A2 and §18.2. A request without preference tokens gets the base snapshot with
  outcome `provider-skipped`, exactly as when the feature is disabled; the provider is not called.
- **Files:**
  - `src/TripMate.Application/Features/Scheduling/Personalization/PoiRankingOrchestrator.cs`
  - `tests/TripMate.Application.UnitTests/Features/Scheduling/Personalization/PoiRankingOrchestratorTests.cs`
- **Steps (TDD):**
  1. Add `BuildRankingAsync_WithNoPreferenceTokens_SkipsTheProviderAndReturnsBaseScores`: provider
     enabled, candidates present, empty token list. Expect zero provider calls, outcome
     `ProviderSkipped`, every entry's `EffectiveDesirabilityScore` equal to its base score, and one log
     line with `outcome=provider-skipped`, `fallbackUsed=False`. Run it and see it fail (today the
     provider is called).
  2. In `BuildRankingAsync`, after the provider-pool selection, return the base snapshot with
     `ProviderSkipped` when `preferenceTokens.Length == 0`, logging like the disabled branch
     (`PoiRankingOrchestrator.cs:100-115`). No other change.
  3. Run the orchestrator tests, the handler tests and the scheduling ranking integration tests; any
     existing test that expected a provider call with an empty token list is updated to give the
     traveler at least one token, and the change is listed in the review notes.
- **Verify:** the Application unit tests and
  `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestRankingSqlServerTests.cs`
  (SQL Server suite) pass.
- **Definition of Done:** the new test fails before step 2 and passes after; no change outside the two
  files except updated test setups; review passed.
- **Commit (developer):** `fix(trip): skip AI POI ranking when the traveler has no preference tokens`.

### T6 — End-to-end smoke test (no repository change)

- **Goal:** spec §13, scenarios S0–S10 with the fake Ollama, then S11–S13 with the real models on the
  laptop GPU (S11–S14), with evidence.
- **Files:** none in the repository. The worktree `.env` (git-ignored, `.gitignore:494`) and scratch
  files under `$env:TEMP\tripmate-ai-smoke\`: `compose.fake-ollama.yml`, `smoke-traveler.sql`,
  `mint_token.py`, `request.json`, and the response files.
- **Steps:**
  1. **Name conflicts.** Containers use fixed names, so containers of another Compose project block
     this run:
     ```powershell
     docker ps -a --filter "name=tripmate-" --format "table {{.Names}}\t{{.Status}}\t{{.Labels}}"
     ```
     Remove another project's containers with that project's `docker compose down` (it keeps the
     volumes), and only with the developer's consent.
  2. **`.env`.** With the developer's consent, copy their local `.env` into the worktree, then generate
     a key:
     ```powershell
     docker run --rm python:3.12-slim python -c "import secrets; print(secrets.token_urlsafe(32))"
     ```
     Set each of these keys exactly once; edit existing lines rather than appending duplicates:
     ```
     TRIPMATE_AI_API_KEY=<key>
     AiRanking__Enabled=true
     AiRanking__Provider=TripMateAi
     AiRanking__Endpoint=http://ai:8000/v1/rank-pois
     AiRanking__ApiKey=<key>
     AiRanking__RateLimitPerMinute=60
     AiExplanation__Enabled=true
     AiExplanation__Provider=TripMateAi
     AiExplanation__Endpoint=http://ai:8000/v1/explain-itinerary
     AiExplanation__ApiKey=<key>
     AiExplanation__RateLimitPerMinute=60
     SchedulingRateLimit__MaxFreshGenerationsPerMinute=60
     SchedulingRateLimit__CooldownSeconds=0
     ```
     `git check-ignore .env` must print `.env`.
  3. **Override file.** Write `$env:TEMP\tripmate-ai-smoke\compose.fake-ollama.yml`. On 2026-10-12 its
     merge was checked with `docker compose config` against a copy of the profile's `ollama` and
     `ollama-init` definitions, and the fake's start command was run once against the AI working tree.
     ```yaml
     services:
       ollama:
         image: python:3.12-slim
         volumes:
           - ${AI_REPO_PATH:-../Capstone_AI}:/src:ro
         command:
           - sh
           - -c
           - >-
             pip install -q -r /src/requirements.txt &&
             cd /src &&
             python -c "import uvicorn; from tests.fake_ollama import FakeOllama, create_fake_ollama_app;
             fake = FakeOllama(); fake.chat_mode = '${FAKE_CHAT_MODE:-ok}';
             uvicorn.run(create_fake_ollama_app(fake), host='0.0.0.0', port=11434)"
         healthcheck:
           test: ["CMD", "python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:11434/api/tags', timeout=3)"]
           interval: 5s
           timeout: 5s
           retries: 60
       ollama-init:
         image: python:3.12-slim
         entrypoint: ["true"]
     ```
     Relative paths in an override resolve against the first file's folder, so `../Capstone_AI` is
     `D:\FPTUCapstone\Capstone_AI`.
  4. **Compose checks** (spec AC7). Do not print the full `config`: it contains resolved secrets.
     ```powershell
     $o  = "$env:TEMP\tripmate-ai-smoke\compose.fake-ollama.yml"
     $dc = @('-f', 'docker-compose.yml', '-f', $o, '--profile', 'ai')
     docker compose config --services                  # sqlserver, db-init, redis, api
     docker compose --profile ai config --services     # also ollama, ollama-init, ai
     docker compose @dc config --services              # same seven services
     ```
  5. **Start.** Run `docker compose @dc up -d --build`. Repeat `docker compose @dc ps` until
     `sqlserver`, `redis`, `ollama` and `ai` are healthy, `db-init` and `ollama-init` exited with code 0,
     and `api` is running.
  6. **Data.**
     - POIs:
       ```powershell
       docker compose @dc exec sqlserver bash -lc '/opt/mssql-tools18/bin/sqlcmd -b -V 11 -C -I -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d TripMateDb -i /scripts/seeds/20260914_seed_danang_planning_pois.sql'
       ```
     - Traveler: write `smoke-traveler.sql`, copy it into the container, run it, and note the printed
       user ID. Pattern: `database/seeds/20261001_seed_uc59_active_trip_details.sql:28-29`; table:
       `database/tripmate_schema_v7.sql:217-228`.
       ```sql
       SET NOCOUNT ON;
       DECLARE @email NVARCHAR(320) = N'ai-smoke-traveler@example.invalid';
       IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE email = @email)
           INSERT dbo.Users(role, email, full_name, status)
           VALUES ('Traveler', @email, N'AI Smoke Traveler', 'Active');
       DECLARE @userId BIGINT = (SELECT user_id FROM dbo.Users WHERE email = @email);
       IF NOT EXISTS (SELECT 1 FROM dbo.TravelerProfiles WHERE user_id = @userId)
           INSERT dbo.TravelerProfiles(user_id, interest_tags_json)
           VALUES (@userId, N'["Museum","Natural attraction"]');
       SELECT @userId AS smoke_user_id;
       ```
       ```powershell
       docker cp "$env:TEMP\tripmate-ai-smoke\smoke-traveler.sql" tripmate-sqlserver:/tmp/smoke-traveler.sql
       docker compose @dc exec sqlserver bash -lc '/opt/mssql-tools18/bin/sqlcmd -b -V 11 -C -I -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d TripMateDb -h -1 -W -i /tmp/smoke-traveler.sql'
       ```
     - Token: an HS256 JWT with the claims the API issues
       (`src/TripMate.Infrastructure/Services/JwtTokenService.cs:22-29`), issuer and audience as in
       `docker-compose.yml:80-81`, signed with the base64-decoded `JWT_SIGNING_KEY`
       (`src/TripMate.Api/Program.cs:151-152`). Write `mint_token.py` (it was run once with a dummy
       key on 2026-10-12):
       ```python
       import base64, hashlib, hmac, json, os, time, uuid

       def b64url(data: bytes) -> str:
           return base64.urlsafe_b64encode(data).rstrip(b"=").decode()

       user_id = os.environ["SMOKE_USER_ID"]
       now = int(time.time())
       header = {"alg": "HS256", "typ": "JWT"}
       claims = {
           "sub": user_id, "nameid": user_id, "email": "ai-smoke-traveler@example.invalid",
           "role": "Traveler", "jti": str(uuid.uuid4()),
           "iss": "TripMate", "aud": "TripMateClients", "exp": now + 3600,
       }
       signing_input = f"{b64url(json.dumps(header).encode())}.{b64url(json.dumps(claims).encode())}"
       key = base64.b64decode(os.environ["JWT_SIGNING_KEY"])
       signature = b64url(hmac.new(key, signing_input.encode(), hashlib.sha256).digest())
       print(f"{signing_input}.{signature}")
       ```
       ```powershell
       $token = docker run --rm --env-file .env -e SMOKE_USER_ID=<user id> -v "$env:TEMP\tripmate-ai-smoke:/smoke:ro" python:3.12-slim python /smoke/mint_token.py
       ```
       `docker run --env-file` does not strip quotes. If `JWT_SIGNING_KEY` is quoted in `.env`, pass
       the value without quotes. Never print `$token`.
     - Request body, starting tomorrow at 08:00 Vietnam time from the coordinates of
       `database/seeds/20260914_seed_danang_planning_pois.sql:67`:
       ```powershell
       $startAt = (Get-Date).AddDays(1).ToString('yyyy-MM-dd') + 'T08:00:00+07:00'
       @"
       {"startAt":"$startAt","timeZoneId":"Asia/Ho_Chi_Minh","startLatitude":16.040393,"startLongitude":108.223058,
        "explorationLatitude":16.040393,"explorationLongitude":108.223058,"endPoiId":null,"returnToStart":true,
        "availableMinutes":480,"transportMode":"Car","searchRadiusKm":15,"budgetVnd":null,"mandatoryPoiIds":[],
        "restPreference":"Auto"}
       "@ | Out-File -Encoding ascii "$env:TEMP\tripmate-ai-smoke\request.json"
       ```
  7. **Scenarios** (spec §13.3). For each one, note the time, send the request with a new
     `Idempotency-Key` (keep S3's key for S4), save the response under the scenario's name, and
     collect the evidence:
     ```powershell
     $name  = 's3'
     $since = (Get-Date).ToUniversalTime().ToString('o')
     $key   = [guid]::NewGuid()
     curl.exe -s -o "$env:TEMP\tripmate-ai-smoke\$name.json" -w "%{http_code}`n" -X POST http://localhost:5000/api/v1/scheduling-requests `
       -H "Authorization: Bearer $token" -H "Idempotency-Key: $key" -H "Content-Type: application/json" `
       --data-binary "@$env:TEMP\tripmate-ai-smoke\request.json"
     docker logs tripmate-api --since $since 2>&1 | Select-String 'POI ranking provider|Itinerary explanation provider|TripMate AI explanation attempt'
     docker logs tripmate-ai --since $since 2>&1 | Select-String 'POST /v1/|rank_pois|explain_itinerary'
     ```
     Setup per scenario. After any `.env` change, recreate only the API with
     `docker compose @dc up -d --no-deps api`.
     - **S0:** `docker exec tripmate-ai python -c "import urllib.request as u; print(u.urlopen('http://127.0.0.1:8000/ready', timeout=5).read().decode())"`,
       and the same for `/health`.
     - **S1:** remove both `*__Provider` lines and set both `*__Enabled=false`. Check that Compose
       resolves both providers to `Gemini` (spec AC7) without printing the rest of the configuration:
       ```powershell
       $api = (docker compose @dc config --format json | ConvertFrom-Json).services.api.environment
       $api.AiRanking__Provider, $api.AiExplanation__Provider    # Gemini, Gemini
       ```
     - **S2:** `Provider=TripMateAi` for both features; still disabled.
     - **S3:** both features enabled.
     - **S4:** S3's request again, with S3's `Idempotency-Key`.
     - **S5:** set `AiRanking__ApiKey` and `AiExplanation__ApiKey` to another random value, leaving
       `TRIPMATE_AI_API_KEY` unchanged. Afterwards restore them and recreate the API again.
     - **S6:** `$env:FAKE_CHAT_MODE = 'not_json'; docker compose @dc up -d --no-deps ollama`. Wait until
       `ollama` is healthy and `/ready` answers 200, then run. Afterwards run
       `Remove-Item Env:FAKE_CHAT_MODE` and recreate `ollama` the same way.
     - **S7:** `docker stop tripmate-ollama`, then run. Afterwards `docker start tripmate-ollama`, and
       wait until it is healthy and `/ready` answers 200.
     - **S8:** `docker pause tripmate-ai`, then run; the request takes about 15 s longer than usual.
       Afterwards `docker unpause tripmate-ai`.
     - **S9:** `docker stop tripmate-ai`, then run. Afterwards `docker start tripmate-ai`, and wait
       until it is healthy.
     - **S10:** run as S3.

     For S1 and S7–S9, compare the `poiId` and `plannedArrival` sequences of the response items:
     ```powershell
     (Get-Content "$env:TEMP\tripmate-ai-smoke\s1.json" -Raw | ConvertFrom-Json).items | ForEach-Object { "$($_.poiId) $($_.plannedArrival)" }
     ```
  7b. **Real models on the laptop GPU** (spec §13, S11–S14; spec §7.6).
     - Check the host: `nvidia-smi` shows driver ≥ 531; `ollama list` shows `tripmate-explainer` and
       `bge-m3` (if not: `ollama pull bge-m3` and `ollama create tripmate-explainer -f Modelfile` in
       `C:\Users\han12\tripmate-ai-model`).
     - Write `$env:TEMP\tripmate-ai-smoke\compose.host-ollama.yml`: `services.ai.environment`
       `OLLAMA_BASE_URL: http://host.docker.internal:11434`, `LLM_MODEL: tripmate-explainer`, and
       `depends_on: !reset {}`. Stop the fake, then `docker compose -f docker-compose.yml -f <that file>
       --profile ai up -d --build ai`; wait for the `ai` container to be healthy and `/ready` 200.
     - Run S11 (the S3 request), S12 (a full-day request with at least 8 visits) and S13 (interest tags
       `["Bãi biển","Ẩm thực"]` in the disposable `TravelerProfiles` row, plus the direct rank-pois
       comparison of spec §13.3).
     - Read every `friendlyExplanation` of S11 and S12 and record whether each is one Vietnamese
       sentence about its own item.
     - Run S14 (spec §13.3): S11 with `SchedulingGeneration__SolverMode=Csp` and a traveler with
       interest tags. Record the total request time and the solver outcome; if PR #65 is not merged
       yet, record the request time only and repeat S14 in T8 after merging `origin/develop`.
     - During S11–S14 also note any CSP time-limit fallback (spec §18.4).
  8. **Evidence.** Fill in this table and copy it into spec §1 and the PR description. Never include
     keys, tokens, `.env` lines or `config` output.

     | # | HTTP | Ranking line: provider, outcome, fallbackUsed, timedOut | Explanation line: provider, outcome, fallbackUsed, timedOut | Attempt lines | AI access log | Itinerary vs S1 | Pass |
     | --- | --- | --- | --- | --- | --- | --- | --- |
     | S0–S14 | | | | | | | |

  9. **Cleanup.**
     - Run `docker compose ls` to confirm this worktree's project name, then `docker compose @dc down`.
       Add `-v` to drop this project's disposable database and model volumes.
     - Delete `$env:TEMP\tripmate-ai-smoke`.
     - Ask the developer whether to keep the worktree `.env`.
     - If another stack was stopped in step 1, start it again with `--build`: both stacks tag the API
       image `tripmate-api:latest` (`docker-compose.yml:57`).
- **Verify:** every row of the evidence table matches spec §13.3; `git status --short` shows no
  tracked change from this task.
- **Definition of Done:** S0–S11, S13 and S14 pass; S12, and S14's times and solver outcome, recorded;
  evidence recorded without secrets;
  environment cleaned up. The host Ollama and its models stay installed (demo machine).
- **STOP:** any mismatch. Investigate it before T7 and T8. A defect in patch code goes to the developer
  (§2).

### T7 — Measurement runbook

- **Goal:** the runbook described in spec §14.
- **Files (new):** `docs/operations/uc10-tripmate-ai-measurement-runbook.md`.
- **Steps:**
  1. Check the state of findings F1–F7 in the `Capstone_AI` PR (spec §8.8). Part A needs payloads of
     backend size (F5). If the AI PR does not add them, ask the developer whether the runbook should
     describe how to build them.
  2. Write the sections of spec §14.3 with the thresholds of §14.2. Every threshold cites its source,
     and the rollback matches spec §7.6.
  3. Copy the preflight (the override file and its commands) exactly as validated in T6.
  4. Include the GPU host procedure of spec §7.6 and §14.3 (driver, native Ollama, models, host
     override), and the laptop reference numbers of spec §9.3 so the teammate can compare.
- **Verify:** the whitespace check (§3) for the new file; every preflight command was run in T6; the
  developer reads the runbook.
- **Definition of Done:** the developer approves the runbook; no other file changed.
- **Commit (developer):** `docs(trip): add TripMate AI measurement runbook`.

### T8 — Final verification and handoff

- **Goal:** all gates green, status recorded, and the developer chooses how to finish (AGENTS.md:26).
- **Files:** `specs/UC-10-tripmate-ai-provider-spec.md` and this plan (status sections only).
- **Steps:**
  1. Run the final commands and the CI-equivalent format check (§3).
  2. Check that the diff against `origin/develop` contains exactly the 13 patch files, the 3 new test
     files, the runbook, the spec and this plan, plus, if A2 was confirmed,
     `PoiRankingOrchestrator.cs`, `PoiRankingOrchestratorTests.cs` and the test setups listed in the
     T5a review notes.
  3. Update spec §1 and plan §1 with the results, the smoke evidence and the answers to the open
     questions.
  4. Present the options (merge, PR, keep, discard), give the developer the commit and push commands,
     and recall the recommended merge order (spec Q4).
- **Verify:** the final commands; `git diff --stat origin/develop` together with `git status --short`.
- **Definition of Done:** local gates green; after the developer pushes, CI green, including the SQL
  Server and Redis suites without skips; status sections updated.
- **Commit (developer):** `docs(trip): record TripMate AI provider verification`.

## 5. Review Focus

1. Patch fidelity: the T2 checks pass, and no later commit touches the patch files.
2. Gemini stays the default and behaves as before: C1 passes, and existing tests are unedited (except
   the T5a setups) and green.
3. Unchanged areas: no change to the contracts, the handler, the blend, the CSP, migrations or
   endpoints, and no orchestrator change except A2 if confirmed (spec AC9, AC13).
4. CSP and personalization: the score flow and the A2 reasoning of spec §18 hold in the code; S14
   evidence shows the CSP and both AI providers in one generation.
5. Tests: controlled handlers only, timing margins as in the Gemini tests, break checks recorded.
6. Secrets: tests use obviously fake keys (the patch uses `test-only-shared-key`, P:930); no key or
   token in logs, documents or evidence.
7. Documents: thresholds trace to the verified timeouts, and the rollback is correct.

## 6. Risk Register

| Risk | Likelihood | Impact | Mitigation |
| --- | --- | --- | --- |
| The patched tree fails build, format or tests on current develop | Low: nothing the patch uses has changed (spec §5.6) | Blocks T2 | STOP; the developer decides on a separate commit. |
| Flaky timing tests | Medium | CI noise | The Gemini tests' margins; no tight race against the overall budget. |
| The smoke run collides with another stack (container names, the shared `tripmate-api:latest` tag) | Medium | Disrupts the developer's main stack | Check first, ask before stopping anything, rebuild the image afterwards. |
| Secrets leak into evidence (`config` output, tokens, keys) | Low | Credential exposure | Record outcomes only; never paste `config` output. |
| OpenRouteService unavailable during the smoke run | Low | The AI steps are never reached | S1 checks generation first; retry later. |
| Demo hardware too slow for the real models | Low on the laptop GPU (spec §9.3); certain on CPU-only hosts | TripMateAi unusable for the demo | Use the laptop GPU (spec §7.6); the runbook's decision rule keeps Gemini elsewhere; the fallback keeps generation working. |
| Plain HTTP used across a network | Low | Key and POI data sent in clear text | The operational rule of spec §10; Q5. |
| A2 changes what existing tests expect (a provider call with an empty token list) | Medium | Test edits in T5a | Give those travelers a token, list each edit in the review notes; spec AC13 fixes the intended behavior. |
| The laptop GPU path is not ready (driver, Ollama, models, host override) | Medium | S11–S14 cannot run | T6 step 7b checks `nvidia-smi` and `ollama list` first; the runbook (T7) carries the procedure. |
| CSP time-limit fallbacks on the shared demo host | Low–medium | The heuristic plan is returned (safe), CSP gains are lost | Record the solver outcome in S11–S14 (spec §18.4); keep the CSP monitoring rules. |
| PR #65 merges before or after this PR | High | `appsettings.json` merge and S14 conditions change | The two diffs touch different lines; merge `origin/develop` in T8 and repeat S14. |
| No traveler preferences in production | Certain today | AI ranking has no visible effect; personalization cannot be judged | Spec §17 and Q9; if A2 is confirmed, plans stay unchanged until preferences exist. |

## 7. Commit Sequence (run by the developer)

| Order | Task | Commit |
| --- | --- | --- |
| 1 | T0 | `docs(trip): add UC-10 TripMate AI provider spec and plan` |
| 2 | T2 | `feat(ai): add TripMate AI providers to replace Gemini behind a config switch` (patch subject and body; author date 2026-10-09 16:19:46 +0700) |
| 3 | T3 | `test(trip): cover TripMate AI ranking provider edge cases` |
| 4 | T4 | `test(trip): cover TripMate AI explanation retry, timeout and budget paths` |
| 5 | T5 | `test(trip): cover TripMate AI provider selection and startup validation` |
| 5a | T5a (if A2 confirmed) | `fix(trip): skip AI POI ranking when the traveler has no preference tokens` |
| 6 | T7 | `docs(trip): add TripMate AI measurement runbook` |
| 7 | T8 | `docs(trip): record TripMate AI provider verification` |

T1 and T6 change no file. Scopes follow AGENTS.md:97-100 (`trip`), except the patch subject (T2).

## 8. Definition of Done (whole plan)

- [ ] Spec and plan approved; every open question answered or deferred in writing.
- [ ] Patch applied unchanged (T2 fidelity checks), with `.env.example` merged as spec A1 says.
- [ ] The 26 patch cases and RT1–RT7, E1–E10 and C1–C8 green (and O1 if A2 is confirmed); break checks recorded.
- [ ] Existing tests green; unedited except the T5a setups listed in the review notes (A2 only).
- [ ] If A2 is confirmed: O1 failed before the change and passes after; spec AC13 holds.
- [ ] Smoke scenarios S0–S11, S13 and S14 passed; S12, and S14's times and solver outcome, recorded (real models on the laptop GPU for S11–S14); evidence recorded without secrets.
- [ ] Runbook written and approved.
- [ ] Final gates green locally; CI green with the SQL Server and Redis suites without skips.
- [ ] Diff limited to the planned files.
- [ ] No commit or push by the agent unless the developer asked.
