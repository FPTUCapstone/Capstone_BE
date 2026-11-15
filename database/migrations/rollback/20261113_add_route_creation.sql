-- Guarded rollback: never discard any new Route Note, creator, operation or lifecycle data.
-- Back up first. A database containing UC-54 creations requires roll-forward recovery.
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF COL_LENGTH(N'catalog.RouteSegments',N'status') IS NOT NULL
        EXEC(N'IF EXISTS (SELECT 1 FROM catalog.RouteSegments WITH (UPDLOCK,HOLDLOCK)
            WHERE status<>''Active'' OR route_note IS NOT NULL OR created_by IS NOT NULL OR idempotency_key IS NOT NULL)
            THROW 51000, ''Rollback refused: preserving UC-54 route data requires roll-forward recovery.'', 1;');
    DROP TRIGGER IF EXISTS catalog.TR_RouteSegments_ActiveEndpoints;
    DROP INDEX IF EXISTS UX_RouteSegments_ActorOperation ON catalog.RouteSegments;
    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name=N'FK_RouteSegments_CreatedBy' AND parent_object_id=OBJECT_ID(N'catalog.RouteSegments'))
        ALTER TABLE catalog.RouteSegments DROP CONSTRAINT FK_RouteSegments_CreatedBy;
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_RouteSegments_Operation' AND parent_object_id=OBJECT_ID(N'catalog.RouteSegments'))
        ALTER TABLE catalog.RouteSegments DROP CONSTRAINT CK_RouteSegments_Operation;
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_RouteSegments_Status' AND parent_object_id=OBJECT_ID(N'catalog.RouteSegments'))
        ALTER TABLE catalog.RouteSegments DROP CONSTRAINT CK_RouteSegments_Status;
    IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name=N'DF_RouteSegments_Status' AND parent_object_id=OBJECT_ID(N'catalog.RouteSegments'))
        ALTER TABLE catalog.RouteSegments DROP CONSTRAINT DF_RouteSegments_Status;
    IF COL_LENGTH(N'catalog.RouteSegments',N'idempotency_key') IS NOT NULL ALTER TABLE catalog.RouteSegments DROP COLUMN idempotency_key;
    IF COL_LENGTH(N'catalog.RouteSegments',N'created_by') IS NOT NULL ALTER TABLE catalog.RouteSegments DROP COLUMN created_by;
    IF COL_LENGTH(N'catalog.RouteSegments',N'route_note') IS NOT NULL ALTER TABLE catalog.RouteSegments DROP COLUMN route_note;
    IF COL_LENGTH(N'catalog.RouteSegments',N'status') IS NOT NULL ALTER TABLE catalog.RouteSegments DROP COLUMN status;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
