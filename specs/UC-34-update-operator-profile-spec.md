# UC-34 Update Tour Operator Profile — Backend Spec

Status: **APPROVED by the developer on 2026-11-26** (incl. §4.1, §4.2, §8 and every decision in §9). Implemented on branch `feature/uc34-update-operator-profile`.

## 1. Product outcome

An approved, active Tour Operator reads and edits the business information TripMate keeps about the
company: display/company name, description, head-office address, contact phone, contact email,
website and logo. Legal identifiers and approval state stay read-only (Direction 1, decided by the
developer). Source: SRS §2.2.2 UC-34 / §3.8.1; BR-07, BR-08, BR-16, BR-17; MSG01, MSG02, MSG19, MSG121,
MSG126, MSG127.

FE-first: `Capstone_FE/src/features/operator/profile` already defines the screen and DTO shape
(`OperatorProfileDto`, `UpdateOperatorProfilePayload`) and currently shows `PENDING_BE_INTEGRATION`.
This BE work exists to let FE switch from the pending state to real calls. Mobile follows later.

## 2. Scope

In scope:
- `GET  /api/v1/operator/profile` — current profile.
- `PUT  /api/v1/operator/profile` — update editable fields (multipart, logo optional).
- SQL migration adding the missing `dbo.OperatorProfiles` columns, plus the canonical
  `database/tripmate_schema_v7.sql` parity change.
- Public-logo storage on Cloudinary.

Non-goals / anti-goals:
- No re-verification flow. Tax code, business licence number, account email, approval status,
  commission rate and bank account (UC-46) are **never** writable here. A request that tries to
  send them is ignored by the binder; they never reach the handler.
- No Staff/Admin approval, notification or audit-log work. YAGNI; Traveler profile does not audit either.
- Traveler-facing display (tour detail showing the new fields) is **not** part of this task.
  The purpose line "presented to Travelers" is a follow-up on the Tour detail query.
- No change to `Users.full_name` (contact person) — not an FE-chosen field.
- No durable logo cleanup journal (see §6, my call).

## 3. Authorization

Caller must be role `TourOperator`, `OperatorProfiles.approval_status = Approved` and
`Users.status = Active`. Reuse `OperatorBookingAccess.ResolveOperatorIdAsync`
(`operator_tour.access_required` → 403, FE shows MSG126). Anyone else: 401 unauthenticated,
403 wrong role / not approved / not active. Pending, Rejected and locked operators cannot reach
this screen.

## 4. Contract

Property names follow the existing FE DTO.

`GET` 200:
```json
{
  "userId": 12,
  "businessName": "...",          // company_name
  "businessDescription": "...",   // new, nullable -> ""
  "businessAddress": "...",       // contact_address
  "contactPhone": "0901234567",   // contact_phone
  "contactEmail": "...",          // new
  "website": "https://...",       // new, nullable
  "logoUrl": "https://...",       // new, nullable
  "businessLicenceNumber": "...", // read-only
  "taxCode": "...",               // read-only
  "approvalStatus": "Approved",   // read-only
  "accountEmail": "..."           // read-only, Users.email
}
```
Nullable text columns are returned as `""`/omitted exactly as FE tolerates (FE treats them as strings;
`website` and `logoUrl` omitted when null).

`PUT` multipart/form-data, `RequestSizeLimit` 6 MB:

| Field | Rule |
|---|---|
| `businessName` | required, trimmed, ≤ 200 → else MSG01 |
| `businessDescription` | optional, ≤ 2000 |
| `businessAddress` | optional, ≤ 300 (existing column length) |
| `contactPhone` | optional; if present `^0\d{9}$` (same as operator registration, MSG04) |
| `contactEmail` | optional; if present valid address, ≤ 200 → MSG02 |
| `website` | optional; if present absolute `http`/`https` URL, ≤ 300 |
| `logo` | optional file; jpeg/png/webp, ≤ 5 MB (BR-16) → MSG19 |
| `removeLogo` | optional bool; ignored when a new `logo` is sent |

200 body: `{ "code": ..., "message": ..., "profile": <GET shape> }` (same envelope style as
`UpdateTravelerProfileResponse`). Failures use RFC-7807 via `HandleFailure(result)` with a field-level
`errorCode`. Codes live in a new `OperatorProfileErrorCodes` (zero magic strings):
`Forbidden`, `NotFound`, `RequestInvalid` (+ field errors), `InvalidLogo`, `Unavailable`.
MSG numbers are FE concerns; BE returns stable error codes, never MSG121 text duplicated as a message
conflict (the SRS has an MSG19/MSG121 numbering conflict recorded in the FE; BE does not decide it).

Writes set `updated_at`. Concurrent saves are last-write-wins (no business reason for stricter control).

### 4.1 Field-validation failure format (pinned)

Mirrors the existing `PUT /api/v1/operator/application/resubmit` response, which FE already reads through
`extractFieldErrors` (`Capstone_FE/src/lib/authErrorMapper.ts`: first element of each array, unknown codes fall
back to a safe message). Validation runs in the controller before dispatch via the Application validator, the
same way `OperatorApplicationController.Resubmit` does it, and the keys go through `ValidationErrorKeyNormalizer`
with `JsonNamingPolicy.CamelCase`.

