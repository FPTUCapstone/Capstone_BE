/*
REVIEW2 (D2, D6): link a POI to its Tripadvisor (Terra) location.

The backend reads the rating and recent reviews of linked POIs from the Terra API.
Only the location id is stored; ratings and review text are fetched at request time.
A POI has at most one Tripadvisor location and a location belongs to at most one POI.

Rerunnable: the column and the filtered unique index are created only when missing.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'catalog.POIs', N'U') IS NULL
    THROW 51000, 'REVIEW2 Tripadvisor link requires catalog.POIs.', 1;

IF COL_LENGTH(N'catalog.POIs', N'tripadvisor_location_id') IS NULL
    ALTER TABLE catalog.POIs ADD tripadvisor_location_id BIGINT NULL
        CONSTRAINT CK_POIs_TripadvisorLocationId CHECK (tripadvisor_location_id > 0);

COMMIT TRANSACTION;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'catalog.POIs') AND name = N'UX_POIs_TripadvisorLocationId')
    CREATE UNIQUE INDEX UX_POIs_TripadvisorLocationId
        ON catalog.POIs(tripadvisor_location_id)
        WHERE tripadvisor_location_id IS NOT NULL;
GO
