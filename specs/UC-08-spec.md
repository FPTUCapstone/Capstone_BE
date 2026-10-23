# UC-08 Update Traveler Profile — Backend Specification

## Status and Scope

**Specification Draft — 2026-10-12.**
- **Use Case:** UC-08 Update Traveler Profile (SRS §3.2.8).
- **Actor:** Authenticated `Traveler`.
- **Target Branch:** `feature/linhnv-update-traveler-profile`.
- **Companion Frontend Specification:** [`Capstone_FE/specs/UC-08-web-spec.md`](file:///d:/study/Project-Capstone/Capstone_FE/specs/UC-08-web-spec.md).
- **Backend Implementation Plan:** [`Capstone_BE/plans/UC-08-plan.md`](file:///d:/study/Project-Capstone/Capstone_BE/plans/UC-08-plan.md).

UC-08 allows an authenticated Traveler to view and maintain personal profile information used by booking, group, and review functions. The system validates all submitted values, stores updated profile values atomically, uploads/replaces the avatar image via Cloudinary when provided, and preserves immutable system identity and role assignments.

---

## Authoritative SRS Requirements (§3.2.8)

### Function Description & Data Processing
1. **Retrieve Profile:** The system retrieves the personal profile of the currently authenticated Traveler account.
2. **Modify Fields:** The Traveler modifies editable profile fields and may optionally select a new avatar image or remove an existing avatar.
3. **Avatar Validation & Storage:** Where a new avatar is selected, file size, MIME type, and image signature are validated before upload to Cloudinary.
4. **Validation:** The system validates all submitted profile values against domain business rules.
5. **Persistence:** Validated values replace stored profile values and an updated timestamp (`updated_at`) is recorded.
6. **Read-only Identity:** The account email address is strictly read-only and is never modified by this use case.
7. **Role Immutability:** The Traveler role is system-assigned and cannot be altered or escalated through profile updates.

---

## Business Rules and Decisions

| Rule Code | Description | Enforcement |
| :--- | :--- | :--- |
| **BR-16** | **Avatar Constraints:** Avatar file must be an image of a supported format (`image/jpeg`, `image/png`, `image/webp`) and must not exceed 5 MB (5,242,880 bytes). | Validated before upload. Invalid format or size returns `400 Bad Request` with `MSG19`. |
| **BR-17** | **Read-Only Email:** The account email address cannot be edited through the profile function. | Incoming requests never modify `User.Email`. |
| **BR-18** | **Date of Birth & Age Limit:** The date of birth, where provided, must be a past date and the traveler must be at least 16 years old at the time of update. | Validated in domain validator: `DateOfBirth <= Today - 16 years`. Violated date returns `400 Bad Request` with `MSG02`. |
| **BR-23** | **Role Immutability:** Profile updates must never change the system-assigned account role (`User.Role` remains `Traveler`). | Handler only touches profile attributes, never modifying `User.Role` or `User.Status`. |
| **BR-PHONE** | **Phone Number Format:** Phone number is optional. When supplied with a non-empty value, it must follow Vietnamese mobile number format: exactly 10 digits starting with `0` (`^0\d{9}$`). | Whitespace is normalized to `null`; a supplied non-empty value is validated in the domain validator. A violation returns `400 Bad Request` with `MSG04`. |
| **BR-PHONE-UNIQUE** | A non-empty phone number belongs to at most one account across all roles. | Check ownership before avatar upload and retain the database unique index for concurrent requests. A duplicate returns `409 Conflict` with the existing catalog code `MSG_PHONE_DUP`; no profile fields are saved. |
| **BR-NAME** | **Full Name Constraint:** Full name is required, trimmed of leading/trailing whitespace, and must not exceed 100 characters. | Empty returns `MSG01`. Length > 100 returns `MSG02`. |

---

## Locked Message Catalog

| Code | Text | Trigger / Condition |
| :--- | :--- | :--- |
| **MSG01** | This field is required. | Required field (`fullName`) is empty or whitespace only. |
| **MSG02** | Invalid input format. | Invalid phone number format, date of birth in the future, or age < 16 years (BR-18). |
| **MSG04** | Invalid phone number. Phone number must be 10 digits starting with 0. | Specific phone number format error. |
| **MSG_PHONE_DUP** | This phone number is already registered to another account. | Number owned by another account, including a concurrent update detected by the unique index. |
| **MSG19** | The uploaded file exceeds the maximum allowed size (5MB) or is not a supported image format. | Avatar exceeds 5 MB or MIME type is not JPEG, PNG, or WEBP (BR-16). |
| **MSG20** | Profile updated successfully. | Profile changes successfully validated and saved to database. |
| **MSG127** | TripMate is temporarily unable to process your request. Please check your connection and try again. | Infrastructure failure (DB timeout, Cloudinary storage unavailable). |

---

## Database Schema & Domain Entity Truth

### 1. Database Schema
Profile data is stored across two normalized tables:
- **`dbo.Users`**: Stores core identity fields:
  - `user_id` (BIGINT, PK)
  - `full_name` (NVARCHAR(150), NOT NULL) $\rightarrow$ updated by UC-08.
  - `phone_number` (NVARCHAR(20), NULL) $\rightarrow$ updated by UC-08.
  - `email` (NVARCHAR(256), NULL) $\rightarrow$ read-only; immutable.
  - `avatar_url` (NVARCHAR(500), NULL) $\rightarrow$ updated by UC-08 upon avatar upload or removal.
  - `role` (VARCHAR(20), NOT NULL) $\rightarrow$ immutable; must remain `Traveler`.
  - `status` (VARCHAR(24), NOT NULL) $\rightarrow$ immutable.
  - `updated_at` (DATETIME2, NOT NULL) $\rightarrow$ updated to `SYSUTCDATETIME()`.
- **`dbo.TravelerProfiles`**: Stores traveler-specific preference attributes and date of birth:
  - `user_id` (BIGINT, PK, FK $\rightarrow$ `dbo.Users(user_id)`)
  - `date_of_birth` (DATE NULL) $\rightarrow$ **Added via migration** (`20261012_add_traveler_profile_date_of_birth.sql`).
  - `updated_at` (DATETIME2, NOT NULL) $\rightarrow$ updated to `SYSUTCDATETIME()`.

### 2. Idempotent Migration Script
Path: `database/migrations/20261012_add_traveler_profile_date_of_birth.sql`
- Adds `date_of_birth DATE NULL` to `dbo.TravelerProfiles` if not exists.

### 3. Entity Adjustments
- `TravelerProfile.cs`:
  - Property: `DateOnly? DateOfBirth`.
  - Method: `UpdateDateOfBirth(DateOnly? dateOfBirth, DateTimeOffset updatedAtUtc)`.
- `TravelerProfileConfiguration.cs`:
  - Property mapping for `date_of_birth`.

### 4. First-Profile Concurrency / Upsert Rule

`dbo.TravelerProfiles.user_id` is the primary key. Two simultaneous first profile updates can
both read no row before either transaction inserts one. This is an expected upsert race, not an
infrastructure outage.

- The initial request may insert a `TravelerProfile` row.
- If that insert loses the race only because SQL Server reports a duplicate-key violation for
  `dbo.TravelerProfiles`, the handler clears its failed EF tracking state, reloads the winner row,
  and retries the profile update exactly once.
- The retry preserves the request's validated profile values. The final persisted values follow
  normal last-committed-write behavior; exactly one profile row exists.
- A duplicate for any other table/index, a second persistence failure, timeout, cancellation, or
  Cloudinary failure is **not** retried by this rule and keeps its existing error behavior.
- If an avatar was uploaded before the duplicate, its existing cleanup reservation is retained
  across the one retry and finalized only by the successful retry transaction; it must not be
  orphaned or deleted as a false failure.

---

## API Contract

### 1. GET `/api/v1/traveler/profile`
Retrieves the traveler profile for the authenticated caller.

- **Authorization:** `[Authorize(Roles = "Traveler")]`; the query also verifies that the current `User.Status` is active. A valid token for a locked or inactive account must not expose profile data.
- **Rate Limiting:** Default authenticated policy.
- **Success Response:** `200 OK`
```json
{
  "userId": 42,
  "fullName": "Nguyen Van A",
  "email": "traveler@example.com",
  "phoneNumber": "0912345678",
  "dateOfBirth": "1998-05-20",
  "avatarUrl": "https://res.cloudinary.com/tripmate/image/upload/v1/avatars/42_abc123.jpg"
}
```
- **Error Responses:**
  - `401 Unauthorized`: Missing or invalid JWT access token.
  - `403 Forbidden`: Authenticated user is not a `Traveler` or account is locked/inactive.
  - `404 Not Found`: User record does not exist.
  - `503 Service Unavailable`: Database connectivity failure (`MSG127`).

---

### 2. PUT `/api/v1/traveler/profile`
Updates the complete editable profile representation for the authenticated Traveler. The endpoint always uses `multipart/form-data`, including updates without an avatar, so one unambiguous request contract covers avatar upload, replacement, and removal.

- **Authorization:** `[Authorize(Roles = "Traveler")]`; the command also verifies that the current `User.Status` is active before any upload or persistence work.
- **Content-Type:** `multipart/form-data` only
- **Request Form / Body:**

| Field | Type | Rules |
| :--- | :--- | :--- |
| `fullName` | `string` | Required, trimmed, 1–100 chars (`MSG01`, `MSG02`). |
| `phoneNumber` | `string` | Optional / 10 digits starting with `0` (`MSG04` / `MSG02`). |
| `dateOfBirth` | `string` (YYYY-MM-DD) | Optional; if provided, must be past date and age $\ge 16$ years (BR-18, `MSG02`). |
| `avatar` | `IFormFile` (file) | Optional; max 5 MB; MIME types: `image/jpeg`, `image/png`, `image/webp` (BR-16, `MSG19`). |
| `removeAvatar` | `bool` | Optional; if `true`, removes the current avatar. It cannot be `true` when a new `avatar` is supplied; that invalid combination returns `400`. |

- **Success Response:** `200 OK`
```json
{
  "messageCode": "MSG20",
  "message": "Profile updated successfully.",
  "profile": {
    "userId": 42,
    "fullName": "Nguyen Van A",
    "email": "traveler@example.com",
    "phoneNumber": "0912345678",
    "dateOfBirth": "1998-05-20",
    "avatarUrl": "https://res.cloudinary.com/tripmate/image/upload/v1/avatars/42_def456.jpg"
  }
}
```

- **Validation Failure Response:** `400 Bad Request`
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "fullName": ["MSG01"],
    "dateOfBirth": ["MSG02"]
  }
}
```

- **Error Responses:**
  - `400 Bad Request`: Validation rule violations (BR-16, BR-18, MSG01, MSG02, MSG19).
  - `409 Conflict`: A different account already owns `phoneNumber` (`MSG_PHONE_DUP`).
  - `401 Unauthorized`: Missing or invalid JWT access token.
  - `403 Forbidden`: Authenticated user is not a `Traveler` or the account is locked/inactive.
  - `503 Service Unavailable`: Cloudinary storage or SQL persistence failure (`MSG127`).

---

## Avatar Media Lifecycle & Cloudinary Storage

1. **Storage Service Interface:** `ITravelerAvatarStorage`
   - `UploadAsync(TravelerAvatarUpload upload, CancellationToken ct)` returns a successful HTTPS URL or a safe failure.
   - `DeleteAsync(string publicId, CancellationToken ct)` deletes by stable public ID.
2. **Cloudinary Asset Path:**
   - Public ID scheme: `tripmate/avatars/{userId}/{Guid:N}`.
   - Access mode: Public/Delivery image.
3. **Failure Isolation and Durable Cleanup:**
   - Per SRS §3.2.8 (Abnormal Case 7.b1): if Cloudinary upload fails, the previous avatar is retained, no profile values are persisted, and the system returns `503` with `MSG127`.
   - Before upload, the application writes an independent durable cleanup reservation. A successful profile transaction saves the public ID and URL, then removes that reservation while holding the per-asset SQL lock. On transaction failure, the reservation remains for retryable background deletion.
   - The previous avatar's cleanup task is enqueued in the same profile transaction and becomes visible only after commit. The handler never deletes the previous asset before the replacement/removal commit.
   - File signature validation verifies actual image bytes (magic bytes for JPEG, PNG, WEBP) in addition to MIME type headers.

---

## Security and Role Escalation Defenses

1. **Role Protection (BR-23):** The update command accepts only profile attributes. `User.Role` and `User.Status` are never bound or assigned.
2. **Identity Scoping:** The `userId` is obtained strictly from `ICurrentUserService.UserId` extracted from validated JWT claims, preventing horizontal privilege escalation.
3. **Email Read-Only (BR-17):** `User.Email` is excluded from update statements. Any attempt to supply a different email in the request body is discarded.
4. **Account State Protection:** Authorization is defense in depth: controller role authorization is supplemented by handler-level enforcement that the current user exists, has the `Traveler` role, and is in an active account status.
