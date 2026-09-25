SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE name = N'CK_ItineraryItems_KindPoi'
          AND parent_object_id = OBJECT_ID(N'planning.ItineraryItems'))
    BEGIN
        ALTER TABLE planning.ItineraryItems
            DROP CONSTRAINT CK_ItineraryItems_KindPoi;
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE name = N'CK_ItineraryItems_KindPoi'
          AND parent_object_id = OBJECT_ID(N'planning.ItineraryItems'))
    BEGIN
        ALTER TABLE planning.ItineraryItems
            ADD CONSTRAINT CK_ItineraryItems_KindPoi CHECK (
                (item_kind = 'Visit' AND poi_id IS NOT NULL)
                OR (item_kind = 'Rest' AND is_mandatory = 0));
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
