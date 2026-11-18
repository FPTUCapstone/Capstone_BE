-- TM-226: Monthly Service Commission Statement and Receipt (UC-81-83)
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commercial.ServiceProviders', N'U') IS NULL
        OR OBJECT_ID(N'commercial.ServiceBookings', N'U') IS NULL
        OR OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51005, 'TM-226 requires ServiceProviders, ServiceBookings, and Users.', 1;

    IF OBJECT_ID(N'commercial.ServiceCommissionStatements', N'U') IS NULL
    BEGIN
        CREATE TABLE commercial.ServiceCommissionStatements (
            statement_id BIGINT IDENTITY(1,1) PRIMARY KEY,
            statement_code NVARCHAR(32) NOT NULL,
            provider_id BIGINT NOT NULL,
            period VARCHAR(7) NOT NULL,
            total_requests INT NOT NULL,
            total_actual_amount DECIMAL(14,2) NOT NULL,
            commission_rate DECIMAL(5,2) NOT NULL DEFAULT 10.00,
            commission_amount DECIMAL(14,2) NOT NULL,
            issue_date DATE NOT NULL,
            due_date DATE NOT NULL,
            status VARCHAR(20) NOT NULL DEFAULT 'Issued',
            bank_transfer_reference NVARCHAR(100) NULL,
            reported_at DATETIME2 NULL,
            confirmed_by_user_id BIGINT NULL,
            confirmed_at DATETIME2 NULL,
            receipt_number NVARCHAR(50) NULL,
            created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
            row_version ROWVERSION,
            CONSTRAINT FK_ServiceCommissionStatements_Provider FOREIGN KEY (provider_id)
                REFERENCES commercial.ServiceProviders(provider_id),
            CONSTRAINT FK_ServiceCommissionStatements_Staff FOREIGN KEY (confirmed_by_user_id)
                REFERENCES dbo.Users(user_id),
            CONSTRAINT UQ_ServiceCommissionStatements_StatementCode UNIQUE (statement_code),
            CONSTRAINT UQ_ServiceCommissionStatements_Provider_Period UNIQUE (provider_id, period),
            CONSTRAINT CK_ServiceCommissionStatements_TotalRequests CHECK (total_requests > 0),
            CONSTRAINT CK_ServiceCommissionStatements_TotalActualAmount CHECK (total_actual_amount >= 0),
            CONSTRAINT CK_ServiceCommissionStatements_CommissionAmount CHECK (commission_amount >= 0),
            CONSTRAINT CK_ServiceCommissionStatements_Status CHECK (status IN ('Issued', 'PaymentReported', 'Paid', 'Overdue'))
        );

        CREATE INDEX IX_ServiceCommissionStatements_Provider ON commercial.ServiceCommissionStatements(provider_id);
        CREATE INDEX IX_ServiceCommissionStatements_Period ON commercial.ServiceCommissionStatements(period);
        CREATE INDEX IX_ServiceCommissionStatements_Status ON commercial.ServiceCommissionStatements(status);
    END;

    IF COL_LENGTH(N'commercial.ServiceBookings', N'commission_statement_id') IS NULL
    BEGIN
        ALTER TABLE commercial.ServiceBookings
            ADD commission_statement_id BIGINT NULL
            CONSTRAINT FK_ServiceBookings_CommissionStatement
            REFERENCES commercial.ServiceCommissionStatements(statement_id);

        CREATE INDEX IX_ServiceBookings_CommissionStatementId
            ON commercial.ServiceBookings(commission_statement_id);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
