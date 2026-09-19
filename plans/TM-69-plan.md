# TM-69 Join Shared Group Trip Implementation Plan

**Goal:** Allow an authenticated Traveler to redeem one valid private group invitation, create or reactivate exactly one active membership, receive the target group, and open that group in the Flutter mobile app.

**Architecture:** The API uses a MediatR Command executed in a serializable transaction with transaction-scoped SQL Server application locks, an idempotent `social.GroupJoinOperations` database ledger, and domain-driven membership lifecycle logic on `GroupMember` and `GroupInvitation`. Flutter provides manual-code and QR-invitation entry points, validates payloads through a dedicated Cubit, preserves the operation key across retries, and navigates upon verified server success.

**Spec:** `specs/TM-69-spec.md`

## Task 1: Synchronize branch and database schema baseline

- Verify branch `feature/khanhpq-join-shared-group-trip` on both `Capstone_BE` and `Capstone_Mobile`.
- Add idempotent migration `database/migrations/20260917_add_group_join_operations.sql` creating `social.GroupJoinOperations` with primary key, foreign keys, and unique constraint on `(traveler_user_id, idempotency_key)`.
- Update `specs/TM-69-spec.md` status to approved/in-progress.

## Task 2: Domain and Error Code specifications

- Add domain entity `GroupJoinOperation` and EF Core configuration in Infrastructure.
- Add domain methods on `GroupMember`:
  - `CreateMember(TravelGroup, long userId, DateTimeOffset joinedAtUtc)`
  - `Reactivate(DateTimeOffset rejoinedAtUtc)`
- Add `IncrementUsedCount()` to `GroupInvitation`.
- Add error codes `travel_group.invitation_unavailable` and `travel_group.already_active_member` to `TravelGroupErrorCodes`.
- Add domain unit tests for `GroupMember` creation, rejoin, and guards.

## Task 3: Application lock and join command handler

- Add `IGroupJoinLock` interface and `SqlServerGroupJoinLock` implementation acquiring application locks in the required order:
  `operation key (Traveler + UUID) -> invitation code -> travel group`.
- Implement `JoinTravelGroupCommand`, `JoinTravelGroupCommandValidator`, and `JoinTravelGroupCommandHandler`.
- Enforce transactional boundaries, idempotent replays, payload mismatch detection, membership checks (`Active`, `Left`, `Removed`), invitation expiration/capacity checks, and atomic state updates.
- Add handler unit tests and transaction rollback / concurrency tests.

## Task 4: HTTP endpoint and integration tests

- Add `JoinTravelGroupRequest` DTO and endpoint `POST /api/v1/travel-groups/join` with `[BindRequired, FromHeader(Name = "Idempotency-Key")]` in `TravelGroupsController`.
- Map 400 (validation, unavailable invitation), 401 (unauthorized), 403 (non-traveler), 409 (already active, key mismatch), and 200 (raw DTO) responses using ProblemDetails for failures.
- Add integration tests verifying end-to-end HTTP behavior, concurrent join serialization, and database persistence.

## Task 5: Mobile domain, parsing, and state management

- Implement `QrInvitationParser` utility supporting `tripmate://groups/join?code=<INVITATION_CODE>` and 8-character manual code normalization.
- Update `travel_group_repository.dart` and `travel_group_repository_impl.dart` to call `POST /api/v1/travel-groups/join` with `Idempotency-Key` header.
- Update `error_mapper.dart` to map MSG56, MSG57, MSG125, MSG126, MSG127.
- Implement `JoinTravelGroupCubit` and `JoinTravelGroupState` managing operation UUID lifecycle and state transitions.
- Add unit tests for parser, error mapper, repository, and cubit.

## Task 6: Mobile UI, QR scanner, and navigation

- Implement `JoinTravelGroupPage` matching `join-shared-group-trip-screen-spec.md` and Stitch prompt:
  - App bar with Back and exact title.
  - Form with 8-character uppercase code text field.
  - "Join Group" button.
  - "or" separator.
  - "Scan QR Invitation" button and in-app scanner modal/sheet.
  - Inline error displays for MSG01, MSG56, MSG57, MSG127.
  - Success toast MSG58 and navigation to joined group details (`/traveler/groups/{groupId}`).
- Register route in `app_router.dart` and add entry point in traveler navigation.
- Add widget tests for manual and QR paths.

## Task 7: Final verification and quality gates

- Run `dotnet test` and format checks on `Capstone_BE`.
- Run `flutter analyze` and `flutter test` on `Capstone_Mobile`.
- Commit with conventional commit messages referencing `[TM-69]`.
