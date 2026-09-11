# Specification: TM-64 [UC-18] Invite Group Members

**Feature**: Invite Group Members  
**Jira Ticket**: TM-64  
**Use Case**: UC-18  
**Branch**: `feature/khanhpq-invite-group-members` (Base: `develop`)  
**Target Repository**: `Capstone_BE` (ASP.NET Core 10 Web API)  
**Status**: Pending Approval  

---

## 1. Scope & Objective

### 1.1 Objective
Allow an authenticated Traveler acting as the **Group Host** to retrieve or generate a valid, usable invitation code (8 alphanumeric characters) and its corresponding QR invitation data for an existing Travel Group (`social.TravelGroups`).

### 1.2 Boundary & Out of Scope
- **In Scope**:
  - Validating target travel group existence.
  - Verifying the authenticated user is the active **Group Host** of the travel group (`social.TravelGroups.host_user_id == currentUserId`).
  - Retrieving the currently active, unexpired `social.GroupInvitations` record for the group. If none exists (or if expired), atomically generating a new valid invitation code.
  - Exposing endpoint `GET /api/v1/travel-groups/{groupId}/invitation`.
  - Returning group identity, invitation code, QR data string (deep link / payload), and expiration timestamp.
- **Out of Scope (Adjacent Tasks)**:
  - Redeeming/joining a group with the invite code or scanning the QR (UC-23 / TM-69).
  - Modifying group details or member roles (UC-19 / TM-65).
  - Public group discovery or sending automated outbound emails/SMS.

---

## 2. Acceptance Criteria (AC)

- **AC-01 (Authentication & Host Authorization)**:
  - The request must be authenticated (`[Authorize]`).
  - The authenticated user must be the Host of the specified group (`TravelGroup.HostUserId == currentUserId`).
  - If the user is not the Host, return `403 Forbidden` with error code `TravelGroup.HostPermissionRequired` (MSG126: "You do not have permission to access this function.").
- **AC-02 (Group Existence)**:
  - If `groupId` does not exist in `social.TravelGroups`, return `404 Not Found` with error code `TravelGroup.GroupNotFound`.
- **AC-03 (Active Invitation Retrieval or Generation)**:
  - If an active, non-expired invitation exists (`expires_at > UtcNow` and `used_count < max_uses`), return that invitation.
  - If no active invitation exists or the existing one is expired, automatically generate a new unique 8-character invitation code with `expires_at = UtcNow + 30 days`, `max_uses = 50`, `used_count = 0`, and persist it.
- **AC-04 (No Membership Side Effect)**:
  - Retrieving or generating an invitation must NEVER alter or insert any record into `social.GroupMembers`.
- **AC-05 (Response Contract)**:
  - On success, return `200 OK` with payload containing `groupId`, `groupName`, `inviteCode`, `qrData`, and `expiresAtUtc`.

---

## 3. Database Schema Mapping (Database-First `tripmate_schema_v7.sql`)

### 3.1 `social.TravelGroups` (Read-only reference)
| Column | Type | Nullable | Description |
|---|---|---|---|
| `group_id` | `BIGINT` | No (PK) | Group identifier |
| `itinerary_id` | `BIGINT` | No (FK) | References linked itinerary |
| `host_user_id` | `BIGINT` | No (FK) | Creator/Host user ID |
| `name` | `NVARCHAR(150)` | Yes | Group name |

### 3.2 `social.GroupInvitations` (Query & Fallback Insert)
| Column | Type | Nullable | Description |
|---|---|---|---|
| `invitation_id` | `BIGINT IDENTITY(1,1)` | No (PK) | Auto-generated invitation identifier |
| `group_id` | `BIGINT` | No (FK) | References `social.TravelGroups(group_id)` |
| `invite_code` | `VARCHAR(20)` | No (UQ) | Unique 8-character invitation code |
| `created_by` | `BIGINT` | No (FK) | Host user ID who created the invitation |
| `expires_at` | `DATETIME2` | No | Expiration timestamp (30 days from creation) |
| `max_uses` | `INT` | No | Default `50` |
| `used_count` | `INT` | No | Current redemption count |
| `created_at` | `DATETIME2` | No | UTC creation timestamp |

---

## 4. API Endpoint & DTO Contracts

### 4.1 Endpoint
- **Route**: `GET /api/v1/travel-groups/{groupId}/invitation`
- **Auth**: Authenticated Traveler (`[Authorize]`)

### 4.2 Response Body - Success (`200 OK`)
```json
{
  "groupId": 1,
  "groupName": "Da Nang Summer Trip 2026",
  "inviteCode": "TM7X9K2A",
  "qrData": "tripmate://groups/join?code=TM7X9K2A",
  "expiresAt": "2026-10-08T10:30:00Z"
}
```

### 4.3 Error Responses
- **401 Unauthorized**:
  Missing or invalid JWT Bearer token.
- **403 Forbidden**:
  User is authenticated but is NOT the host of the group.
  ```json
  {
    "type": "https://tools.ietf.org/html/rfc7231#section-6.5.3",
    "title": "Forbidden",
    "status": 403,
    "detail": "You do not have permission to access this function.",
    "errorCode": "TravelGroup.HostPermissionRequired"
  }
  ```
- **404 Not Found**:
  The specified group does not exist.
  ```json
  {
    "type": "https://tools.ietf.org/html/rfc7231#section-6.5.4",
    "title": "Not Found",
    "status": 404,
    "detail": "The specified travel group does not exist.",
    "errorCode": "TravelGroup.GroupNotFound"
  }
  ```

---

## 5. Domain Rules & Technical Invariants

1. **Reusing Domain Constants**:
   Use `TravelGroupConstants.InviteCodeLength` (8), `TravelGroupConstants.InviteCodeExpirationDays` (30), `TravelGroupConstants.DefaultMaxUses` (50), and `TravelGroupConstants.InviteCodeCharacters`.
2. **Deep-link QR Format**:
   QR Data string follows standard URI scheme: `tripmate://groups/join?code={inviteCode}` for seamless scanner integration in UC-23.
3. **Idempotency & Read-Preference**:
   If an active invitation already exists, return it directly without writing new rows to the database.
4. **No Synthetic Envelope**:
   Matches repository convention by returning `GetGroupInvitationResponse` directly on 200 OK and ProblemDetails RFC-7807 on failure.

