# TM-207 — Operator Tour Media management API

Status: **APPROVED — 2026-09-26**
Jira: TM-207
Parent: TM-204 — Tour Catalogue Enrichment & Media
Repository: `FPTUCapstone/Capstone_BE`
Base: `origin/develop` at
`525489b61ee2a2791b3d0cf71313e80a4a0445e7`
Dependency: TM-206 merged as `1c8c8f0 feat(database): add tour media schema`
Product source: TM-205 merged into `Capstone_Docs/develop` by PR #7 at
`58ab994` (decision commit `7bf28d8`)
Date prepared: 2026-09-23 (Asia/Ho_Chi_Minh)

## 1. Purpose

Provide an authenticated backend workflow for an approved Tour Operator to
upload, list, edit metadata, reorder, choose the primary image, and remove
Tour-owned media for Tours owned by that Operator.

Cloudinary stores image assets. SQL Server stores only metadata and lifecycle
state. The API must preserve the TM-206 invariants, enforce the application
limit of at most 10 active images, record audit events, and never expose
Cloudinary credentials or unsigned upload authority.

## 2. Authoritative requirements

- Only the owning authorised Tour Operator may mutate a Tour's media.
- Administrator identity does not bypass Operator ownership on these
  Operator endpoints. Administrator review/moderation endpoints are separate.
- A Tour may have at most 10 active images.
- JPEG, PNG, and WebP are the only accepted formats.
- SQL Server stores no raw image bytes.
- Active ordering is positive, deterministic, and unique per Tour.
- A Tour has zero or one active primary image while being assembled.
- Normal media removal is a database soft delete:
  `lifecycle_status = 'Deleted'` and `deleted_at = UTC now`.
- A physical Tour deletion is the only case in which the TM-206 FK cascade
  physically removes TourMedia rows.
- Tour media and POI media remain completely separate.
- Media operations are audited with actor, Tour, action, and UTC timestamp.
- At least 3 active images including exactly one primary image is a
  submit/publish precondition, not a create/list/reorder/delete precondition.
- Public thumbnail and gallery contracts remain outside TM-207.

## 3. Proposed API contract

Base route: `/api/v1/operator/tours/{tourId}/media`

### 3.1 List active media

`GET /api/v1/operator/tours/{tourId}/media`

- Requires an authenticated Active `TourOperator` who owns the Tour.
- Returns only active rows ordered by `sortOrder`, then `tourMediaId`.
- Returns an empty array when the owned Tour has no active media.
- Does not return deleted rows, Cloudinary API keys/secrets, upload signatures,
  storage-internal values, or POI media.

### 3.2 Upload one image

`POST /api/v1/operator/tours/{tourId}/media`

- Content type: `multipart/form-data`.
- Required: `file`, `altText`, `Idempotency-Key` header.
- Optional: `caption`, `isPrimary` (default false).
- The server validates the file before uploading, generates the Cloudinary
  public identifier, uploads through the signed server-side SDK, then persists
  metadata.
- `sortOrder` is assigned by the server as the next active order. A client may
  not choose an arbitrary order during upload.
- A concurrent request cannot take the Tour above 10 active images.
- If `isPrimary = true`, the request succeeds only when no other active primary
  exists. Primary replacement is performed by the atomic reorder/primary
  endpoint, not by silently demoting an existing row during upload.
- Success: `201 Created` with the direct media DTO.

### 3.3 Edit metadata

`PATCH /api/v1/operator/tours/{tourId}/media/{mediaId}`

- Supports `caption` and `altText` only.
- Does not replace the image asset, change order, change lifecycle, or change
  primary state.
- Empty caption normalizes to null. Alt text remains required and nonblank.

### 3.4 Reorder and choose primary atomically

`PUT /api/v1/operator/tours/{tourId}/media/order`

- Body contains every currently active `mediaId` exactly once in desired order
  and optional `primaryMediaId`.
- Server assigns contiguous one-based sort order.
- `primaryMediaId` must be null or identify one active media row in the same
  Tour.
- Missing, duplicate, deleted, or foreign media IDs reject the complete
  request. No partial order changes are committed.
- Concurrent requests are serialized for the Tour. Last committed complete
  order wins; every committed state remains valid and deterministic.

### 3.5 Remove media

`DELETE /api/v1/operator/tours/{tourId}/media/{mediaId}`

- Performs database soft deletion only.
- Repeating deletion for the same owned media is idempotent and returns `204`.
- If the deleted row was primary, the Tour temporarily has zero active primary
  images. The server never promotes another image implicitly.
- Remaining active images are compacted to contiguous one-based order in the
  same transaction.

## 4. DTO contract

