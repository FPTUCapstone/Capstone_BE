# TM-89: classify a connection-open `TimeoutException` as "SQL temporarily unavailable" in check-in

Status: draft for approval, 2026-11-24.
Base: `origin/develop` at `e4c11252`. Scope: one production class and its unit tests.

## 1. Problem

After a check-in request is durably admitted, `CheckInTravelerCommandHandler` catches persistence errors with
`catch (Exception ex) when (persistenceErrorClassifier.IsTemporarilyUnavailable(ex))` (handler line 396) and returns the
"unavailable" result: HTTP 503 with error code `MSG127`. The UC-43 contract (abnormal case 8.a1) is that a system or network
failure leaves the e-ticket unused, shows MSG127 and permits retry.

`SqlServerCheckInPersistenceErrorClassifier.IsTemporarilyUnavailable` walks the `InnerException` chain but only looks at
`SqlException`. When SqlClient cannot even start opening a connection it throws `System.TimeoutException` instead:

> Timeout attempting to open the connection. The time period elapsed prior to attempting to open the connection has been
> exceeded. This may have occurred because of too many simultaneous non-pooled connection attempts.

That exception is not a `SqlException`, so it is not classified, falls through to the generic error path and the client
receives HTTP 500 with no `MSG127`. The mobile client then cannot tell "retry later" from a real defect.

Evidence: the CI test `CheckInAuditHttpSqlFailureTests.SqlVerificationUnavailable_AfterDurableAdmission_Returns503AndRetryAfterRecoverySucceeds`
failed with 500 instead of 503 on develop run 43810619177 (2026-11-16) and on PR #199 attempt 1 (2026-11-23).
The test uses `ConnectTimeout=1` and `Pooling=false`; non-pooled opens share a small per-process queue, so under CI load an
open waits longer than 1 s and SqlClient throws `TimeoutException`. Attempt 2 of the same commit was green.

## 2. Goal and non-goals

Goal: `IsTemporarilyUnavailable` returns `true` when any exception in the `InnerException` chain is a
`System.TimeoutException` (including derived types). Check-in then returns 503 `MSG127` for this failure, as it already does
for the `SqlException` cases.

Non-goals (kept out on purpose):
- `InvalidOperationException` from connection-pool exhaustion. It is a generic type that also signals programming errors,
  so classifying it by type would hide real defects, and matching its message is localization-fragile. Separate decision.
- Other flaky SQL tests (for example `AccountProvisioningConcurrencySqlServerTests`, `PasswordResetSqlServerTests`). They share
  the same non-pooled-queue symptom but fail in other code paths; they are tracked separately.
- Any change to the handler, the interface `ICheckInPersistenceErrorClassifier`, the `SqlException` number list, the test
  fixture or `ConnectTimeout` in `CheckInAuditHttpSqlFailureTests.cs`.
- No schema, migration, DI or API contract change.

## 3. Design

The loop in `IsTemporarilyUnavailable` gains one condition, next to the existing `SqlException` check:

```csharp
if (current is TimeoutException)
{
    return true;
}
```

Decisions:
- Match on type (`is TimeoutException`), never on the message text.
- Scope is the classifier used by check-in only. It is only consulted inside the handler's persistence `try`, so a
  `TimeoutException` raised by SQL work (open or command) is the only way to reach it. Command timeouts already arrive as
  `SqlException` number `-2`, which is already classified, so this adds no second path for them.
- The `OperationCanceledException` branch earlier in the handler still runs first; caller cancellation is not turned
  into 503. (`TaskCanceledException` is not a `TimeoutException`.)

## 4. Approaches considered

| Option | Verdict |
|---|---|
| A. Classify `TimeoutException` in the classifier (this spec) | Chosen: smallest change, fixes the real contract gap, one place. |
| B. Raise `ConnectTimeout` 1 → 15 s in the test | Only hides the symptom in one test; production behavior stays wrong. Kept as a fallback, not in this PR. |
| C. Also reduce non-pooled queue contention for all tests | Wider, needs measurement, touches shared fixtures. Deferred. |

## 5. Tests (TDD, written first)

In `tests/TripMate.Infrastructure.UnitTests/Persistence/SqlServerCheckInPersistenceErrorClassifierTests.cs`, which today only
tests `IsKnownInfrastructureFailure`; the new tests call the public `IsTemporarilyUnavailable`:

| Test | Expectation |
|---|---|
| `TimeoutException_IsTemporarilyUnavailable` | `new TimeoutException("...")` → `true` |
| `TimeoutException_WrappedInInnerException_IsTemporarilyUnavailable` | `new InvalidOperationException("x", new TimeoutException())` → `true` |
| `UnrelatedException_IsNotTemporarilyUnavailable` | `new InvalidOperationException("x")` → `false` (guards against over-matching) |
| `OperationCanceledException_IsNotTemporarilyUnavailable` | `new OperationCanceledException()` and `new TaskCanceledException()` → `false` |
| `NullException_Throws` | `ArgumentNullException` (existing behavior, pinned) |

The first test must fail before the change (red), then pass. The existing integration test
`SqlVerificationUnavailable_AfterDurableAdmission_Returns503AndRetryAfterRecoverySucceeds` and
`UnexpectedNonSqlFailure_IsNotDisguisedAsMsg127` must still pass unchanged (the second injects an `InvalidOperationException`,
which stays unclassified).

## 6. Acceptance

- New unit tests pass; the first fails on `develop` code.
- `dotnet build` clean, including the repo's formatting and final-newline checks (`*.cs` has no final newline).
- `CheckInAuditHttpSqlFailureTests` and the infrastructure unit suite pass locally.
- PR targets `develop`, describes this change only, and records that other flaky tests are out of scope.
- CI green. Because the target test is a timing flake, a green run does not prove the fix; the proof is the unit tests plus
  the contract argument in section 1.

## 7. Risks

- Over-classification: a `TimeoutException` raised inside the persistence `try` for a non-SQL reason would be reported as
  503. The `try` contains only SQL work, so the risk is low; the unrelated-exception test pins the boundary.
- A stuck-but-reachable database now returns 503 instead of 500 for open timeouts. That matches the UC-43 8.a1 intent
  (leave the ticket unused, allow retry).
