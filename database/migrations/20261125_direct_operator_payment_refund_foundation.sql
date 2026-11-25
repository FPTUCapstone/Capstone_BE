-- Direct Operator Payment, Refund and Commission — Foundation (Phase 1 Task 5)
-- Adds: payment_flow column to commerce.Bookings; payment.OperatorPaymentChannels;
--       payment.DirectRefundCases; payment.DirectRefundTransfers;
--       commercial.TourDirectCommissionStatements
-- Convention: idempotent (run twice safe); no credentials; legacy data intact.
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    -- Prerequisites
    IF OBJECT_ID(N'commerce.Bookings', N'U') IS NULL
        THROW 51005, N'Direct payment foundation requires commerce.Bookings.', 1;
    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51005, N'Direct payment foundation requires dbo.Users.', 1;
    IF SCHEMA_ID(N'payment') IS NULL
        THROW 51005, N'Direct payment foundation requires payment schema.', 1;
    IF SCHEMA_ID(N'commercial') IS NULL
        THROW 51005, N'Direct payment foundation requires commercial schema.', 1;

    -- =========================================================================
    -- 1. commerce.Bookings — add payment_flow column
    --    Existing rows get default 'PlatformCollectedLegacy' (legacy model).
    -- =========================================================================
    IF COL_LENGTH(N'commerce.Bookings', N'payment_flow') IS NULL
    BEGIN
        ALTER TABLE commerce.Bookings
            ADD payment_flow VARCHAR(30) NOT NULL
                CONSTRAINT CK_Bookings_PaymentFlow
                    CHECK (payment_flow IN (N'PlatformCollectedLegacy', N'OperatorDirect'))
                CONSTRAINT DF_Bookings_PaymentFlow DEFAULT N'PlatformCollectedLegacy';
    END;

    -- =========================================================================
    -- 2. payment.OperatorPaymentChannels
    --    One row per TO. Credentials are reference tokens to a secret store,
    --    never the raw keys. UNIQUE on (tour_operator_user_id) enforces one
    --    active channel per TO.
    -- =========================================================================
    IF OBJECT_ID(N'payment.OperatorPaymentChannels', N'U') IS NULL
    BEGIN
        CREATE TABLE payment.OperatorPaymentChannels (
            channel_id          BIGINT         IDENTITY(1,1) NOT NULL
                                               CONSTRAINT PK_OperatorPaymentChannels PRIMARY KEY,
            tour_operator_user_id BIGINT       NOT NULL,
            opaque_id           UNIQUEIDENTIFIER NOT NULL
                                               CONSTRAINT DF_OperatorPaymentChannels_OpaqueId DEFAULT NEWID(),
            status              VARCHAR(20)    NOT NULL
                                               CONSTRAINT CK_OperatorPaymentChannels_Status
                                                   CHECK (status IN (
                                                       N'NotConnected', N'PendingVerification',
                                                       N'Ready', N'ConnectionError', N'Disabled'))
                                               CONSTRAINT DF_OperatorPaymentChannels_Status DEFAULT N'NotConnected',
            channel_version     INT            NOT NULL
                                               CONSTRAINT DF_OperatorPaymentChannels_Version DEFAULT 1,
            -- Encrypted credential references (actual keys in secret store, never stored raw)
            client_id_ref       NVARCHAR(500)  NULL,
            api_key_ref         NVARCHAR(500)  NULL,
            checksum_key_ref    NVARCHAR(500)  NULL,
            -- Beneficiary snapshot (display / verification; not used for auth)
            beneficiary_account_number NVARCHAR(50)  NULL,
            beneficiary_account_name   NVARCHAR(200) NULL,
            beneficiary_bank_code      NVARCHAR(20)  NULL,
            -- Webhook registration
            webhook_registered_at   DATETIME2  NULL,
            webhook_url             NVARCHAR(500) NULL,
            -- Lifecycle timestamps
            connected_at        DATETIME2      NULL,
            last_verified_at    DATETIME2      NULL,
            created_at          DATETIME2      NOT NULL
                                               CONSTRAINT DF_OperatorPaymentChannels_CreatedAt DEFAULT SYSUTCDATETIME(),
            updated_at          DATETIME2      NOT NULL
                                               CONSTRAINT DF_OperatorPaymentChannels_UpdatedAt DEFAULT SYSUTCDATETIME(),
            row_version         ROWVERSION,
            CONSTRAINT FK_OperatorPaymentChannels_Operator
                FOREIGN KEY (tour_operator_user_id) REFERENCES dbo.Users(user_id),
            CONSTRAINT UQ_OperatorPaymentChannels_OpaqueId
                UNIQUE (opaque_id),
            CONSTRAINT UQ_OperatorPaymentChannels_Operator
                UNIQUE (tour_operator_user_id)
        );

        CREATE INDEX IX_OperatorPaymentChannels_Status
            ON payment.OperatorPaymentChannels(status);
    END;

    -- =========================================================================
    -- 3. payment.DirectRefundCases
    --    One row per refund obligation. Tracks status, amounts, recipient info,
    --    and the 72-hour transfer deadline. VND stored as BIGINT (no fractions).
    -- =========================================================================
    IF OBJECT_ID(N'payment.DirectRefundCases', N'U') IS NULL
    BEGIN
        CREATE TABLE payment.DirectRefundCases (
            case_id             BIGINT         IDENTITY(1,1) NOT NULL
                                               CONSTRAINT PK_DirectRefundCases PRIMARY KEY,
            operation_id        UNIQUEIDENTIFIER NOT NULL
                                               CONSTRAINT DF_DirectRefundCases_OperationId DEFAULT NEWID(),
            booking_id          BIGINT         NOT NULL,
            source_type         VARCHAR(30)    NOT NULL
                                               CONSTRAINT CK_DirectRefundCases_SourceType
                                                   CHECK (source_type IN (
                                                       N'PolicyCancellation', N'ToCancellation',
                                                       N'EmergencyEvent', N'PostTripComplaint')),
            source_ref_id       BIGINT         NULL,
            -- Amount (VND, integer; refund_amount agreed; paid_amount verified from webhook/API)
            refund_amount_vnd   BIGINT         NOT NULL
                                               CONSTRAINT CK_DirectRefundCases_RefundAmount CHECK (refund_amount_vnd > 0),
            paid_amount_vnd     BIGINT         NOT NULL
                                               CONSTRAINT CK_DirectRefundCases_PaidAmount CHECK (paid_amount_vnd >= 0),
            -- Recipient (confirmed by Traveler; captured before transfer deadline starts)
            recipient_bank_code          NVARCHAR(20)  NULL,
            recipient_account_number     NVARCHAR(50)  NULL,
            recipient_account_holder     NVARCHAR(200) NULL,
            recipient_confirmed_at       DATETIME2     NULL,
            -- Status / deadline
            status              VARCHAR(40)    NOT NULL
                                               CONSTRAINT CK_DirectRefundCases_Status
                                                   CHECK (status IN (
                                                       N'AwaitingRecipient', N'AwaitingTransfer',
                                                       N'AwaitingTravelerConfirmation',
                                                       N'Completed', N'Disputed'))
                                               CONSTRAINT DF_DirectRefundCases_Status DEFAULT N'AwaitingRecipient',
            transfer_deadline_at DATETIME2    NULL,
            is_overdue_flag     BIT            NOT NULL
                                               CONSTRAINT DF_DirectRefundCases_IsOverdue DEFAULT 0,
            -- Completion
            completed_at        DATETIME2      NULL,
            completion_method   VARCHAR(30)    NULL
                                               CONSTRAINT CK_DirectRefundCases_CompletionMethod
                                                   CHECK (completion_method IS NULL OR completion_method IN (
                                                       N'TravelerConfirmed',
                                                       N'AutoCompletedAfter72Hours',
                                                       N'AdminResolved')),
            completion_rule_version NVARCHAR(20) NULL,
            -- Timestamps
            created_at          DATETIME2      NOT NULL
                                               CONSTRAINT DF_DirectRefundCases_CreatedAt DEFAULT SYSUTCDATETIME(),
            updated_at          DATETIME2      NOT NULL
                                               CONSTRAINT DF_DirectRefundCases_UpdatedAt DEFAULT SYSUTCDATETIME(),
            row_version         ROWVERSION,
            CONSTRAINT FK_DirectRefundCases_Booking
                FOREIGN KEY (booking_id) REFERENCES commerce.Bookings(booking_id),
            CONSTRAINT UQ_DirectRefundCases_OperationId
                UNIQUE (operation_id)
        );

        -- One direct refund case per (booking + source + source_ref) when source_ref exists.
        -- Cases from EmergencyEvent or PolicyCancellation without a specific ref use booking_id alone.
        CREATE UNIQUE INDEX UQ_DirectRefundCases_Booking_Source_Ref
            ON payment.DirectRefundCases(booking_id, source_type, source_ref_id)
            WHERE source_ref_id IS NOT NULL;

        CREATE INDEX IX_DirectRefundCases_Status
            ON payment.DirectRefundCases(status, transfer_deadline_at);

        CREATE INDEX IX_DirectRefundCases_Booking
            ON payment.DirectRefundCases(booking_id);
    END;

    -- =========================================================================
    -- 4. payment.DirectRefundTransfers
    --    Immutable record of each transfer report from a TO.
    --    confirmation_deadline_at = reported_at + 72h (continuous, set by server).
    --    Recipient snapshot is immutable at time of report.
    -- =========================================================================
    IF OBJECT_ID(N'payment.DirectRefundTransfers', N'U') IS NULL
    BEGIN
        CREATE TABLE payment.DirectRefundTransfers (
            transfer_id         BIGINT         IDENTITY(1,1) NOT NULL
                                               CONSTRAINT PK_DirectRefundTransfers PRIMARY KEY,
            operation_id        UNIQUEIDENTIFIER NOT NULL,
            case_id             BIGINT         NOT NULL,
            -- Transfer detail (reported by TO; not authoritative proof)
            reported_amount_vnd BIGINT         NOT NULL
                                               CONSTRAINT CK_DirectRefundTransfers_Amount CHECK (reported_amount_vnd > 0),
            bank_reference      NVARCHAR(200)  NOT NULL,
            actual_transferred_at DATETIME2    NOT NULL,
            reported_at         DATETIME2      NOT NULL,
            -- 72h traveler confirmation window (continuous, server-set)
            confirmation_deadline_at DATETIME2 NOT NULL,
            -- Recipient snapshot (immutable; captured from DirectRefundCase at report time)
            snapshot_bank_code           NVARCHAR(20)  NOT NULL,
            snapshot_account_number      NVARCHAR(50)  NOT NULL,
            snapshot_account_holder      NVARCHAR(200) NOT NULL,
            -- Timestamps
            created_at          DATETIME2      NOT NULL
                                               CONSTRAINT DF_DirectRefundTransfers_CreatedAt DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_DirectRefundTransfers_Case
                FOREIGN KEY (case_id) REFERENCES payment.DirectRefundCases(case_id),
            CONSTRAINT UQ_DirectRefundTransfers_OperationId
                UNIQUE (operation_id)
        );

        CREATE INDEX IX_DirectRefundTransfers_Case
            ON payment.DirectRefundTransfers(case_id);

        CREATE INDEX IX_DirectRefundTransfers_ConfirmationDeadline
            ON payment.DirectRefundTransfers(confirmation_deadline_at);
    END;

    -- =========================================================================
    -- 5. commercial.TourDirectCommissionStatements
    --    Monthly commission invoice for tour operators under the OperatorDirect
    --    payment model. Separate from commercial.ServiceCommissionStatements
    --    (which covers non-tour service providers).
    --    TO pays via payOS TripMate channel (CommissionCollection purpose).
    -- =========================================================================
    IF OBJECT_ID(N'commercial.TourDirectCommissionStatements', N'U') IS NULL
    BEGIN
        CREATE TABLE commercial.TourDirectCommissionStatements (
            statement_id        BIGINT         IDENTITY(1,1) NOT NULL
                                               CONSTRAINT PK_TourDirectCommissionStatements PRIMARY KEY,
            statement_code      NVARCHAR(32)   NOT NULL,
            tour_operator_user_id BIGINT       NOT NULL,
            period              VARCHAR(7)     NOT NULL, -- YYYY-MM
            -- Financials (VND, BIGINT; rate snapshot at statement generation time)
            eligible_base_vnd   BIGINT         NOT NULL
                                               CONSTRAINT CK_TourDirectComm_Base CHECK (eligible_base_vnd >= 0),
            commission_rate     DECIMAL(5,4)   NOT NULL
                                               CONSTRAINT CK_TourDirectComm_Rate CHECK (commission_rate > 0 AND commission_rate <= 1),
            commission_amount_vnd BIGINT       NOT NULL
                                               CONSTRAINT CK_TourDirectComm_Amount CHECK (commission_amount_vnd >= 0),
            -- Payment tracking
            status              VARCHAR(20)    NOT NULL
                                               CONSTRAINT CK_TourDirectComm_Status
                                                   CHECK (status IN (N'Issued', N'PaymentReported', N'Paid', N'Overdue', N'Cancelled'))
                                               CONSTRAINT DF_TourDirectComm_Status DEFAULT N'Issued',
            payos_payment_link_id NVARCHAR(100) NULL,
            paid_amount_vnd     BIGINT         NOT NULL
                                               CONSTRAINT DF_TourDirectComm_PaidAmount DEFAULT 0,
            paid_at             DATETIME2      NULL,
            -- Dates
            issue_date          DATE           NOT NULL,
            due_date            DATE           NOT NULL,
            -- Timestamps
            created_at          DATETIME2      NOT NULL
                                               CONSTRAINT DF_TourDirectComm_CreatedAt DEFAULT SYSUTCDATETIME(),
            updated_at          DATETIME2      NOT NULL
                                               CONSTRAINT DF_TourDirectComm_UpdatedAt DEFAULT SYSUTCDATETIME(),
            row_version         ROWVERSION,
            CONSTRAINT FK_TourDirectCommissionStatements_Operator
                FOREIGN KEY (tour_operator_user_id) REFERENCES dbo.Users(user_id),
            CONSTRAINT UQ_TourDirectCommissionStatements_Code
                UNIQUE (statement_code),
            CONSTRAINT UQ_TourDirectCommissionStatements_Operator_Period
                UNIQUE (tour_operator_user_id, period)
        );

        CREATE INDEX IX_TourDirectCommissionStatements_Operator
            ON commercial.TourDirectCommissionStatements(tour_operator_user_id);
        CREATE INDEX IX_TourDirectCommissionStatements_Period
            ON commercial.TourDirectCommissionStatements(period);
        CREATE INDEX IX_TourDirectCommissionStatements_Status
            ON commercial.TourDirectCommissionStatements(status, due_date);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
