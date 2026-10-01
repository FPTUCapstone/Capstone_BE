-- Historical itinerary items deliberately remain NULL; no inferred backfill/default.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'planning.ItineraryItems', N'U') IS NULL
    THROW 50001, 'planning.ItineraryItems must exist before applying this migration.', 1;

IF COL_LENGTH(N'planning.ItineraryItems', N'friendly_explanation') IS NULL
    ALTER TABLE planning.ItineraryItems
        ADD friendly_explanation NVARCHAR(500) NULL;

COMMIT TRANSACTION;
