# Implementation plan

The user explicitly authorized execution without another approval round.

1. Read AGENTS and existing handlers/controllers/tests; establish the focused existing member-query baseline.
2. Add a failing handler projection/isolation test under `tests/TripMate.Application.UnitTests/Features/TravelGroups/GetConversations/`; implement query, response and SQL-translatable read-only handler under `src/TripMate.Application/Features/TravelGroups/GetConversations/`. Add paging and validation cases in subsequent red/green cycles.
3. Add failing HTTP tests in `tests/TripMate.Api.IntegrationTests/TravelGroups/GetTravelGroupConversationsEndpointTests.cs`; add the separate `src/TripMate.Api/Controllers/V1/TravelGroupConversationsController.cs`. Verify role restrictions, caller isolation, cursor validation, response shape and existing traveler-only endpoint restrictions.
4. Add SQL Server coverage in `tests/TripMate.Api.IntegrationTests/TravelGroups/GetTravelGroupConversationsSqlServerTests.cs`; verify actual SQL translation, one read command, no tracking and nullable/UTC message projection using existing isolated fixture.
5. Run focused handler and travel-group endpoint tests with `dotnet test --no-restore -m:1 --filter ... -- RunConfiguration.MaxCpuCount=1`; review scope and diff. Report exact files and results including skipped SQL verification. Do not restart running services.

Done: bounded endpoint matches the explicit contract, focused tests pass, no unrelated files changed. Perform spec-compliance and code-quality self-review before delivery.

## Verification record

Unread follow-up plan (explicitly approved by user): confirm the third worker's property exists; add focused failing handler cases and the HTTP contract assertion; extend only the existing response/projection; extend the isolated SQL assertion; run conversation handler and endpoint tests with one build/test worker. Preserve all group paging logic and third-worker-owned entity/mapping files.

- Existing member-query handler baseline: 6 passed.
- Red: new handler test initially failed compilation before the feature existed; invalid-paging cases failed before the guard was added. HTTP red phase: 16 failed with missing-route responses, 1 existing-route authorization regression check passed.
- Green: focused handler/member-query run passed 16 tests before adding the final non-host membership case.
- Travel-group integration run: 52 passed, 0 failed, 40 skipped; includes all 17 new endpoint cases. SQL tests are skipped because `TRIPMATE_SQLSERVER_TEST_CONNECTION` is unset; actual SQL execution/UTC verification remains pending that environment.
- Two-pass self-review: contract/authorization/paging scope checked; bounded projection, no tracking, no writes, canonical status mapping, and nullable previews checked. All nine task files pass whitespace checks.
- Only the nine new task files were authored here. Concurrent direct-chat and chat-hardening changes belong to the parent task; the pre-existing coupon test was preserved.
- Broader travel-group unit run: 114 passed, 1 failed in the concurrently introduced `GroupChatHardeningTests.Vote_RejectsOptionMissingFromPoll_AndAllowsRepeatedCancellation` (expected invalid-poll-options error, actual null). This parent-owned safety failure was reported and left untouched; all conversation handler cases passed within that run.
- Final focused handler/member-query verification: 17 passed, 0 failed, using the built assembly with `-m:1`, `RunConfiguration.MaxCpuCount=1`, `xUnit.MaxParallelThreads=1`, and collection parallelization disabled.

### Unread follow-up verification

- Confirmed the third worker's `LastReadChatMessageId` property and mapping existed; neither file was edited here.
- Red: focused unread tests failed compilation because the conversation response lacked `UnreadCount`.
- Added the SQL-side count before materialization: current active membership watermark (null means zero), exclusive message ID comparison, matching group, sender different from caller. Existing group ordering, limit, lookahead, and continuation cursor code are unchanged.
- Initial handler green run: 16 passed, including five new unread cases. Strengthened the cursor cases to use a non-host caller with a different host cursor; final verification recorded below.
- Endpoint verification: 18 passed, 0 failed; 1 SQL Server test skipped because the test connection is unset. The updated SQL fixture verifies one incoming unread message and one command when SQL Server is configured.
- A shared-output build hit another worker's testhost file lock. Verification succeeded with `-p:OutputPath=bin/UnreadConversationVerification/`; no other process was interrupted. Build and xUnit parallelism were limited to one.
- Follow-up edits are limited to the conversation handler/response, the existing handler/endpoint/SQL tests, and this plan/spec. Cursor behavior, controller, entities, mapping, and migrations were left untouched. Reviewed scope and SQL projection; whitespace checks produced no warnings.
- Final strengthened handler run: 16 passed, 0 failed, including non-host cursor isolation and all existing group pagination cases. Used the separate output folder and `BuildProjectReferences=false` to reuse the dependencies already built for endpoint verification.
