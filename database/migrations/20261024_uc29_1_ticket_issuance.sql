/*
    UC-29 View QR E-ticket — one ticket per booking.

    Decision D2 (2026-10-24): a confirmed booking holds exactly one e-ticket; participant_id stays
    NULL. UX_Tickets_OneTicketPerBooking is the database backstop for that rule and for repeated
    payment notifications (UC-28 10.a2). commerce.Tickets and commerce.TicketQrCodes already exist in
    tripmate_schema_v7.sql; no column changes.

    Refuses, without changing anything, when a booking already holds several tickets (the offending
    booking ids are named) or when an index with this name has another shape.

    Non-destructive and idempotent. 20261024_uc29_2_backfill_tickets.sql runs after this file.
*/
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commerce.Tickets', N'U') IS NULL
        THROW 51000, 'UC-29 ticket issuance requires commerce.Tickets (tripmate_schema_v7.sql).', 1;

    IF EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'commerce.Tickets') AND name = N'UX_Tickets_OneTicketPerBooking')
    BEGIN
        IF NOT EXISTS (
            SELECT 1
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'commerce.Tickets')
              AND i.name = N'UX_Tickets_OneTicketPerBooking'
              AND i.is_unique = 1
              AND i.has_filter = 0
              AND c.name = N'booking_id'
              AND (SELECT COUNT(*) FROM sys.index_columns all_columns
                   WHERE all_columns.object_id = i.object_id AND all_columns.index_id = i.index_id) = 1)
            THROW 51000, 'UX_Tickets_OneTicketPerBooking exists with an incompatible shape.', 1;
    END
    ELSE
    BEGIN
        DECLARE @duplicateBookings NVARCHAR(1000) = (
            SELECT STRING_AGG(CAST(booking_id AS NVARCHAR(20)), N', ')
            FROM (
                SELECT TOP (20) booking_id
                FROM commerce.Tickets WITH (UPDLOCK, HOLDLOCK)
                GROUP BY booking_id
                HAVING COUNT(*) > 1
                ORDER BY booking_id
            ) AS duplicates);

        IF @duplicateBookings IS NOT NULL
        BEGIN
            DECLARE @message NVARCHAR(2048) =
                N'UC-29 requires one ticket per booking; resolve these bookings first: ' + @duplicateBookings;
            THROW 51000, @message, 1;
        END;

        CREATE UNIQUE INDEX UX_Tickets_OneTicketPerBooking ON commerce.Tickets(booking_id);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
