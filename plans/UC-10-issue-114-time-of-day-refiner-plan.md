# UC-10 Issue #114 — Preserve Weighted Solver Result

## Scope

Prevent `TimeOfDayRefiner` from replacing a plan already evaluated by `WeightedUtility`. Keep the
existing refiner behavior for plans without `ModelObjective`.

## Task 1 — Pin the regression

- File: `tests/TripMate.Application.UnitTests/Features/Scheduling/TimeOfDay/TimeOfDayRefinerTests.cs`
- Add a test where the time-of-day score prefers the reverse order, but the input plan has a
  `ModelObjective`.
- Verify that the original order and objective are preserved and an applicable recommendation
  reason is still added.
- Red command:
  `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter "FullyQualifiedName~TimeOfDayRefinerTests.RefineAsync_WhenPlanHasModelObjective_PreservesSolverResultAndAddsReasons"`

## Task 2 — Apply the minimal guard

- File: `src/TripMate.Application/Features/Scheduling/TimeOfDay/TimeOfDayRefiner.cs`
- Return `WithReasons(...)` before candidate-order generation when `ModelObjective` has a value.
- Keep the disabled path and plans without `ModelObjective` unchanged.

## Verification

- Run the new regression test and all `TimeOfDayRefinerTests`.
- Run the related scheduling and itinerary version unit tests.
- Run the Application unit test suite and Release build.
- Run formatting verification and `git diff --check`.

## Definition of Done

- A weighted plan's order, schedule, and `ModelObjective` survive refinement.
- Recommendation reasons are still populated where a time-of-day rule applies.
- Existing refiner behavior for plans without `ModelObjective` remains covered and passing.
- Spec and ADR record the ownership rule.
