# UC-02 Register Tour Operator Account Backend Specification

## Status

**Approved design 2026-10-01; UC-02 message constants decided 2026-10-04**
(MSG157-MSG160). The UC-50 review-list dependency remains. No implementation is
merge-ready until the required decisions and registration/verification integration tests pass. Branch
`feature/linhnv-register-tour-operator` (BE baseline `aba9465`; FE baseline `2170bc9`). Written
following the established UC-50 approve-flow patterns (the review queue this UC feeds) and
SRS §3.2.2 read in full. Implementation follows `plans/UC-02-plan.md`.

## Business identifier format decision (2026-10-08)

After trimming surrounding whitespace, `taxCode` must be 10 ASCII digits, or
10 ASCII digits followed by `-` and 3 ASCII digits for a branch. Examples:
`0101234567`, `0315678901-001`. `businessLicenseNo` must be a two-digit province
code, `-`, one or more serial digits, `/`, a four-digit issue year, `/`, and
exactly `TCDL-GPLHQT` (international) or `SDL-GPLHND` (domestic). Examples:
`79-0123/2026/TCDL-GPLHQT`, `01-0456/2025/SDL-GPLHND`. The serial width was
not specified, so only numeric content is checked. This checks format, not
government issuance or document authenticity; Administrator review remains
authoritative.

The BE returns field-level `OPERATOR_TAX_CODE_INVALID` and
`OPERATOR_TRAVEL_LICENSE_INVALID` for malformed non-empty values before Firebase
lookup, document upload, or SQL write. Required and maximum-length checks remain.
Web and Mobile use the same pre-submit checks and never rewrite text during input.

## Sources

- SRS §3.2.2 "Register Tour Operator Account" — Guest submits account + company info +
  business documents; the system creates a `TourOperator`-role account with status
  `Pending Approval` and an application (OperatorProfile) with status `PendingApproval`
  linked to the documents, placed into the UC-50/UC-51 review queue (PC-01…PC-04).
- Section business rules: BR-01 (email uniqueness), BR-02 (password policy), BR-03 (hashed
  storage), BR-07 (mandatory documents; locked until approved), BR-08 (license/tax-code
  uniqueness).
- Existing patterns followed (approve flow): `OperatorProfile` + `OperatorDocuments`
  entities and statuses (`PendingApproval` profile; documents `Submitted`),
  `AccountStatus.PendingApproval` (already gates the operator workspace, never sign-in),
  `ApproveOperatorApplicationCommandHandler` reading exactly the records this UC creates.
- Locked messages §5.3 — **multiple SRS references are wrong and reconciled below (M-table)**;
  existing locked entries were checked against `dbo.Messages`. The UC-02-only extension
  below is defined in BE constants and is not added to that table.

## Message reconciliation (SRS reference → locked content)

| Context | SRS cites | Locked content of that code | Correct match / resolution |
| --- | --- | --- | --- |
| Required field empty | MSG01 | "This field is required." | **MSG01 VALID** |
| Email format invalid | MSG02 | "Invalid email format…" | **MSG02 VALID** |
| Password policy | MSG03 | MSG03 is the email-exists message — wrong ref | **MSG05** (locked: password policy) — R-a |
| Confirm Password mismatch | MSG04 | MSG04 is the phone-number message | **MSG06** (locked: "Passwords do not match. Please re-enter.") - leader re-review point 2 |
| Email already registered | MSG05 | MSG05 is the password-policy message — wrong ref | **MSG03** (locked: "An account with this email already exists…") — R-b |
| Mandatory document missing | MSG22 | MSG22 is the travel-preferences message | **MSG157** "Please upload the required business licence document." |
| File type/size invalid | MSG19 | MSG19 is the profile-updated toast | **MSG158** "The uploaded file type is not supported or the file exceeds the size limit." |
| License/tax code already registered | MSG26 | MSG26 is the POI-updated toast | **MSG159** "This business licence number or tax code is already registered." |
| Duplicate pending application | MSG23 | MSG23 is the unsaved-changes modal | **MSG160** "An application is already pending review for this business information." |
| Submission success / verification handoff | MSG21 | MSG21 is the travel-preferences toast - wrong ref | **MSG08** for the business-submission toast; **MSG07** (locked: "Account registered successfully! Please verify your email/OTP to activate your account.") for the verification handoff |
| Storage/system failure | MSG127 | "TripMate is temporarily unable…" | **MSG127 VALID** |

