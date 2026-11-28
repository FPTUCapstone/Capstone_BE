SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('commercial.ServiceCommissionStatements', 'verified_bank_transfer_reference') IS NULL
    ALTER TABLE commercial.ServiceCommissionStatements ADD verified_bank_transfer_reference NVARCHAR(100) NULL;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('commercial.ServiceCommissionStatements')
      AND name = 'UX_ServiceCommissionStatements_VerifiedBankReference'
)
    EXEC(N'CREATE UNIQUE INDEX UX_ServiceCommissionStatements_VerifiedBankReference
        ON commercial.ServiceCommissionStatements (verified_bank_transfer_reference)
        WHERE verified_bank_transfer_reference IS NOT NULL;');

COMMIT TRANSACTION;