`400 Bad Request`, `application/problem+json`:
```json
{
  "title": "One or more profile fields are invalid.",
  "status": 400,
  "errorCode": "operator_profile.request_invalid",
  "errors": {
    "businessName": ["MSG01"],
    "contactEmail": ["MSG02"],
    "contactPhone": ["MSG04"],
    "logo": ["MSG19"],
    "website": ["operator_profile.request_invalid"]
  }
}
```
- `errors` is a dictionary `field -> string[]` (array of stable codes, never free text). Only failing fields appear.
  Several failures on one field appear in the array in rule order; FE shows the first.
- Field keys are exactly the FE `OperatorProfileValidationErrors` keys: `businessName`, `businessDescription`,
  `businessAddress`, `contactPhone`, `contactEmail`, `website`, `logo`.
- Field codes: required → `MSG01`; bad email → `MSG02`; bad phone → `MSG04` (reuses `AuthErrorCodes`);
  bad/oversized/empty logo → `MSG19` (UC-34 §3.8.1 wins over the global appendix, as recorded in the FE types);
  too long, bad website URL, bad `removeLogo` → `operator_profile.request_invalid` (same convention as the
  resubmit endpoint's length rules).
- A request body above the 6 MB `RequestSizeLimit` is rejected by Kestrel with 413 before the controller runs;
  FE must map 413 to its logo message. Not changed by this task.
- Non-field failures use plain `HandleFailure(result)` (no `errors` member): 403 `operator_tour.access_required`,
  404 `operator_profile.not_found`, 503 `operator_profile.unavailable` (logo storage down; nothing persisted).

### 4.2 `removeLogo` semantics (pinned)

- `removeLogo=true` when the operator has no logo (`logo_url` and `logo_public_id` both NULL) is a **no-op**:
  200, no error, no storage call, response `logoUrl` omitted. Repeating the call is idempotent.
- `removeLogo=true` with a legacy row that has `logo_url` but no `logo_public_id`: clear both columns, skip the
  storage delete (nothing to delete by id).
- `removeLogo=true` together with a new `logo` file: the new logo wins and `removeLogo` is ignored (§4 table).
- `removeLogo` is the only way to clear the logo; omitting both fields leaves the current logo untouched.
- The remove path never contacts Cloudinary unless a `logo_public_id` exists; a failed best-effort delete of the
  old asset never fails the request (§6).

## 5. Data model

Migration `database/migrations/20261126_uc34_operator_profile_fields.sql`, idempotent
(`IF COL_LENGTH(...) IS NULL ALTER TABLE ... ADD`), all columns NULL so existing rows need no backfill:

| Column | Type |
|---|---|
| `description` | NVARCHAR(2000) NULL |
| `contact_email` | NVARCHAR(200) NULL |
| `website_url` | NVARCHAR(300) NULL |
| `logo_url` | NVARCHAR(500) NULL |
| `logo_public_id` | NVARCHAR(300) NULL |

Mirror in `tripmate_schema_v7.sql`, `OperatorProfile` entity (private-safe setters per current style,
one domain method `UpdateBusinessProfile(...)` guarding lengths), `OperatorProfileConfiguration`, and the
migration-contract test pattern already used by the repo. Azure SQL migration is run **before merge**
(develop auto-deploys) — operational step in the handoff, not in code.

## 6. Logo handling (my call — please confirm)

The Traveler avatar pipeline (durable cleanup journal + background service) is hard-wired to
`Users.avatar_public_id` and the `tripmate/avatars/` root, so it cannot be reused without a refactor.
Decision: add a small `IOperatorLogoStorage` (Cloudinary, root `tripmate/operator-logos/{userId}/{guid}`,
public delivery URL, same type/size checks) and keep the handler compensating:
1. upload new logo → 2. commit DB transaction → 3. best-effort delete of the old asset;
on DB failure delete the new asset. Failures are logged; a rare orphaned asset is accepted.
Rejected alternative: generalising the avatar journal (correct but a multi-file refactor of working
Traveler code for a rarely changed logo — can be a follow-up if orphans matter).

## 7. Existing behavior that must not change

- `company_name` is snapshotted into `OperatorContract` at issue time, so renaming does not alter
  signed contracts. Live screens that read `CompanyName` (admin tour review, reports, bookings search)
  will show the new name — intended.
- Registration and `resubmit` rules, tax code / licence uniqueness (BR-08), UC-46 bank account.

## 8. Test plan (outline; detail goes in the plan)

Application unit tests: authorization matrix (unauthenticated role, Traveler, Pending, Rejected,
inactive), each validation rule, trimming/nulling, read-only fields untouched, logo upload/replace/
remove/compensation paths, `removeLogo` no-op when no logo exists (no storage call, 200) and when only
`logo_url` exists, `removeLogo` + new `logo` (new wins). Validator tests. API-level tests: the §4.1 400 body
(`errorCode`, camelCase `errors` dictionary of string arrays, only failing fields, MSG codes per rule) and that
tax/licence/email/status form fields cannot change values. SQL-backed migration test (idempotent re-run,
defaults NULL).

## 9. Decisions I made that you should check

1. Only `businessName` is required (column is NOT NULL); all other fields optional because FE records
   the SRS names no other required field.
2. Phone uses the registration rule `^0\d{9}$` although FE currently validates no phone format —
   FE should align.
3. Max lengths (description 2000, email 200, website 300) are mine; the SRS gives none.
4. Logo cleanup strategy in §6.
5. Last-write-wins, no audit log, no Staff notification.
6. `GET`/`PUT` share route `/api/v1/operator/profile` (mirrors `traveler/profile`).
