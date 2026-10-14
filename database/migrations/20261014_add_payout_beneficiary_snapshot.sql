/*
    UC-64 (Review Payout Request): beneficiary bank snapshot on payment.Payouts.
    Non-destructive: adds nullable columns only, no data is transformed or deleted.
    Stores the bank name, account holder and the last 4 digits of the account number.
    The full account number is NOT stored here; encrypted storage is deferred to UC-65.
    Until UC-46 writes these columns, they stay NULL (test fixtures set them explicitly).
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'payment.Payouts', N'U') IS NULL
        THROW 51000, 'Beneficiary snapshot migration requires payment.Payouts.', 1;

    IF COL_LENGTH(N'payment.Payouts', N'beneficiary_bank_name') IS NULL
        ALTER TABLE payment.Payouts ADD beneficiary_bank_name NVARCHAR(100) NULL;
    IF COL_LENGTH(N'payment.Payouts', N'beneficiary_account_holder') IS NULL
        ALTER TABLE payment.Payouts ADD beneficiary_account_holder NVARCHAR(200) NULL;
    IF COL_LENGTH(N'payment.Payouts', N'beneficiary_account_last4') IS NULL
        ALTER TABLE payment.Payouts ADD beneficiary_account_last4 CHAR(4) NULL;

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'payment.Payouts')
          AND name = N'CK_Payouts_BeneficiarySnapshot')
        EXEC(N'ALTER TABLE payment.Payouts ADD CONSTRAINT CK_Payouts_BeneficiarySnapshot CHECK (
            (beneficiary_bank_name IS NULL AND beneficiary_account_holder IS NULL AND beneficiary_account_last4 IS NULL)
            OR (beneficiary_bank_name IS NOT NULL AND beneficiary_account_holder IS NOT NULL
                AND beneficiary_account_last4 IS NOT NULL
                AND beneficiary_account_last4 NOT LIKE ''%[^0-9]%''));');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
