SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'commercial.ServiceAvailability',N'U') IS NULL
BEGIN
    CREATE TABLE commercial.ServiceAvailability (
        service_id BIGINT NOT NULL REFERENCES commercial.Services(service_id),
        starts_at DATETIME2 NOT NULL,
        quantity INT NOT NULL CHECK (quantity >= 0),
        CONSTRAINT PK_ServiceAvailability PRIMARY KEY (service_id, starts_at)
    );
END;
IF COL_LENGTH(N'commercial.ServiceBookings', N'submit_key') IS NULL
    ALTER TABLE commercial.ServiceBookings ADD submit_key UNIQUEIDENTIFIER NULL, submit_hash CHAR(64) NULL,
        contact_name NVARCHAR(150) NULL, contact_phone NVARCHAR(20) NULL, contact_email NVARCHAR(256) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'commercial.ServiceBookings') AND name = N'UX_ServiceBookings_TravelerSubmitKey')
    EXEC(N'CREATE UNIQUE INDEX UX_ServiceBookings_TravelerSubmitKey ON commercial.ServiceBookings(traveler_user_id, submit_key) WHERE submit_key IS NOT NULL;');
COMMIT TRANSACTION;