```text
TourMediaDto
  tourMediaId: long
  tourId: long
  deliveryUrl: absolute HTTPS URL
  caption: string? (max 500)
  altText: string (max 500)
  sortOrder: positive integer
  isPrimary: boolean
  createdAtUtc: ISO-8601 UTC timestamp
  updatedAtUtc: ISO-8601 UTC timestamp
```

`cloudinaryPublicId`, lifecycle internals, deletion timestamp, credentials,
upload signatures, and provider response payloads are never returned.

## 5. Validation and Cloudinary policy requiring approval

### Proposed initial policy

- Server-side signed Cloudinary uploads; no unsigned upload preset is exposed
  to Web or Mobile.
- Configuration names:
  `Cloudinary__CloudName`, `Cloudinary__ApiKey`,
  `Cloudinary__ApiSecret`, `Cloudinary__TourMediaFolderRoot`.
- Folder convention: `tripmate/tours/{tourId}`.
- Public identifier: server-generated opaque UUID; never derived from the
  original filename or delivery URL.
- Accepted content: JPEG, PNG, WebP, verified by decoded image content/magic
  bytes in addition to MIME and extension.
- Maximum encoded file size: **10 MiB**.
- Maximum decoded image area: **24 megapixels**; width and height must each be
  positive. No minimum dimension is imposed by TM-207.
- Strip original filename and avoid exposing embedded metadata. Cloudinary
  delivery remains HTTPS.
- No Cloudinary AI moderation add-on in TM-207. Publication remains gated by
  the approved Tour review workflow. Invalid/corrupt files fail before upload.

### Provider failure behavior

- Validation failure: `400` RFC 7807 validation details; no upload and no row.
- Cloudinary unavailable/rejected: `503` ProblemDetails; no row.
- Cloudinary upload succeeds but SQL write fails: perform best-effort asset
  cleanup and record a safe operational error without exposing credentials.
- The request must never report success unless the SQL row and audit event are
  committed.

## 6. Cloudinary retention/deletion decision

TM-206 approved database soft deletion but did not decide when the underlying
Cloudinary asset is destroyed.

**Recommended initial decision:** TM-207 soft-deletes the SQL row immediately
but does not physically destroy the Cloudinary asset inside the HTTP request.
Retain the asset for 30 days for recovery/audit, then delete it through a
persistent cleanup-outbox worker with bounded retry. A successful cleanup
invalidates the CDN resource. Failed cleanup remains retryable and does not
reactivate the SQL row.

This requires a small upgrade-safe cleanup-outbox table and background worker.
If the team does not approve the outbox in TM-207, Cloudinary physical deletion
must be explicitly deferred; a fire-and-forget delete is not acceptable.

## 7. Approval-state and material/minor classification

The existing Tour schema has `Draft`, `Pending`, `Approved`, `Rejected`, and
`Inactive` states but no versioned public-media revision. Mutating the only
media rows of an Approved Tour would expose unreviewed presentation or remove
the currently approved presentation.

### Proposed deterministic classification

- **Material:** upload/replace an image; remove an image; change primary image;
  reorder the gallery; change caption.
- **Minor:** alt-text-only correction that does not change the asset, caption,
  order, primary state, or visibility.

### Proposed state rules

- `Draft` and `Rejected`: all TM-207 mutations are allowed.
- `Pending`: list only; every mutation returns `409` because review is in
  progress.
- `Approved` and `Inactive`: list and alt-text-only correction are allowed.
  Material mutations return `409` until a separately approved versioned Tour
  revision/resubmission workflow exists.

TM-207 does not automatically change an Approved Tour back to Pending because
the current database has no separate approved public version to keep serving.
It also does not trust an Operator-supplied `isMinor` flag.

## 8. Authorization and information disclosure

- Controller role gate: `TourOperator` only.
- Application handler derives actor ID from the authenticated principal.
- Account must be Active and have an approved Operator profile according to the
  repository's authoritative account/application model.
- A Tour belonging to another Operator returns the approved non-disclosing
  resource error; ownership is never accepted from request data.
- Traveler and Administrator tokens cannot mutate via these endpoints.
- Cloudinary secret values, raw provider errors, stack traces, and other
  Operators' media identifiers must not appear in responses or logs.

## 9. Database and domain changes

TM-207 consumes `commerce.TourMedia` from TM-206 and adds the EF/domain model.
Jira requires alt text, but TM-206 has no `alt_text` column. Therefore TM-207
must add:

- `alt_text NVARCHAR(500) NOT NULL` for new canonical rows, with an
  upgrade-safe strategy for any pre-existing TM-206 row;
- a uniqueness guarantee for `cloudinary_public_id` so one provider asset is
  not mapped to multiple TourMedia rows; and
