-- TM-207 migration-test data applied after the frozen TM-206 baseline fixture
-- and the canonical TM-206 migration. This represents a deployed post-TM-206,
-- pre-TM-207 database with both active and soft-deleted Tour-owned media.

SET IDENTITY_INSERT commerce.TourMedia ON;

INSERT INTO commerce.TourMedia
    (tour_media_id, tour_id, cloudinary_public_id, delivery_url, caption,
     sort_order, is_primary, lifecycle_status, created_at, updated_at, deleted_at)
VALUES
    (7001, 5001, N'tripmate/tours/5001/legacy-active',
     N'https://res.cloudinary.com/tripmate/image/upload/legacy-active.webp',
     N'Existing active Tour image', 1, 1, 'Active',
     '2026-09-20T01:00:00', '2026-09-20T01:00:00', NULL),
    (7002, 5001, N'tripmate/tours/5001/legacy-deleted',
     N'https://res.cloudinary.com/tripmate/image/upload/legacy-deleted.webp',
     N'Existing deleted Tour image', 2, 0, 'Deleted',
     '2026-09-20T02:00:00', '2026-09-21T02:00:00', '2026-09-21T02:00:00');

SET IDENTITY_INSERT commerce.TourMedia OFF;
GO
