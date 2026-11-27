-- Preserve original TO webhook verification credentials for in-flight payments.
-- Apply after 20261125_direct_operator_payment_refund_foundation.sql.
-- Existing records are intentionally NOT backfilled from mutable current channels.
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'payment.PaymentTransactions', N'U') IS NULL
        OR OBJECT_ID(N'payment.OperatorPaymentChannels', N'U') IS NULL
        THROW 51005, N'Operator webhook snapshots require payment foundation tables.', 1;
    IF COL_LENGTH(N'payment.PaymentTransactions', N'operator_channel_id') IS NULL
        ALTER TABLE payment.PaymentTransactions ADD operator_channel_id BIGINT NULL;
    IF COL_LENGTH(N'payment.PaymentTransactions', N'operator_channel_version') IS NULL
        ALTER TABLE payment.PaymentTransactions ADD operator_channel_version INT NULL;
    IF COL_LENGTH(N'payment.PaymentTransactions', N'operator_checksum_key_ref') IS NULL
        ALTER TABLE payment.PaymentTransactions ADD operator_checksum_key_ref NVARCHAR(500) NULL;
    -- Reservations survive credential changes and prevent cross-TO webhook redirection.
    IF OBJECT_ID(N'payment.OperatorPaymentMerchantClaims', N'U') IS NULL
        CREATE TABLE payment.OperatorPaymentMerchantClaims
        (
            merchant_fingerprint VARCHAR(64) NOT NULL CONSTRAINT PK_OperatorPaymentMerchantClaims PRIMARY KEY,
            channel_id BIGINT NOT NULL,
            created_at DATETIME2 NOT NULL,
            CONSTRAINT FK_OperatorPaymentMerchantClaims_Channel FOREIGN KEY (channel_id)
                REFERENCES payment.OperatorPaymentChannels(channel_id)
        );
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PaymentTransactions_OperatorChannel')
        EXEC(N'ALTER TABLE payment.PaymentTransactions ADD CONSTRAINT FK_PaymentTransactions_OperatorChannel
            FOREIGN KEY (operator_channel_id) REFERENCES payment.OperatorPaymentChannels(channel_id);');
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PaymentTransactions_OperatorChannelSnapshot')
        EXEC(N'ALTER TABLE payment.PaymentTransactions ADD CONSTRAINT CK_PaymentTransactions_OperatorChannelSnapshot CHECK
            ((operator_channel_id IS NULL AND operator_channel_version IS NULL AND operator_checksum_key_ref IS NULL)
            OR (operator_channel_id IS NOT NULL AND operator_channel_id > 0 AND operator_channel_version IS NOT NULL
            AND operator_channel_version > 0 AND operator_checksum_key_ref IS NOT NULL AND LEN(operator_checksum_key_ref) > 0));');
    COMMIT;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    THROW;
END CATCH;
