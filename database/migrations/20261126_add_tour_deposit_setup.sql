/*
    Tour Package Deposit & Balance Settlement Configuration:
    Adds allow_deposit, deposit_percent, and balance_due_days_before_departure on commerce.Tours.
    Existing tours default to allow_deposit = 0 (100% upfront payment) and keep NULL deposit terms.
    Balance settlement deadline must be >= 4 days before departure to leave room before the 72-hour
    booking/payment cutoff (BR-154).
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

    IF OBJECT_ID(N'commerce.Tours', N'U') IS NULL
        THROW 51000, 'commerce.Tours must exist before adding deposit setup columns.', 1;

    IF COL_LENGTH(N'commerce.Tours', N'allow_deposit') IS NULL
        ALTER TABLE commerce.Tours ADD allow_deposit BIT NOT NULL
            CONSTRAINT DF_Tours_AllowDeposit DEFAULT 0;

    IF COL_LENGTH(N'commerce.Tours', N'deposit_percent') IS NULL
        ALTER TABLE commerce.Tours ADD deposit_percent DECIMAL(5,2) NULL;

    IF COL_LENGTH(N'commerce.Tours', N'balance_due_days_before_departure') IS NULL
        ALTER TABLE commerce.Tours ADD balance_due_days_before_departure INT NULL;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.Tours')
          AND name = N'CK_Tours_DepositTerms')
    BEGIN
        EXEC sys.sp_executesql N'
            ALTER TABLE commerce.Tours ADD CONSTRAINT CK_Tours_DepositTerms CHECK (
                (allow_deposit = 0 AND deposit_percent IS NULL AND balance_due_days_before_departure IS NULL)
                OR
                (allow_deposit = 1
                 AND deposit_percent >= 10 AND deposit_percent <= 90
                 AND balance_due_days_before_departure >= 4 AND balance_due_days_before_departure <= 180)
            );';
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
