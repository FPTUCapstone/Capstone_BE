# Specification: TM-63 [UC-17] Create Travel Group

**Feature**: Create Travel Group  
**Jira Ticket**: TM-63  
**Use Case**: UC-17  
**Branch**: `feature/khanhpq-create-travel-group` (Base: `develop`)  
**Target Repository**: `Capstone_BE` (ASP.NET Core 10 Web API)  
**Status**: Pending Approval  

---

## 1. Scope & Objective

### 1.1 Objective
Allow an authenticated Traveler to create a new Travel Group linked to an eligible, existing itinerary (`planning.Itineraries`). The creator is automatically assigned as the **Group Host** in `social.GroupMembers`. Invitation generation is handled by UC-18.

### 1.2 Boundary & Out of Scope
- **In Scope**:
  - Validating linked itinerary existence and ownership.
  - Creating `social.TravelGroups` record with creator as `host_user_id`.
  - Atomically inserting creator into `social.GroupMembers` with `status = 'Active'` and `location_sharing_enabled = 0`.
  - Rejecting duplicate submissions through a client-supplied `Idempotency-Key`.
  - Exposing endpoint `POST /api/v1/travel-groups`.
- **Out of Scope (Adjacent Tasks)**:
  - Inviting members via email/notification (UC-18 / TM-64).
  - Joining a group using an invite code (UC-23 / TM-69).
  - Removing a member (UC-20 / TM-66) or leaving a group (UC-21 / TM-67).
  - Toggling real-time location sharing (UC-22 / TM-68).

---

## 2. Acceptance Criteria (AC)

- **AC-01 (Itinerary Validation)**: Request must specify an `itineraryId`. If the itinerary does not exist, return `404 Not Found` with code `TravelGroup.ItineraryNotFound`.
- **AC-02 (Group Name Validation)**: `groupName` is required (MSG01: "This field is required.") and must not exceed 150 characters. Whitespace must be trimmed. If empty, return `400 Bad Request` with FluentValidation error.
- **AC-03 (Host Assignment)**: The authenticated creator (`host_user_id`) must automatically become the exclusive initial Group Host in `social.GroupMembers` with `status = 'Active'` and `joined_at = UtcNow`.
- **AC-04 (Idempotency)**: The client must send a non-empty GUID `Idempotency-Key` header. A repeated request from the same Traveler with the same key returns the original group and must not create another group. Different keys may create groups with the same name.
- **AC-05 (Atomic Transaction)**: `TravelGroup`, `GroupMember` (Host), and the idempotency operation record are persisted in one database transaction. If any operation fails, no incomplete TravelGroup is retained.
- **AC-06 (Response Contract)**: On success, return `201 Created` with payload containing `groupId`, `groupName`, `itineraryId`, `hostUserId`, and `createdAtUtc`.

---

## 3. Database Schema Mapping (Database-First `tripmate_schema_v7.sql`)

### 3.1 `social.TravelGroups`
| Column | Type | Nullable | Description |
|---|---|---|---|
| `group_id` | `BIGINT IDENTITY(1,1)` | No (PK) | Auto-generated group identifier |
| `itinerary_id` | `BIGINT` | No (FK) | References `planning.Itineraries(itinerary_id)` |
| `host_user_id` | `BIGINT` | No (FK) | References `dbo.Users(user_id)` |
| `name` | `NVARCHAR(150)` | Yes | Group name (validated max 150 chars) |
| `created_at` | `DATETIME2` | No | UTC creation timestamp |

### 3.2 `social.GroupMembers`
| Column | Type | Nullable | Description |
|---|---|---|---|
| `group_id` | `BIGINT` | No (PK, FK) | References `social.TravelGroups(group_id)` ON DELETE CASCADE |
| `user_id` | `BIGINT` | No (PK, FK) | References `dbo.Users(user_id)` |
| `location_sharing_enabled` | `BIT` | No | Default `0` (false) |
| `status` | `VARCHAR(10)` | No | Check constraint: `'Active'`, `'Removed'`, `'Left'`. Initial: `'Active'` |
| `joined_at` | `DATETIME2` | No | UTC join timestamp |
| `left_at` | `DATETIME2` | Yes | Null for active members |

### 3.3 `social.TravelGroupCreationRequests`
| Column | Type | Nullable | Description |
|---|---|---|---|
| `request_id` | `BIGINT IDENTITY(1,1)` | No (PK) | Auto-generated operation identifier |
| `traveler_user_id` | `BIGINT` | No | Authenticated Traveler who submitted the operation |
| `idempotency_key` | `UNIQUEIDENTIFIER` | No | Client-generated GUID; unique per Traveler |
| `group_id` | `BIGINT` | No (FK) | Created TravelGroup returned on retry |
| `created_at` | `DATETIME2` | No | UTC creation timestamp |

## 4. API Endpoint & DTO Contracts

### 4.1 Endpoint
- **Route**: `POST /api/v1/travel-groups`
- **Auth**: Authenticated Traveler (JWT Bearer Token)
- **Required header**: `Idempotency-Key: <GUID>`

### 4.2 Request Body (`CreateTravelGroupRequest`)
```json
{
  "itineraryId": 1001,
  "groupName": "Da Nang Summer Trip 2026"
}
```

### 4.3 Response Body - Success (`201 Created`)
```json
{
  "groupId": 1,
  "groupName": "Da Nang Summer Trip 2026",
  "itineraryId": 1001,
  "hostUserId": 5,
  "createdAtUtc": "2026-09-08T10:30:00Z"
}
```

### 4.4 Error Responses
- **400 Bad Request (Validation Error)**:
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "GroupName": ["This field is required."]
  }
}
```
- **404 Not Found (Business Error)**:
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.4",
  "title": "Itinerary not found.",
  "status": 404,
  "detail": "The specified itinerary does not exist or is inaccessible."
}
```

---

## 5. Domain Rules & Technical Invariants

1. **Pure Domain**: Domain entities contain no references to EF Core, ASP.NET Core, or infrastructure.
2. **Navigation Property Graph Insert (AGENTS.md §3.2)**:
   When creating `GroupMember` and `TravelGroupCreationRequest` alongside `TravelGroup`, assign their `TravelGroup` navigation property, NOT scalar `GroupId` (which is still `0` before persistence).
3. **Change Tracking**: Keep change tracking enabled during command handler execution.
4. **UTC Timestamps**: All `DateTime` properties must be mapped using `AsUtcDateTime2()` or `DateTime.UtcNow`.
5. **No Synthetic Wrapper**: Return raw DTO on success (201 Created) and ProblemDetails RFC-7807 on failure.