- only the operation/outbox persistence approved for idempotency and delayed
  cleanup.

Any schema change must update the full schema and add an idempotent SQL
migration. Fresh and upgraded databases must have identical canonical
inventory. No EF migration is used.

## 10. Idempotency, concurrency, and transaction rules

- Upload requires `Idempotency-Key`; its scope is actor + Tour + operation.
- The persisted operation stores a payload fingerprint including content hash,
  normalized caption/alt text, and primary intent.
- Same key/same fingerprint replays the original result without a second asset
  or row. Same key/different fingerprint returns `409`.
- Maximum-10 enforcement and next-sort assignment use a SQL Server transaction
  with a Tour-scoped lock/serializable equivalent.
- Reorder validates the full active set and commits order/primary changes as
  one transaction.
- Delete soft-deletion, order compaction, cleanup-outbox enqueue, and audit
  success are one database transaction.
- Expected unique/constraint conflicts map to stable ProblemDetails; unrelated
  database failures are not mislabeled as duplicates.

## 11. Audit contract

Record successful upload, metadata edit, reorder/primary change, and deletion.
Audit payload contains only safe metadata: actor ID, Tour ID, media ID, action,
before/after order/primary/lifecycle values, and UTC timestamp. It excludes
file bytes, credentials, signatures, raw provider responses, and unnecessary
URLs.

Authorization failures and persistence/provider failures use the established
safe failure-audit mechanism when applicable without changing the original
HTTP result.

## 12. Error contract

All failures use RFC 7807/ValidationProblemDetails with stable feature error
codes. Required mappings:

- `400`: invalid file/metadata/order payload or missing idempotency key;
- `401`: unauthenticated;
- `403`: wrong role/inactive or unapproved Operator account;
- `404`: unavailable/non-owned Tour or media according to the approved
  non-disclosure rule;
- `409`: media limit, duplicate/reused idempotency key with different payload,
  active-primary conflict, stale/invalid complete order, or locked Tour state;
- `413`: request/file exceeds the configured HTTP upload limit;
- `503`: Cloudinary temporarily unavailable;
- `500`: unexpected mapped failure with no sensitive details.

## 13. Verification requirements

- Domain tests for lifecycle, metadata bounds, primary, and immutable identity.
- Application tests for ownership, account/Tour state, maximum 10, ordering,
  deletion, material/minor rules, idempotency, and provider failures.
- SQL Server integration tests for alt-text migration parity, constraints,
  concurrent uploads at the 10-image boundary, atomic reorder, rollback, audit,
  operation replay, and cleanup outbox.
- HTTP integration tests through real authentication for every status above,
  multipart binding, content-type spoofing, and response redaction.
- Cloudinary adapter contract tests using a controlled fake/stub; one labelled
  development-account smoke upload/delete is required before deployment, with
  credentials and URLs redacted from evidence.
- Full Release build, format, complete backend suite, vulnerability scan, and
  `git diff --check` on the final head.

## 14. Explicit non-goals

- Operator Web or Mobile UI.
- Administrator media review/moderation UI or endpoint.
- Tour approval/versioning/resubmission implementation.
- Public search `thumbnailUrl` (TM-208).
- Public Tour Detail gallery (TM-210).
- Rating/review, favourites, recommendations, booking, POI media, or generic
  shared asset library.
- Hard deletion of TourMedia through normal media-removal endpoints.

## 15. Decisions requiring explicit approval

1. Approve the endpoint set and DTO in sections 3–4.
2. Approve server-signed Cloudinary integration and configuration/folder/public
   ID rules in section 5.
3. Approve 10 MiB and 24-megapixel upload boundaries.
4. Confirm TM-207 uses the already approved maximum of 10 active images per
   Tour and uploads one file per request.
5. Approve 30-day retention plus persistent cleanup outbox, or explicitly defer
   physical Cloudinary deletion.
6. Approve the material/minor classification and fail-closed Tour-state rules
   in section 7.
7. Approve mandatory nonblank alt text, maximum 500 characters, and the
   required upgrade migration.
8. Approve mandatory upload idempotency and persistent operation records.
9. Confirm Administrator is denied on these Operator endpoints; future Admin
   moderation remains separate.

## 16. Approval record

- The developer explicitly approved this specification on 2026-09-26 with
  `approve TM-207 spec`.
- Cloudinary configuration is present locally in the ignored `.env` using the
  `Cloudinary__...` .NET configuration-key form. Credential values were not
  printed, copied into source, or recorded in this specification.
- Implementation remains gated on explicit approval of the atomic plan at
  `plans/TM-207-plan.md`.
