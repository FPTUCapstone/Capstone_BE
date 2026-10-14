/*
    UC-74 (Reject Payout Request): message catalogue entry for an invalid rejection reason length.
    MSG131-MSG156 are referenced by SRS functional text, so the next free code is MSG157.
    Idempotent insert; no existing rows are changed.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Messages', N'U') IS NULL
        THROW 51000, 'MSG157 migration requires dbo.Messages.', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.Messages WHERE message_code = 'MSG157')
        INSERT INTO dbo.Messages (message_code, message_type, content_template)
        VALUES ('MSG157', 'RedUnderTextbox', N'Rejection reason must be between {min_length} and {max_length} characters.');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