Decision (2026-10-04, revised by the UC-02 feature owner): the erroneous SRS references
are corrected with MSG157-MSG160. They are a dedicated UC-02 extension, outside the
original SRS §5.3 catalog (MSG01-MSG130); MSG131-MSG156 are reserved by other UC
references. The stable codes remain in `AuthErrorCodes` and the four English texts live in
`OperatorRegistrationMessages`. The API returns codes in field errors and code plus
title for business failures; FE maps codes to the agreed text and placement. These
feature-local messages do **not** require rows in
`dbo.Messages` or a message-seed migration. Keep locked MSG19/22/23/26 unchanged. The
authored SRS should be amended to point UC-02 to these codes; this spec records the
approved correction until that document is updated.

## Document storage (approved 2026-10-01 — Option A)

**Business document storage** — BR-07 makes the business licence document mandatory, and
`dbo.OperatorDocuments.file_url` needs a real storage target. Approved: **multipart upload
through the BE → server-side Cloudinary raw upload → store `file_url`**, behind a thin
`IOperatorDocumentStorage` abstraction (Application port + `CloudinaryOperatorDocumentStorage`
in Infrastructure, unit-testable with a fake `ICloudinaryClient`). Types: PDF/JPG/PNG; max
5 MB per file, max 5 supporting files (limits recorded; SRS leaves them open). PDF uploads use
Cloudinary's `raw` resource type (the shared client was extended with an `IsRaw` upload flag —
the image pipeline would reject PDFs). Rejected: deferring uploads (violates BR-07 and empties
the UC-50 review queue).

**Security amendment (leader review, 2026-10-08):** Operator documents must use Cloudinary
`authenticated` delivery type for both raw PDFs and images. Persist an opaque provider
reference in `file_url`, never a public `SecureUrl`. The Administrator-authorized UC-50
detail endpoint converts only owned authenticated references into signed download URLs
that expire after five minutes; legacy public URLs are not returned. The anonymous
multipart registration endpoint is limited to five requests per remote IP per minute,
with no queue and a 429 response before MVC binds the form. Existing documents uploaded
under Cloudinary's public `upload` delivery type require a separate provider migration
and invalidation of old URLs; hiding them from the API does not revoke previously shared URLs.

## Email verification (required implementation work)

`AccountEligibilityResolver` only resolves sign-in for a `PendingApproval` TourOperator whose
persisted `EmailVerifiedAtUtc` is present and plausible. The existing verify handlers set the
marker **only** for `PendingEmailVerification`; `PendingApproval` falls through without the
marker being set. A small BE amendment is therefore part of this UC's scope:

- `WebVerifyEmailCommandHandler` and `ConfirmEmailVerificationCommandHandler`: for a
  `PendingApproval` TourOperator whose Firebase evidence is genuinely verified
  (`evidence.EmailVerified` / `verificationStatus.IsVerifiedAsync`), set
  `EmailVerifiedAtUtc = now` and **preserve** the `PendingApproval` status (do NOT flip to
  `Active` — the workspace remains gated until UC-50 approves). The marker is never
  fabricated: it comes only from Firebase-verified evidence.
- `WebPasswordSignInCommandHandler`: after the submitted password has been validated, an
  `AccountStateUnresolved` outcome for an unverified `PendingApproval` operator may invoke
  `ConfirmEmailVerificationCommand` and retry sign-in once. The confirmation checks Firebase's
  verified flag and preserves `PendingApproval`; wrong passwords and other account states
  never bypass the normal eligibility checks. This recovers verification when a link was
  opened without the original Firebase browser session.
- While a Firebase browser session exists, the FE resends via `sendEmailVerification`; if it
  has been lost, the verification UI must first restore that Firebase session using the
  applicant's Firebase credentials. Neither path creates a second BE account.

**Full flow (end-to-end):**
1. After client-side validation, the FE creates the Firebase identity with
   `createUserWithEmailAndPassword`, obtains its ID token, and POSTs the multipart form with
   `firebaseIdToken`. It does **not** send a verification email before BE commit.
2. The BE validates the Firebase token and requires its email to equal the normalized form
   email before uploading documents. An unverified Firebase token binds the registration to
   that Firebase identity; it is **not** evidence that the email address is verified. The BE
   hashes the password and creates the User (`TourOperator`, `PendingApproval`,
   `EmailVerifiedAtUtc = null`) + OperatorProfile + OperatorDocuments in one SQL transaction.
3. After a `201` response, the FE sends the verification email with continue URL
   `/verify-email?flow=operator`. Delivery failure keeps the committed application and offers
   resend; it never retries the registration to send the email. The operator follows the link;
   the operator branch of `/verify-email` calls `POST /api/v1/auth/web/verify-email` with a
   fresh verified Firebase ID token. It does not call the traveler `/auth/verify-email`
   endpoint or store session tokens.
4. **BE amendment** (above): the handler sets `EmailVerifiedAtUtc = now` and keeps
   `PendingApproval`.
