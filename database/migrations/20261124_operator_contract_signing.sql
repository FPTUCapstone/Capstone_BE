/* Drawn signature and email OTP onboarding. Additive; existing operator statuses stay unchanged. */
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL OR OBJECT_ID(N'dbo.OperatorProfiles', N'U') IS NULL
        OR OBJECT_ID(N'admin.OperatorContractTemplates', N'U') IS NULL
        THROW 51220, 'Operator signing requires users, profiles and contract template foundation.', 1;
    IF COL_LENGTH(N'dbo.Users', N'status_before_lock') IS NULL
        THROW 51220, 'Operator signing requires the existing lock recovery foundation.', 1;

    IF OBJECT_ID(N'admin.OperatorContracts', N'U') IS NULL
    BEGIN
        CREATE TABLE admin.OperatorContracts (
            contract_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OperatorContracts PRIMARY KEY,
            operator_user_id BIGINT NOT NULL,
            template_id BIGINT NOT NULL,
            template_version INT NOT NULL,
            template_source_storage_key NVARCHAR(500) NOT NULL,
            template_source_sha256 VARCHAR(64) NOT NULL,
            title NVARCHAR(200) NOT NULL,
            company_name NVARCHAR(200) NOT NULL,
            tax_code NVARCHAR(50) NOT NULL,
            business_license_no NVARCHAR(100) NOT NULL,
            business_address NVARCHAR(300) NOT NULL,
            operator_email NVARCHAR(256) NOT NULL,
            signer_name NVARCHAR(150) NOT NULL,
            signer_title NVARCHAR(150) NOT NULL,
            signer_capacity NVARCHAR(500) NOT NULL,
            status VARCHAR(20) NOT NULL,
            document_state VARCHAR(24) NOT NULL,
            revision INT NOT NULL,
            snapshot_sha256 VARCHAR(64) NULL,
            unsigned_document_storage_key NVARCHAR(500) NULL,
            unsigned_document_sha256 VARCHAR(64) NULL,
            signature_anchors_json NVARCHAR(MAX) NULL,
            signed_document_storage_key NVARCHAR(500) NULL,
            signed_document_sha256 VARCHAR(64) NULL,
            signature_storage_key NVARCHAR(500) NULL,
            signature_sha256 VARCHAR(64) NULL,
            approved_by_user_id BIGINT NOT NULL,
            approved_at DATETIME2 NOT NULL,
            created_at DATETIME2 NOT NULL,
            updated_at DATETIME2 NOT NULL,
            signed_at DATETIME2 NULL,
            voided_at DATETIME2 NULL,
            preparation_claim_token UNIQUEIDENTIFIER NULL,
            preparation_lease_expires_at DATETIME2 NULL,
            preparation_attempts INT NOT NULL,
            preparation_error_code VARCHAR(100) NULL,
            current_signing_challenge_id UNIQUEIDENTIFIER NULL,
            last_otp_issued_at DATETIME2 NULL,
            signing_operation_id UNIQUEIDENTIFIER NULL,
            signing_request_sha256 VARCHAR(64) NULL,
            consent_version NVARCHAR(80) NULL,
            row_version ROWVERSION NOT NULL,
            CONSTRAINT AK_OperatorContracts_ContractOwner UNIQUE(contract_id,operator_user_id),
            CONSTRAINT FK_OperatorContracts_Operator FOREIGN KEY(operator_user_id) REFERENCES dbo.Users(user_id),
            CONSTRAINT FK_OperatorContracts_Approver FOREIGN KEY(approved_by_user_id) REFERENCES dbo.Users(user_id),
            CONSTRAINT FK_OperatorContracts_Template FOREIGN KEY(template_id) REFERENCES admin.OperatorContractTemplates(template_id),
            CONSTRAINT CK_OperatorContracts_Status CHECK(status IN ('PendingSignature','Signed','Voided')),
            CONSTRAINT CK_OperatorContracts_DocumentState CHECK(document_state IN ('NeedsSignerDetails','Processing','Ready','Failed')),
            CONSTRAINT CK_OperatorContracts_Counters CHECK(template_version > 0 AND revision > 0 AND preparation_attempts BETWEEN 0 AND 3),
            CONSTRAINT CK_OperatorContracts_Lease CHECK((preparation_claim_token IS NULL AND preparation_lease_expires_at IS NULL) OR
                (preparation_claim_token IS NOT NULL AND preparation_lease_expires_at IS NOT NULL AND document_state='Processing' AND status='PendingSignature')),
            CONSTRAINT CK_OperatorContracts_Ready CHECK(document_state <> 'Ready' OR
                (snapshot_sha256 IS NOT NULL AND LEN(snapshot_sha256)=64 AND unsigned_document_storage_key IS NOT NULL
                 AND unsigned_document_sha256 IS NOT NULL AND LEN(unsigned_document_sha256)=64 AND signature_anchors_json IS NOT NULL AND ISJSON(signature_anchors_json)=1)),
            CONSTRAINT CK_OperatorContracts_Signed CHECK(status <> 'Signed' OR
                (document_state='Ready' AND signed_at IS NOT NULL AND signed_document_storage_key IS NOT NULL
                 AND signed_document_sha256 IS NOT NULL AND LEN(signed_document_sha256)=64
                 AND signature_storage_key IS NOT NULL AND signature_sha256 IS NOT NULL AND LEN(signature_sha256)=64
                 AND signing_operation_id IS NOT NULL AND signing_request_sha256 IS NOT NULL AND LEN(signing_request_sha256)=64
                 AND current_signing_challenge_id IS NOT NULL AND consent_version IS NOT NULL AND LEN(consent_version)>0)),
            CONSTRAINT CK_OperatorContracts_Void CHECK((status='Voided' AND voided_at IS NOT NULL) OR (status<>'Voided' AND voided_at IS NULL))
        );
        CREATE UNIQUE INDEX UX_OperatorContracts_Current ON admin.OperatorContracts(operator_user_id) WHERE status <> 'Voided';
        CREATE UNIQUE INDEX UX_OperatorContracts_SigningOperation ON admin.OperatorContracts(operator_user_id,signing_operation_id) WHERE signing_operation_id IS NOT NULL;
    END;
    IF COL_LENGTH(N'admin.OperatorContracts', N'current_signing_challenge_id') IS NULL
        OR COL_LENGTH(N'admin.OperatorContracts', N'signer_capacity') IS NULL
        OR NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'admin.OperatorContracts') AND name='contract_id' AND system_type_id=127)
        THROW 51221, 'Existing operator contract schema is incompatible.', 1;

    IF OBJECT_ID(N'admin.OperatorContractSigningChallenges', N'U') IS NULL
    BEGIN
        CREATE TABLE admin.OperatorContractSigningChallenges (
            challenge_id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_OperatorContractSigningChallenges PRIMARY KEY,
            contract_id BIGINT NOT NULL,
            operator_user_id BIGINT NOT NULL,
            accepted_content_sha256 VARCHAR(64) NOT NULL,
            protected_otp NVARCHAR(4000) NOT NULL,
            created_at DATETIME2 NOT NULL,
            expires_at DATETIME2 NOT NULL,
            sent_at DATETIME2 NULL,
            used_at DATETIME2 NULL,
            failed_attempts INT NOT NULL,
            status VARCHAR(12) NOT NULL,
            row_version ROWVERSION NOT NULL,
            CONSTRAINT FK_OperatorContractSigningChallenges_Contract FOREIGN KEY(contract_id,operator_user_id) REFERENCES admin.OperatorContracts(contract_id,operator_user_id),
            CONSTRAINT FK_OperatorContractSigningChallenges_Operator FOREIGN KEY(operator_user_id) REFERENCES dbo.Users(user_id),
            CONSTRAINT CK_OperatorContractSigningChallenges_Status CHECK(status IN ('Sending','Sent','Failed','Used')),
            CONSTRAINT CK_OperatorContractSigningChallenges_Validity CHECK(expires_at>created_at AND failed_attempts BETWEEN 0 AND 5 AND LEN(accepted_content_sha256)=64),
            CONSTRAINT CK_OperatorContractSigningChallenges_Used CHECK(status<>'Used' OR (used_at IS NOT NULL AND sent_at IS NOT NULL))
        );
        CREATE INDEX IX_OperatorContractSigningChallenges_OperatorUserId_CreatedAtUtc ON admin.OperatorContractSigningChallenges(operator_user_id,created_at);
    END;
    IF ISNULL(COL_LENGTH(N'admin.OperatorContractSigningChallenges', N'protected_otp'),-1) <> 8000
        THROW 51221, 'Existing contract challenge schema is incompatible.', 1;

    -- A one-time cutover is tied to table creation, not every migration rerun.
    IF OBJECT_ID(N'admin.OperatorContractLegacyExemptions', N'U') IS NULL
    BEGIN
        CREATE TABLE admin.OperatorContractLegacyExemptions (
            operator_user_id BIGINT NOT NULL CONSTRAINT PK_OperatorContractLegacyExemptions PRIMARY KEY,
            granted_at DATETIME2 NOT NULL,
            reason NVARCHAR(300) NOT NULL,
            CONSTRAINT FK_OperatorContractLegacyExemptions_Operator FOREIGN KEY(operator_user_id) REFERENCES dbo.Users(user_id)
        );
        DECLARE @cutover DATETIME2=SYSUTCDATETIME();
        INSERT admin.OperatorContractLegacyExemptions(operator_user_id,granted_at,reason)
        SELECT u.user_id,@cutover,N'Existing active approved operator at contract signing cutover'
        FROM dbo.Users u JOIN dbo.OperatorProfiles p ON p.user_id=u.user_id
        WHERE u.role='TourOperator' AND p.approval_status='Approved'
          AND (u.status='Active' OR (u.status='Locked' AND u.status_before_lock='Active'));
        IF OBJECT_ID(N'dbo.AuditLogs', N'U') IS NOT NULL
            INSERT dbo.AuditLogs(actor_user_id,action_type,affected_entity,affected_entity_id,after_data,created_at)
            SELECT NULL,'OperatorContractLegacyExempt','OperatorProfile',operator_user_id,
                N'{"reason":"Existing active approved operator at contract signing cutover"}',@cutover
            FROM admin.OperatorContractLegacyExemptions;
    END;

    IF EXISTS (
        SELECT 1 FROM (VALUES
            (N'contract_id'),(N'operator_user_id'),(N'template_id'),(N'template_version'),(N'template_source_storage_key'),
            (N'template_source_sha256'),(N'title'),(N'company_name'),(N'tax_code'),(N'business_license_no'),(N'business_address'),
            (N'operator_email'),(N'signer_name'),(N'signer_title'),(N'signer_capacity'),(N'status'),(N'document_state'),(N'revision'),
            (N'snapshot_sha256'),(N'unsigned_document_storage_key'),(N'unsigned_document_sha256'),(N'signature_anchors_json'),
            (N'signed_document_storage_key'),(N'signed_document_sha256'),(N'signature_storage_key'),(N'signature_sha256'),
            (N'approved_by_user_id'),(N'approved_at'),(N'created_at'),(N'updated_at'),(N'signed_at'),(N'voided_at'),
            (N'preparation_claim_token'),(N'preparation_lease_expires_at'),(N'preparation_attempts'),(N'preparation_error_code'),
            (N'current_signing_challenge_id'),(N'last_otp_issued_at'),(N'signing_operation_id'),(N'signing_request_sha256'),
            (N'consent_version'),(N'row_version')) expected(column_name)
        LEFT JOIN sys.columns actual ON actual.object_id=OBJECT_ID(N'admin.OperatorContracts') AND actual.name=expected.column_name
        WHERE actual.column_id IS NULL)
        THROW 51221, 'Existing operator contract columns are incomplete.', 1;
    IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'admin.OperatorContracts') AND name='UX_OperatorContracts_Current' AND is_unique=1)
        OR NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'admin.OperatorContracts') AND name='UX_OperatorContracts_SigningOperation' AND is_unique=1)
        THROW 51221, 'Existing operator contract uniqueness guarantees are incompatible.', 1;

    IF EXISTS (
        SELECT 1 FROM (VALUES (N'challenge_id'),(N'contract_id'),(N'operator_user_id'),(N'accepted_content_sha256'),
            (N'protected_otp'),(N'created_at'),(N'expires_at'),(N'sent_at'),(N'used_at'),(N'failed_attempts'),(N'status'),(N'row_version')) expected(column_name)
        LEFT JOIN sys.columns actual ON actual.object_id=OBJECT_ID(N'admin.OperatorContractSigningChallenges') AND actual.name=expected.column_name
        WHERE actual.column_id IS NULL)
        OR ISNULL(COL_LENGTH(N'admin.OperatorContractLegacyExemptions',N'operator_user_id'),-1)<>8
        OR COL_LENGTH(N'admin.OperatorContractLegacyExemptions',N'granted_at') IS NULL
        OR ISNULL(COL_LENGTH(N'admin.OperatorContractLegacyExemptions',N'reason'),-1)<>600
        THROW 51221, 'Existing contract challenge or legacy exemption columns are incomplete.', 1;

    -- Discover the original unnamed inline status CHECK instead of assuming a generated name.
    DECLARE @dropStatus NVARCHAR(MAX)=N'';
    SELECT @dropStatus=@dropStatus+N'ALTER TABLE dbo.Users DROP CONSTRAINT '+QUOTENAME(name)+N';'
    FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.Users')
      AND (parent_column_id=COLUMNPROPERTY(OBJECT_ID(N'dbo.Users'),'status','ColumnId') OR name=N'CK_Users_Status');
    IF @dropStatus<>N'' EXEC sys.sp_executesql @dropStatus;
    ALTER TABLE dbo.Users WITH CHECK ADD CONSTRAINT CK_Users_Status CHECK(status IN
        ('PendingEmailVerification','Active','Locked','PendingApproval','Rejected','Inactive','PendingContractSignature'));
    IF OBJECT_ID(N'dbo.CK_Users_LockRecoveryState', N'C') IS NOT NULL ALTER TABLE dbo.Users DROP CONSTRAINT CK_Users_LockRecoveryState;
    ALTER TABLE dbo.Users WITH CHECK ADD CONSTRAINT CK_Users_LockRecoveryState CHECK (
        (status <> 'Locked' AND status_before_lock IS NULL AND locked_by_user_id IS NULL AND locked_at_utc IS NULL AND lock_reason IS NULL)
        OR (status='Locked' AND (
            (status_before_lock IS NULL AND locked_by_user_id IS NULL AND locked_at_utc IS NULL AND lock_reason IS NULL)
            OR (status_before_lock IS NULL AND locked_by_user_id IS NULL AND locked_at_utc IS NOT NULL AND lock_reason IS NOT NULL AND LEN(LTRIM(RTRIM(lock_reason)))>0)
            OR (status_before_lock IN ('PendingEmailVerification','Active','PendingApproval','Rejected','Inactive','PendingContractSignature')
                AND locked_at_utc IS NOT NULL AND lock_reason IS NOT NULL AND LEN(LTRIM(RTRIM(lock_reason)))>0))));
    IF OBJECT_ID(N'admin.UserUnlockOperations', N'U') IS NOT NULL
    BEGIN
        IF OBJECT_ID(N'admin.CK_UserUnlockOperations_RestoredStatus', N'C') IS NOT NULL
            ALTER TABLE admin.UserUnlockOperations DROP CONSTRAINT CK_UserUnlockOperations_RestoredStatus;
        ALTER TABLE admin.UserUnlockOperations WITH CHECK ADD CONSTRAINT CK_UserUnlockOperations_RestoredStatus CHECK(restored_status IN
            ('PendingEmailVerification','Active','PendingApproval','Rejected','Inactive','PendingContractSignature'));
    END;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
