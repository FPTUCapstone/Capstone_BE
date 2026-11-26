# UC-34 Backend API contract — Update Tour Operator Profile

Status: **implemented on `feature/uc34-update-operator-profile` (local, not pushed, not merged).** Authority:
`specs/UC-34-update-operator-profile-spec.md` (Approved 2026-11-26), SRS §2.2.2 UC-34 / §3.8.1, BR-07, BR-08,
BR-16, BR-17. For Capstone_FE (Web) and Capstone_Mobile. Both clients show the same Operator Profile screen.

## What changes for the FE

`src/features/operator/profile` currently answers `PENDING_BE_INTEGRATION` outside demo mode. Point it at the
endpoints below. The DTO already matches; these are the differences to handle:

| Topic | Contract |
| --- | --- |
| Route | One route, `/api/v1/operator/profile`: `GET` reads, `PUT` saves (multipart). |
| Read-only data | `businessLicenceNumber`, `taxCode`, `approvalStatus`, `accountEmail` come back on `GET` and in the `PUT` response. They are **never** writable: extra form fields with those names are ignored. |
| Required fields | Only `businessName`. The SRS names no other required field. |
| Phone | If sent, must match `^0\d{9}$` (same rule as operator registration) → `MSG04`. The FE currently validates no phone format: add this rule. |
| Email | `contactEmail`, if sent, must be a valid address → `MSG02`. Stored trimmed, **not** lower-cased. |
| Website | If sent, an absolute `http`/`https` URL of at most 300 characters. Not an MSG case: code `operator_profile.request_invalid`. |
| Success code | `code = "operator_profile.updated"`. It is deliberately **not** `MSG121`, because the SRS reuses that number for an unrelated message (see the FE types note). The FE picks the success copy. |
| Logo | Optional file (`logo`), JPEG/PNG/WebP, at most 5 MB, bytes must match the declared type → `MSG19`. `removeLogo=true` clears it. |
| Unset text | `businessDescription`, `businessAddress`, `contactPhone`, `contactEmail` are `""` when never entered. `website` and `logoUrl` are **omitted** when unset. |

## Auth

`Authorization: Bearer <operator token>`. Role `TourOperator`, account `Active`, application `Approved`.

| Caller | Result |
| --- | --- |
| no / invalid token | `401` |
| another role (Traveler, Administrator, Staff, ServiceProvider) | `403` |
| operator whose application is Pending or Rejected, or account Locked / Inactive / PendingContractSignature | `403`, `errorCode = "operator_tour.access_required"` (show `MSG126`) |

## `GET /api/v1/operator/profile`

`200`, `Cache-Control: no-store`:

```json
{
  "userId": 12,
  "businessName": "Sun Tours",
  "businessDescription": "Family tours",
  "businessAddress": "1 Beach Rd",
  "contactPhone": "0901234567",
  "contactEmail": "hello@suntours.vn",
  "website": "https://suntours.vn",
  "logoUrl": "https://res.cloudinary.com/.../tripmate/operator-logos/12/....png",
  "businessLicenceNumber": "79-1234/2026/TCDL-GPLHQT",
  "taxCode": "0312345678",
  "approvalStatus": "Approved",
  "accountEmail": "owner@suntours.vn"
}
```

`userId` is a JSON number. Commission rate and bank account are never returned (UC-46 owns the bank account).
An operator user without a profile row is refused with the same `403` (`404 operator_profile.not_found` only if the
row disappears between the access check and the read).

## `PUT /api/v1/operator/profile`

`Content-Type: multipart/form-data`. Request size limit 6 MB.

| Form field | Type | Rule | Error code |
| --- | --- | --- | --- |
| `businessName` | text | required, trimmed, ≤ 200 | `MSG01` / `operator_profile.request_invalid` |
| `businessDescription` | text | optional, ≤ 2000 | `operator_profile.request_invalid` |
| `businessAddress` | text | optional, ≤ 300 | `operator_profile.request_invalid` |
| `contactPhone` | text | optional, `^0\d{9}$` after trim | `MSG04` |
| `contactEmail` | text | optional, valid address, ≤ 200 | `MSG02` / `operator_profile.request_invalid` |
| `website` | text | optional, absolute http(s) URL, ≤ 300 | `operator_profile.request_invalid` |
| `logo` | file | optional, jpeg/png/webp, 1 byte to 5 MB, signature must match the type | `MSG19` |
| `removeLogo` | text | optional `true`/`false` | `operator_profile.request_invalid` |

Blank optional text (empty or whitespace) is stored as NULL, so sending an empty value **clears** the field.
Omitting an optional text field also clears it: the form is a full replacement of the editable fields, not a patch.
The logo is the exception: omit both `logo` and `removeLogo` to keep the current logo.

### Success `200`

```json
{
  "code": "operator_profile.updated",
  "message": "Operator profile updated successfully.",
  "profile": { "...same shape as GET..." }
}
```

### Field validation failure `400` (`application/problem+json`)

Same convention as `PUT /api/v1/operator/application/resubmit`, so `extractFieldErrors` works unchanged:

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

- `errors` is `field -> string[]` of stable codes, never free text. Only failing fields appear; the FE shows the first
  element.
- Keys are camelCase and equal the FE `OperatorProfileValidationErrors` keys: `businessName`, `businessDescription`,
  `businessAddress`, `contactPhone`, `contactEmail`, `website`, `logo`. One extra key exists: `removeLogo`.
- All failing fields are reported in one response.

### Other failures (no `errors` member)

| Status | `errorCode` | Meaning |
| --- | --- | --- |
| `403` | `operator_tour.access_required` | not an approved, active Tour Operator (`MSG126`) |
| `404` | `operator_profile.not_found` | profile row missing (rare) |
| `503` | `operator_profile.unavailable` | logo storage is down; **nothing was saved**, including the text fields (`MSG127`) |

### Logo behavior

- New `logo` replaces the current one. The old file is deleted after the save; a failed delete is only logged and
  never fails the request.
- `removeLogo=true` with no logo is a no-op: `200`, no error, safe to repeat.
- `removeLogo=true` together with a new `logo`: the new logo wins.
- A logo over 5 MB is not read into memory; it is answered with `MSG19` on `logo`.

### Request larger than the 6 MB limit

Measured with the in-process test server: a 7 MB body is answered **`400 Bad Request`**, not `413`, and nothing is
saved. The 400 comes from form reading and has no `errors` member. A real Kestrel host enforces
`[RequestSizeLimit]` itself and may answer `413`; **that was not verified against Kestrel.** The FE should treat both
`413` and a `400` without `errors` on this call as "file too large" and show the logo message (`MSG19`), and must
not rely on one of them.

## Schema

Migration `database/migrations/20261126_uc34_operator_profile_fields.sql` (idempotent) adds five nullable columns to
`dbo.OperatorProfiles`: `description NVARCHAR(2000)`, `contact_email NVARCHAR(200)`, `website_url NVARCHAR(300)`,
`logo_url NVARCHAR(500)`, `logo_public_id NVARCHAR(300)`. `tripmate_schema_v7.sql` carries the same columns. Run the
migration on Azure SQL **before** merging; `develop` auto-deploys.
