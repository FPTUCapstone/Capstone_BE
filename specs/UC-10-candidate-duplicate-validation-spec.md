# UC-10 Candidate Duplicate Validation Specification

## Status

Completed — implemented and verified on 2026-10-04.

## Context and Problem

Both public generation workflows in `ItineraryGenerationService` currently turn
`GenerationInput.Candidates` into a dictionary keyed by `GenerationCandidate.Id`:

```csharp
var candidatesById = input.Candidates.ToDictionary(candidate => candidate.Id);
```

`Enumerable.ToDictionary` throws `ArgumentException` as soon as it encounters a
duplicate key. Consequently, the later candidate-count checks are unreachable for
duplicate IDs, and corrupted caller input escapes as an unexpected exception rather
than the scheduling service's existing controlled infeasible result.

The same defect exists in:

- `GenerateAsync`, whose existing unavailable-candidate message is
  `"A mandatory location is unavailable."`;
- `GenerateFixedOrderAsync`, whose existing unavailable-candidate message is
  `"A selected visit location is unavailable."`.

## Scope

This change covers:

- safe, non-throwing validation and dictionary construction for
  `GenerationInput.Candidates`;
- identical duplicate-ID handling in `GenerateAsync` and
  `GenerateFixedOrderAsync`;
- preservation of the existing `ConstraintsInfeasible` error code and the
  workflow-specific unavailable-candidate messages;
- unit regression tests through the two public generation methods.

## Out of Scope

- changing caller logic or database projections in
  `CreateSchedulingRequestCommandHandler`;
- silently repairing or deduplicating corrupted candidate input;
- changing POI ranking, scoring, matrix selection, permutation, or scheduling
  behavior;
- changing public API DTOs, response bodies, or HTTP status mappings;
- changing validation of duplicate `MandatoryPoiIds` or duplicate
  `orderedVisitPoiIds`, which already has separate behavior.

## Requirements

### R1 - Non-Throwing Candidate Validation

Neither public generation method may throw `ArgumentException` when two or more
entries in `input.Candidates` have the same `GenerationCandidate.Id`.

### R2 - Controlled Infeasible Failure

When duplicate candidate IDs are detected:

- `GenerateAsync` shall return a failed `Result<GeneratedItineraryPlan>` whose
  `ErrorCode` equals `SchedulingErrorCodes.ConstraintsInfeasible` and whose
  `ErrorMessage` equals `"A mandatory location is unavailable."`;
- `GenerateFixedOrderAsync` shall return a failed
  `Result<GeneratedItineraryPlan>` whose `ErrorCode` equals
  `SchedulingErrorCodes.ConstraintsInfeasible` and whose `ErrorMessage` equals
  `"A selected visit location is unavailable."`.

These are assertions on the observable `Result<T>` contract. The implementation
may continue to use the service's existing `Infeasible` helper.

### R3 - No Silent Deduplication

The implementation shall not use `GroupBy`, `DistinctBy`, first-wins,
last-wins, or any equivalent behavior that silently chooses one duplicate.
Duplicate IDs represent invalid caller input and must reject the generation.

### R4 - Early Validation and No External Work

Duplicate validation shall finish before route-matrix retrieval. A duplicate-ID
failure must not call `IRouteDurationProvider`.

### R5 - Efficiency

Candidate validation and lookup construction shall use one `O(N)` pass and one
dictionary allocation. It shall not allocate an intermediate grouped or
deduplicated candidate collection.

## Acceptance Criteria

1. `GenerateAsync` with duplicate candidate IDs returns the exact controlled
   failure in R2 and does not call the route provider.
2. `GenerateFixedOrderAsync` with duplicate candidate IDs returns the exact
   controlled failure in R2 and does not call the route provider.
3. Existing `ItineraryGenerationServiceTests` for unique candidate IDs remain
   green with unchanged results.
4. `TripMate.slnx` builds with no new compiler warnings or errors.
5. `dotnet format TripMate.slnx --no-restore --verify-no-changes` succeeds.

## Approved Test Seams

Tests exercise only these existing public methods:

- `ItineraryGenerationService.GenerateAsync`;
- `ItineraryGenerationService.GenerateFixedOrderAsync`.

Tests may replace `IRouteDurationProvider`, which is an external-system boundary,
with a spy that fails the test if invoked. Private helpers are not direct test
targets.

## Delivery Constraints

- Follow one vertical TDD slice at a time: one failing public-behavior test,
  minimal implementation, then green verification before starting the second
  method.
- Preserve all unrelated developer changes in the working tree.
- Do not introduce an EF migration or database change.
- Do not commit, push, merge, or open a pull request without explicit developer
  instruction.
