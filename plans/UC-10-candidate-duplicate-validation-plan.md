# UC-10 Candidate Duplicate Validation Implementation Plan

## Status

Completed — implemented, verified with red-to-green unit tests, full suite green, and formatted on 2026-10-04.

## Goal

Resolve UC-10 audit issue 2 by replacing exception-throwing candidate dictionary
construction with a safe, single-pass builder. Duplicate
`GenerationCandidate.Id` values must produce the existing controlled
`ConstraintsInfeasible` result in both public generation workflows.

## Source Specification

`specs/UC-10-candidate-duplicate-validation-spec.md`

## Baseline and Workspace

- Branch: `feature/phuctv-uc10-poi-recommendations`.
- The working tree already contains unrelated developer changes; preserve them.
- The most recent recorded Application test run was green, but implementation
  must establish a fresh targeted baseline before the first red test.
- No formatter or full-build result is claimed until W03 records a successful
  command after the implementation.

## Technical Design

In
`src/TripMate.Application/Features/Scheduling/Common/ItineraryGenerationService.cs`,
add one private static helper:

```csharp
private static bool TryBuildCandidateMap(
    IReadOnlyCollection<GenerationCandidate> candidates,
    out Dictionary<long, GenerationCandidate> candidateMap)
{
    candidateMap = new Dictionary<long, GenerationCandidate>(candidates.Count);
    foreach (GenerationCandidate candidate in candidates)
    {
        if (!candidateMap.TryAdd(candidate.Id, candidate))
        {
            return false;
        }
    }

    return true;
}
```

`GenerateAsync` shall use the helper before checking mandatory membership:

```csharp
if (!TryBuildCandidateMap(input.Candidates, out var candidatesById)
    || input.MandatoryPoiIds.Any(id => !candidatesById.ContainsKey(id)))
{
    return Infeasible("A mandatory location is unavailable.");
}
```

`GenerateFixedOrderAsync` shall use the same helper before checking ordered
visit membership:

```csharp
if (!TryBuildCandidateMap(input.Candidates, out var candidates)
    || orderedVisitPoiIds.Any(id => !candidates.ContainsKey(id)))
{
    return Infeasible("A selected visit location is unavailable.");
}
```

The `||` operators intentionally short-circuit. A partially populated map is not
observed after `TryBuildCandidateMap` returns `false`.

## Work Items

### W01 - Baseline and `GenerateAsync` Red-to-Green Slice

Files:

- Modify
  `tests/TripMate.Application.UnitTests/Features/Scheduling/ItineraryGenerationServiceTests.cs`.
- Modify
  `src/TripMate.Application/Features/Scheduling/Common/ItineraryGenerationService.cs`.

Steps:

1. Run the targeted service tests before editing and record the baseline.
2. Add
   `Generate_WithDuplicateCandidateIds_ReturnsControlledInfeasibleWithoutRouting`.
3. Supply two distinct `GenerationCandidate` values with the same ID and a
   mandatory list containing that ID.
4. Assert `IsFailure`, the exact error code and message from spec R2, and zero
   route-provider calls.
5. Run only this test and confirm it is red because the current `ToDictionary`
   throws `ArgumentException`.
6. Add `TryBuildCandidateMap` and adopt it only in `GenerateAsync`.
7. Rerun the new test and then all `ItineraryGenerationServiceTests` until green.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --nologo --filter FullyQualifiedName~Generate_WithDuplicateCandidateIds_ReturnsControlledInfeasibleWithoutRouting
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --nologo --filter FullyQualifiedName~ItineraryGenerationServiceTests
```

Definition of done: `GenerateAsync` rejects duplicate IDs without throwing or
calling the route provider, while the fixed-order workflow remains unchanged for
the next red slice.

### W02 - `GenerateFixedOrderAsync` Red-to-Green Slice

Files:

- Modify the same service and test files as W01.

Steps:

1. Add
   `GenerateFixedOrder_WithDuplicateCandidateIds_ReturnsControlledInfeasibleWithoutRouting`.
2. Supply duplicate candidate IDs and an ordered visit list containing that ID.
3. Assert `IsFailure`, the exact fixed-order error code and message from spec R2,
   and zero route-provider calls.
4. Run only this test and confirm it is red because the remaining
   `ToDictionary` throws `ArgumentException`.
5. Replace the fixed-order dictionary construction with `TryBuildCandidateMap`
   and remove its now-dead count comparison.
6. Rerun the new test and all `ItineraryGenerationServiceTests` until green.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --nologo --filter FullyQualifiedName~GenerateFixedOrder_WithDuplicateCandidateIds_ReturnsControlledInfeasibleWithoutRouting
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --nologo --filter FullyQualifiedName~ItineraryGenerationServiceTests
```

Definition of done: both public workflows share the safe builder and satisfy the
same non-throwing validation contract without silent deduplication.

### W03 - Focused Regression and Delivery Evidence

Actions:

1. Run the complete Application unit-test project to cover all current callers
   without invoking unrelated SQL integration fixtures.
2. Build the solution with `--no-restore` and confirm no new warnings or errors.
3. Run formatter verification without modifying files.
4. Inspect `git diff --check`, the diff for the two implementation files, and
   `git status --short` to ensure unrelated developer work is preserved.
5. Update this spec and plan to `Completed` only after every command below is
   green, recording counts and any pre-existing warnings honestly.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --nologo
dotnet build TripMate.slnx --no-restore --nologo
dotnet format TripMate.slnx --no-restore --verify-no-changes
git diff --check
git status --short
```

Definition of done: the scoped regression suite, solution build, and formatter
verification are green, with no unrelated file modified by this work.

## Dependency Order

```text
W01 -> W02 -> W03
```

Do not start W02 until W01 is green. Do not declare completion until W03 is
green.

## Definition of Done

- [x] The targeted pre-change baseline is recorded.
- [x] `GenerateAsync` duplicate-ID test was observed red, then green.
- [x] `GenerateFixedOrderAsync` duplicate-ID test was observed red, then green.
- [x] `TryBuildCandidateMap` uses `GenerationCandidate` and one dictionary pass.
- [x] Both workflows return the exact existing controlled failure and skip route
      retrieval for duplicate IDs.
- [x] Existing `ItineraryGenerationServiceTests` remain green (37 passed).
- [x] The complete Application unit-test project is green (1,104 passed).
- [x] The solution build has no new warnings or errors.
- [x] Formatter verification and `git diff --check` succeed.
- [x] Unrelated developer changes remain untouched.
- [x] No commit, push, merge, or pull request occurs without explicit developer
      instruction.

## Verification Evidence

- Red Tests: Both `GenerateAsync_WithDuplicateCandidateIds` and `GenerateFixedOrderAsync_WithDuplicateCandidateIds` failed with `System.ArgumentException: An item with the same key has already been added. Key: 12`.
- Green Tests: After introducing `TryBuildCandidateMap`, both tests passed (0 failed).
- Scoped Suite: `ItineraryGenerationServiceTests` 37/37 passed.
- Full Unit Test Suite:
  - `TripMate.Application.UnitTests`: 1,104 passed, 0 failed.
  - `TripMate.Infrastructure.UnitTests`: 231 passed, 1 skipped.
- Formatting: `dotnet format TripMate.slnx --verify-no-changes` returned 0 violations.
