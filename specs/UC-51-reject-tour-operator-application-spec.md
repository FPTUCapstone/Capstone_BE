# UC-51 Reject Tour Operator Application Spec

## Status

Ready for Implementation (Updated per Architectural Review & SRS Alignment)

## Scope

This specification defines the Backend implementation for **UC-51: Reject Tour Operator Application**.

It covers:
- Validating the Administrator's rejection request and required rejection reason.
- Verifying preconditions (caller role, target user existence, role, and pending state).
- Updating `Users`, `OperatorProfiles`, and `OperatorDocuments` statuses in a single database transaction.
- Creating an `AuditLog` entry and a pending `Notification` row.
- Clarifying sign-in compatibility (UC-04 / BR-09 / BR-10 / UC-03 Resubmit flow) when `Users.status` is `Rejected`.
- Returning an Administrator confirmation response with `MSG116` (and documenting UI prompt `MSG115`).

---

## Actor

- **Administrator**

---

## Preconditions

1. Caller is authenticated.
2. Caller has the `Administrator` role.
3. Target user exists and has `UserRole.TourOperator`.
4. Target user `Status` is `AccountStatus.PendingApproval`.
5. Target user's `OperatorProfile` exists and `ApprovalStatus` is `OperatorApprovalStatus.PendingApproval`.
6. Rejection reason is provided (non-empty, non-whitespace, max 1000 characters).

---

## Account Status & Resubmit Alignment (BR-09 / BR-10 / UC-03)

### State Design Alignment:
- In `tripmate_schema_v7.sql`, `Users.status` supports `AccountStatus.Rejected` (value `5`) and `OperatorProfiles.approval_status` supports `OperatorApprovalStatus.Rejected` (value `3`).
- **Sign-In Compatibility (UC-04 / BR-10):** In `LoginCommandHandler.cs`, `AccountStatus.Rejected` is explicitly allowed to sign in (returns `null` blocking error, exactly like `PendingApproval`).
- **Resubmit Flow (UC-03):** Allowing a rejected Tour Operator to log in with `AccountStatus.Rejected` grants them an authenticated session restricted to viewing their rejection reason and resubmitting updated documents under UC-03.

---

## Application Messages Alignment

| Message ID | Type | Content / Usage |
|---|---|---|
| **MSG115** | Modal Prompt (UI) | `"Please enter specific rejection reason to send to applicant:"` *(Admin UI modal input label)* |
| **MSG116** | ToastMessage / API Response | `"Application rejected. Notification sent to operator."` *(API success response confirmation message)* |

---

## API Contract

### Endpoint

```http
POST /api/v1/admin/tour-operator-applications/{userId}/reject
Content-Type: application/json
Authorization: Bearer <Admin_JWT>
```

### Request Body

```json
{
  "reason": "The submitted business license could not be verified with government records."
}
```

### Success Response (`200 OK`)

```json
{
  "userId": 123,
  "accountStatus": "Rejected",
  "applicationStatus": "Rejected",
  "rejectionReason": "The submitted business license could not be verified with government records.",
  "reviewedBy": 1,
  "reviewedAt": "2026-09-10T20:38:00Z",
  "message": "Application rejected. Notification sent to operator."
}
```

---

## Business Logic & Execution Flow

When a valid request is received:

1. **Authorization Check:**
   - Verify `currentUserService.UserId` is not null.
   - Verify `currentUserService.Role == "Administrator"`.
   - Failure code: `admin.tour_operator_application_forbidden` (`403 Forbidden`).

2. **Input Validation:**
   - Trim `request.Reason`.
   - If empty or whitespace: Return failure code `admin.tour_operator_application_rejection_reason_required` (`422 Unprocessable Entity`).
   - If `Reason.Length > 1000`: Return failure code `admin.tour_operator_application_rejection_reason_too_long` (`422 Unprocessable Entity`).

