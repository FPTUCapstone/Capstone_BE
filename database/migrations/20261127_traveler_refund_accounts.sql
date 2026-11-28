SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.Users', N'U') IS NULL THROW 51010, N'Traveler refund accounts require dbo.Users.', 1;
IF SCHEMA_ID(N'payment') IS NULL EXEC(N'CREATE SCHEMA payment');
IF OBJECT_ID(N'payment.TravelerRefundAccounts', N'U') IS NULL
BEGIN
    CREATE TABLE payment.TravelerRefundAccounts
    (
        user_id BIGINT NOT NULL CONSTRAINT PK_TravelerRefundAccounts PRIMARY KEY,
        bank_code NVARCHAR(20) NOT NULL,
        account_number_protected NVARCHAR(2048) NOT NULL,
        account_holder_protected NVARCHAR(2048) NOT NULL,
        updated_at DATETIME2 NOT NULL,
        CONSTRAINT FK_TravelerRefundAccounts_Users FOREIGN KEY(user_id) REFERENCES dbo.Users(user_id)
    );
END;
COMMIT TRANSACTION;
