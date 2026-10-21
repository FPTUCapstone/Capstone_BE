/* UC-28: retain the PayOS VietQR payload so Mobile can render native checkout. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF COL_LENGTH(N'payment.PaymentTransactions', N'qr_code') IS NULL
    ALTER TABLE payment.PaymentTransactions ADD qr_code NVARCHAR(MAX) NULL;
COMMIT TRANSACTION;
