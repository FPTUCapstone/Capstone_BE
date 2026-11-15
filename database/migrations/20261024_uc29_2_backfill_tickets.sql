/*
    UC-29 View QR E-ticket — backfill tickets for bookings confirmed before UC-29.

    Decision D7 (2026-10-24): every booking that a verified payment confirmed before this release
    gets its one Valid ticket here. SQL cannot sign, so no commerce.TicketQrCodes row is written;
    the API issues the QR the first time the Traveler opens the ticket.

    Eligible: status Confirmed or Completed, payment_status Paid, linked to a tour schedule, and no
    ticket yet. ticket_code follows Ticket.CodeFor in the domain: 'TK-' + booking_id padded to ten
    digits. issued_at is the backfill time.

    Idempotent: a second run inserts nothing. Prints the number of tickets inserted.
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

    IF OBJECT_ID(N'commerce.Tickets', N'U') IS NULL OR OBJECT_ID(N'commerce.Bookings', N'U') IS NULL
        THROW 51000, 'UC-29 ticket backfill requires commerce.Bookings and commerce.Tickets.', 1;

    INSERT INTO commerce.Tickets (booking_id, participant_id, ticket_code, status, issued_at)
    SELECT
        b.booking_id,
        NULL,
        'TK-' + CASE WHEN b.booking_id < 10000000000
                     THEN RIGHT('0000000000' + CAST(b.booking_id AS VARCHAR(19)), 10)
                     ELSE CAST(b.booking_id AS VARCHAR(19)) END,
        'Valid',
        SYSUTCDATETIME()
    FROM commerce.Bookings AS b WITH (UPDLOCK, HOLDLOCK)
    WHERE b.status IN ('Confirmed', 'Completed')
      AND b.payment_status = 'Paid'
      AND b.tour_schedule_id IS NOT NULL
      AND NOT EXISTS (
          SELECT 1 FROM commerce.Tickets AS t WITH (UPDLOCK, HOLDLOCK)
          WHERE t.booking_id = b.booking_id);

    DECLARE @inserted INT = @@ROWCOUNT;

    COMMIT TRANSACTION;

    PRINT CONCAT(N'UC-29 ticket backfill inserted ', @inserted, N' ticket(s).');
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