3. **Target User & Profile Verification:**
   - Fetch `User` by `userId` (with tracking).
   - If `null`: `admin.tour_operator_application_not_found` (`404 Not Found`).
   - If `user.Role != UserRole.TourOperator`: `admin.tour_operator_application_wrong_role` (`422 Unprocessable Entity`).
   - If `user.Status != AccountStatus.PendingApproval`: `admin.tour_operator_application_not_pending` (`409 Conflict`).
   - Fetch `OperatorProfile` by `userId` including `Documents` (with tracking).
   - If `null`: `admin.tour_operator_application_not_found` (`404 Not Found`).
   - If `profile.ApprovalStatus != OperatorApprovalStatus.PendingApproval`: `admin.tour_operator_application_not_pending` (`409 Conflict`).

4. **Single-Transaction Persistence:**
   In one `SaveChangesAsync` call:
   - **Update User:**
     - `user.Status = AccountStatus.Rejected`
     - `user.UpdatedAtUtc = now`
   - **Update Profile:**
     - `profile.ApprovalStatus = OperatorApprovalStatus.Rejected`
     - `profile.RejectionReason = request.Reason.Trim()`
     - `profile.ReviewedBy = adminId`
     - `profile.ReviewedAtUtc = now`
     - `profile.UpdatedAtUtc = now`
   - **Update Documents:**
     - For all `profile.Documents` where `Status == DocumentStatus.Submitted`, set `Status = DocumentStatus.Rejected`.
   - **Create AuditLog:**
     - `ActorUserId = adminId`
     - `ActionType = "RejectOperatorApplication"`
     - `AffectedEntity = "OperatorProfile"`
     - `AffectedEntityId = userId`
     - `BeforeData` = JSON snapshot of previous statuses
     - `AfterData` = JSON snapshot of new statuses + rejection reason
     - `CreatedAtUtc = now`
   - **Create Notification:**
     - `User = user`
     - `Channel = NotificationChannel.Email`
     - `Type = "OperatorApplicationRejected"`
     - `Title = "Tour Operator Application Rejected"`
     - `Body = $"Tour Operator \"{profile.CompanyName}\" application was rejected. Reason: {request.Reason.Trim()}"`
     - `RelatedEntityType = "OperatorProfile"`
     - `RelatedEntityId = userId`
     - `Status = NotificationStatus.Pending`
     - `CreatedAtUtc = now`

5. **Confirmation Message (`MSG116`):**
   - Return success result with `MSG116`: `"Application rejected. Notification sent to operator."`

---

## Error Handling & Error Codes

| Error Code | HTTP Status | Description |
|---|---|---|
| `admin.tour_operator_application_forbidden` | `403` | Caller is not an Administrator |
| `admin.tour_operator_application_not_found` | `404` | Application or user not found |
| `admin.tour_operator_application_wrong_role` | `422` | Target user is not a Tour Operator |
| `admin.tour_operator_application_not_pending` | `409` | Application is no longer in PendingApproval state |
| `admin.tour_operator_application_rejection_reason_required` | `422` | Rejection reason is missing or empty |
| `admin.tour_operator_application_rejection_reason_too_long` | `422` | Rejection reason exceeds 1000 characters |

---

## Acceptance Criteria

1. Administrator can successfully reject a pending Tour Operator application by providing a reason.
2. Rejection changes `Users.status` to `Rejected` and `OperatorProfiles.approval_status` to `Rejected`.
3. Rejection records `rejection_reason`, `reviewed_by` (Admin ID), and `reviewed_at` (UTC timestamp).
4. Rejection changes submitted documents status to `Rejected`.
5. Rejection creates an `AuditLog` entry.
6. Rejection creates a pending email `Notification` entry targeting the operator.
7. Rejection returns `MSG116` confirmation message.
8. Non-administrators cannot invoke rejection (`403 Forbidden`).
9. Rejecting without a reason fails with `rejection_reason_required` (`422 Unprocessable Entity`).
10. Rejecting an application that is not in `PendingApproval` fails with `not_pending` (`409 Conflict`).
11. Unit tests pass 100%.
