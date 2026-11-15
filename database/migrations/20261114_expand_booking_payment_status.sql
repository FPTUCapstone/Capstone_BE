/*
    Refund processing writes Booking.PaymentStatus = PartiallyRefunded after a partial traveler
    refund. The original varchar(16) column cannot store that 17-character enum value, causing
    the refund transaction to roll back after gateway acceptance.

    Safe to run repeatedly.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
GO

IF COL_LENGTH(N'commerce.Bookings', N'payment_status') IS NULL
    THROW 51000, N'commerce.Bookings.payment_status is required before this migration can run.', 1;
GO

DECLARE @currentLength int = (
    SELECT c.max_length
    FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(N'commerce.Bookings')
      AND c.name = N'payment_status');

IF @currentLength < 17
BEGIN
    ALTER TABLE commerce.Bookings
        ALTER COLUMN payment_status varchar(17) NOT NULL;
END;
GO