5. Sign-in resolves via `AccountEligibilityResolver` (marker + profile match) →
   `/partner/application` shows Pending Review; the operator workspace stays locked until
   UC-50 approves. If the link was opened without a Firebase session, password sign-in
   performs the verified-status reconciliation described above.

On a deterministic BE rejection, the FE deletes only the Firebase identity it just created,
best-effort. After a network/transport failure the BE commit outcome is unknown: the FE must
not delete that identity; it retries with the same Firebase identity/token and treats a
confirmed pre-existing BE account as a recovery/handoff, not a reason to create a second
Firebase user. These recovery and email-delivery branches require tests.

Until the BE amendment (step 4) is implemented, the flow is incomplete and the
"register - verify - sign in" manual test cannot pass — this is tracked as a required task
in `plans/UC-02-plan.md`.

## Field mapping for contact person (leader re-review point 3 - resolved)

- `contactPerson` is **required** (MSG01 when empty) and is stored in `dbo.Users.full_name`
  — the registrant's full name; the schema has no dedicated contact-person column.
- `businessAddress` (optional) → `OperatorProfiles.contact_address`; `contactPhone`
  (optional, MSG04 format when present) → `OperatorProfiles.contact_phone`.
- `companyName`, `businessLicenseNo`, `taxCode` → their OperatorProfiles columns.
- The FE must require Contact Person and relax Business Address / Contact Phone to optional
  (the current prototype requires every text field — amended in the FE plan).

## BR-08 hardening (leader re-review point 4 - resolved)

The schema has a unique constraint on `tax_code` but none on `business_license_no`, so a
handler-level pre-check cannot prevent two concurrent registrations. Resolution:

1. **Database-first migration** `database/migrations/20261002_add_operator_license_unique.sql`:
   idempotent; fails loudly if duplicate licence numbers already exist, then creates the
   filtered unique index `UX_OperatorProfiles_BusinessLicenseNo` on
   `business_license_no` (non-empty). Deploy order: migration → schema verification →
   backend (same rule as the audit migration; Compose `db-init` applies it automatically).
   Decision (2026-10-04): existing duplicates must be resolved manually by the data owner;
   the migration must not delete, overwrite, or silently choose a record.
2. **Failure classification**: the handler recognizes SQL 2601/2627 and the violated
   constraint/index: `UX_Users_Email` → `409` locked MSG03;
   `UQ_OperatorProfiles_TaxCode` or `UX_OperatorProfiles_BusinessLicenseNo` → `409`
   MSG159. Other persistence failures map to `503` MSG127. Test each constraint,
   including concurrent collisions.
3. The application-level pre-checks remain (fast, friendly errors) but are no longer the
   only defense.

## Document compensation (leader re-review point 5 - resolved)

The document upload happens before the database save, so "one transaction" covers only SQL.
Resolution: `IOperatorDocumentStorage` gains `DeleteAsync(publicId, contentType)` (the
Infrastructure implementation wraps `ICloudinaryClient.DestroyAsync` with the matching
resource type — raw for PDFs, image for JPG/PNG). The handler tracks the uploaded public
ids and, on any subsequent failure (validation, SQL), **deletes the uploaded files
best-effort** and logs the outcome. The spec claims: no database records survive a failure,
and uploaded files are compensated best-effort — orphaned files can remain only if the
storage delete itself fails, and that limitation is logged, not hidden.

## SRS conflict record — operator role timing

SRS §3.9 (UC-50 flow text) says the Tour Operator role exists only after approval, while
§3.2.2 (UC-02) and the merged UC-50 approve code assign the `TourOperator` role at
registration with `status = PendingApproval` gating the workspace until approval.
**Resolution:** the role is assigned at registration; workspace authorization is gated by
the account status and the profile's `approval_status`, which the approve flow flips to
`Approved`. This matches the merged UC-50 implementation and the
`AccountEligibilityResolver`; the SRS wording conflict is recorded here, not "fixed" in the
SRS.

## PC-03 review-queue dependency

The current Admin application API exposes `GET /api/v1/admin/tour-operator-applications/{userId}`
and approve/reject operations, but has no collection/list endpoint; FE has the detail route
only. UC-02 can create a pending profile and prove it is readable through the Admin detail
endpoint, **but cannot yet prove SRS PC-03 (visible in a review queue)**. A reviewed owner
must deliver the queue/list capability or approve a scoped exception before UC-02 is claimed
fully SRS-complete. This specification does not imply the queue exists today.

## Scope

