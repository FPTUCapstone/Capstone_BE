# TM-206 — Tour Media schema and upgrade-safe migration

Status: **IMPLEMENTED AND LOCALLY VERIFIED — 2026-09-23**
Jira: TM-206
Source of truth: `Capstone_Docs/requirements/tour-catalogue-enrichment.md`
(`7bf28d8`)

## 1. Scope

Create the database representation for Tour-owned images and an explicit SQL
Server migration that upgrades an existing TM-70 database to the same relevant
schema inventory as a fresh database.

This task is database-only. It creates no Cloudinary integration, upload/delete
endpoint, authentication/authorisation code, public API field, Web UI, or
Mobile UI. Those are covered by TM-207 through TM-210.

## 2. Approved product rules consumed by this task

- Cloudinary stores and delivers image assets; SQL Server never stores raw
  image binaries.
- SQL Server stores Tour-image metadata, Cloudinary public identifier, delivery
  URL, display order, primary-image state, and lifecycle information.
- Each Tour may have at most 10 active images. TM-207/application workflow,
  not TM-206 SQL constraints, enforces that maximum.
- TM-206 enforces at most one active primary image and no duplicate active
  display order when images have been selected for public presentation.
- A Tour needs at least three active images, including one primary image, before
  it may be submitted for approval or published. This is a future write/publish
  workflow rule; TM-206 must not reject historical Draft or existing Tour rows.
- A disabled, deleted, rejected, or non-public image must not be returned from
  a future public API. TM-206 only supplies the persistent lifecycle state.
- Normal Operator image removal is a soft deletion: set
  `lifecycle_status = 'Deleted'` and `deleted_at` to current UTC time. It must
  not hard-delete a `TourMedia` row.
- `ON DELETE CASCADE` applies only when `commerce.Tours` is physically deleted.
  Approved, Published, or historically referenced Tours must not be hard-deleted
  by the normal business workflow. A non-published Draft with no booking or
  dependency may be physically deleted; only then are its media rows deleted
  physically by cascade.
- POI media remains in `catalog.POIPhotos`; no FK, fallback, migration, or data
  copy between POI and Tour media is allowed.

## 3. Proposed physical model requiring approval

New table: `commerce.TourMedia`

| Column | Proposed SQL type | Null | Purpose |
| --- | --- | --- | --- |
| `tour_media_id` | `BIGINT IDENTITY(1,1)` | No | Surrogate primary key. |
| `tour_id` | `BIGINT` | No | Parent Tour FK with `ON DELETE CASCADE`. |
| `cloudinary_public_id` | `NVARCHAR(500)` | No | Cloudinary asset identifier; never inferred from a delivery URL. |
| `delivery_url` | `NVARCHAR(1000)` | No | Canonical HTTPS URL returned by Cloudinary for the selected asset. |
| `caption` | `NVARCHAR(500)` | Yes | Optional operator-managed caption. |
| `sort_order` | `INT` | No | Positive, deterministic gallery order. |
| `is_primary` | `BIT` | No | Marks the Tour's selected cover image; defaults to `0`. |
| `lifecycle_status` | `VARCHAR(16)` | No | Values: `Active`, `Deleted`; defaults to `Active`. |
| `created_at` | `DATETIME2` | No | UTC creation timestamp; defaults to `SYSUTCDATETIME()`. |
| `updated_at` | `DATETIME2` | No | UTC timestamp of last metadata/lifecycle update; defaults to `SYSUTCDATETIME()`. |
| `deleted_at` | `DATETIME2` | Yes | UTC soft-delete timestamp; required when lifecycle is `Deleted`. |

Proposed constraints and indexes:

1. `PK_TourMedia` primary key on `tour_media_id`.
2. `FK_TourMedia_Tours`: `tour_id → commerce.Tours(tour_id)` with cascade
   delete, matching the ownership semantics of Tour-owned content.
3. `CK_TourMedia_SortOrderPositive`: `sort_order > 0`.
4. `CK_TourMedia_Lifecycle`: only `Active` or `Deleted`.
5. `CK_TourMedia_DeletedAt`: an `Active` row has `deleted_at IS NULL`; a
   `Deleted` row has `deleted_at IS NOT NULL`.
6. `UX_TourMedia_ActiveSortOrder`: filtered unique index on
   `(tour_id, sort_order)` where `lifecycle_status = 'Active'`.
7. `UX_TourMedia_ActivePrimary`: filtered unique index on `tour_id` where
   `lifecycle_status = 'Active' AND is_primary = 1`, enforcing at most one
   active primary image without preventing Tours that have no image yet.
8. `IX_TourMedia_TourLifecycleOrder`: lookup index for a future gallery read,
   ordered by `tour_id, lifecycle_status, sort_order, tour_media_id`.

The "at least three active images" rule is deliberately not a SQL constraint:
it is evaluated only at submit/publish time by a later approved write workflow.
This preserves current Tour records and lets a Draft be assembled incrementally.

## 4. Migration requirements

- Add the canonical table, constraints, and indexes to
  `database/tripmate_schema_v7.sql`.
- Add one new dated script under `database/migrations/`; the exact date prefix
  follows the repository migration sequence when implementation begins.
- The migration must run in a transaction with `XACT_ABORT ON` and be
  idempotent.
- It must create absent objects, validate the complete expected shape of
  existing same-named objects, and fail without partial changes when a shape is
  incompatible.
- It must not modify existing `commerce.Tours`, `catalog.POIPhotos`, Tour
  schedules, or historical rows.
- It must validate the full table inventory: columns (type, length,
  nullability, collation/default/identity), PK, FK action, check definitions,
  and filtered/unfiltered index keys and predicates.

## 5. Test requirements

Add SQL Server integration coverage that:

1. verifies fresh schema shape for `commerce.TourMedia`;
2. upgrades a recorded pre-TM-206 baseline and compares its full TourMedia
   inventory with fresh schema;
3. proves applying the migration twice is safe;
4. rejects a same-named wrong-shape table/index/constraint without partial
   migration effects;
5. proves a physical Tour deletion cascades to its media rows while normal
   soft-deleted media rows remain present and POI photos are unaffected; and
6. proves the filtered indexes allow zero active primary images, allow one
   active primary image, and reject duplicate active primary/order values.

## 6. Explicit non-goals

- Cloudinary credentials, SDK, upload preset, folder naming, transformations,
  moderation, retention, or physical asset deletion.
- Operator media management API and material/minor edit classification (TM-207).
- `thumbnailUrl` in `GET /api/v1/tours` (TM-208).
- Tour Detail gallery API/UI (TM-210).
- Rating, review, favourite, booking, recommendation, category, language,
  inclusion, cancellation-policy, or generic shared asset work.

## 7. Decisions needing developer approval

The addendum approves the information categories but not these physical rules.
They must be approved with this spec before a migration is written:

1. Accept `Active` and `Deleted` as the initial lifecycle vocabulary, with a
   soft-delete timestamp, rather than introducing Pending/Rejected states now.
2. Accept `NVARCHAR(500)` for Cloudinary public identifier and caption, and
   `NVARCHAR(1000)` for delivery URL.
3. Accept `sort_order > 0` (rather than zero-based order).
4. Accept cascade deletion of TourMedia only when its parent Tour is physically
   deleted; normal Operator image removal is soft deletion.
5. Accept the two filtered unique indexes as the database enforcement for
   active primary image and active display order.

## 8. Acceptance criteria

- [x] This specification is approved explicitly by the developer on 2026-09-23.
- [x] The atomic plan, including the zero-or-one active primary clarification,
  is approved explicitly by the developer on 2026-09-23.
- [x] Full schema and upgrade migration have matching TourMedia inventory.
- [x] All migration integration tests and the backend suite pass.
- [x] No API/UI/Cloudinary credential behaviour is introduced by TM-206.