One Guest endpoint: `POST /api/v1/auth/register/operator` (multipart/form-data). Creates, in
**one transaction** (SRS 9.a1): a `dbo.Users` row (role `TourOperator`, status
`PendingApproval`, hashed password per BR-03) + `dbo.OperatorProfiles` (status
`PendingApproval`, `pending` review semantics = the approve flow's precondition) +
`dbo.OperatorDocuments` rows referencing uploaded files. The Firebase identity is created
client-side at registration and the operator completes the existing email-verification flow
before sign-in resolves (see Email verification); the operator workspace stays locked until
approval, matching the existing account-eligibility gates. **No audit row** — §3.2.2 defines
none (the approve/reject events are audited instead).

Uniqueness (checked before any insert): a matching **pending application for the same email**
returns MSG160 first; otherwise a duplicate email returns locked MSG03. Tax code
uses its existing DB constraint and business licence number uses the new migration's unique
index (both MSG159). The migration must be applied before the endpoint is deployed.

## Contract

`POST /api/v1/auth/register/operator` — `multipart/form-data`:

| Field | Required | Validation |
| --- | --- | --- |
| `email` | yes | MSG02 format; BR-01 uniqueness → MSG03 |
| `firebaseIdToken` | yes | Valid Firebase token; token email must match `email` before upload. It does not establish email verification. |
| `password`, `confirmPassword` | yes | BR-02 policy → MSG05; mismatch → locked MSG06 |
| `companyName` | yes | MSG01 |
| `businessLicenseNo` | yes | MSG01; BR-08 uniqueness → MSG159 |
| `taxCode` | yes | MSG01; BR-08 uniqueness → MSG159 |
| `contactPerson` | yes | MSG01 (stored into `Users.full_name` — see field mapping) |
| `businessAddress`, `contactPhone` | no | lengths per schema; phone format per MSG04 when present |
| `businessLicenseDocument` | yes | missing → MSG157; MIME, extension, and leading PDF/JPEG/PNG signature must agree; type/size → MSG158; stored (DEC) |
| `supportingDocuments` | no | same type/size checks per file → MSG158; at most five files → `auth.request_invalid` with a field-level count message |
| `acceptTerms` | yes | must be true |

Responses: `201` with `{ userId, applicationStatus: "PendingApproval", messageCode: "MSG08" }`.
The FE displays locked MSG08, then sends the verification email and displays locked MSG07
as the handoff; MSG07 is not a second BE response field. Missing token and other malformed
form fields → `400`; invalid/expired Firebase token → `401` (`AUTH_TOKEN_INVALID`); verified
token email mismatching the form email → `400` (`AUTH_EMAIL_MISMATCH`), all before upload.
Other `400` field errors use MSG01/02/05, locked MSG06 mismatch, or MSG157/158.
`409` email exists (MSG03), license/tax-code or
duplicate-application collisions (MSG159/MSG160 — concurrent registrations included,
BR-08 hardening); `503` storage/system failure (MSG127). Password mismatch is **field-level**
(locked MSG06). Contact Person is required (→ `Users.full_name`); Business Address / Contact
Phone are optional.

## Acceptance criteria

1. A successful submission creates exactly one User (`TourOperator`, `PendingApproval`,
   `EmailVerifiedAtUtc = null`) + one OperatorProfile (`PendingApproval`) + the document rows,
   in one database transaction with best-effort document compensation (PC-01/PC-02); the
   profile is readable by the existing UC-50 Admin detail endpoint. PC-03 queue visibility
   remains the explicit dependency above; no operator function is accessible before approval
   (PC-04).
2. Uniqueness: duplicate email → MSG03; duplicate tax code or licence → MSG159
   (409 — enforced by the DB unique indexes incl. the new license index under concurrent
   registrations, BR-08 hardening); duplicate pending application for the same email →
   MSG160. No partial database records on any failure (9.a1/9.a2) and uploaded
   files are compensated best-effort (document compensation section).
3. Password hashed (BR-03); policy per BR-02 → MSG05. Email verification: the marker is set
   only by the real verification flow (never fabricated); sign-in resolves per the
   `AccountEligibilityResolver` contract after the operator completes it.
4. Documents validated (type/size → MSG158) and stored per DEC; missing licence
   document → MSG157; storage failure → `503` MSG127 with nothing persisted (7.c1).
5. Success returns locked MSG08; the FE sends the verification email only after the `201`
   response, then presents locked MSG07. Delivery failure preserves the application and
   offers resend without submitting a second registration.
6. Unit tests: validator, uniqueness branches, mismatch, atomicity; API integration tests:
   201/400/409/503 flows; SQL Server tests: real-schema persistence incl. documents and the
   uniqueness constraints — **executed locally with a real SQL Server before every push**.

## Non-goals

Approval/rejection (UC-50/UC-51); operator workspace unlock; resubmission (UC-03);
login behavior changes beyond the verify-handler amendment above and the existing
`AccountPendingApproval` gating.
