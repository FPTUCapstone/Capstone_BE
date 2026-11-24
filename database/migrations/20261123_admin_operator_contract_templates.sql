/* Administrator contract templates. No account/approval status changes. */
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
    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51190, 'Contract templates require dbo.Users.', 1;
    IF SCHEMA_ID(N'admin') IS NULL EXEC(N'CREATE SCHEMA admin AUTHORIZATION dbo');

    IF OBJECT_ID(N'admin.OperatorContractTemplates', N'U') IS NULL
    BEGIN
        CREATE TABLE admin.OperatorContractTemplates (
            template_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OperatorContractTemplates PRIMARY KEY,
            template_type VARCHAR(40) NOT NULL,
            language VARCHAR(10) NOT NULL,
            title NVARCHAR(200) NOT NULL,
            version INT NULL,
            draft_revision INT NOT NULL,
            status VARCHAR(12) NOT NULL,
            source_storage_key NVARCHAR(500) NOT NULL,
            source_sha256 VARCHAR(64) NOT NULL,
            source_file_name NVARCHAR(255) NOT NULL,
            variables_manifest_json NVARCHAR(MAX) NOT NULL,
            validation_valid BIT NOT NULL,
            render_state VARCHAR(16) NOT NULL,
            preview_storage_key NVARCHAR(500) NULL,
            preview_sha256 VARCHAR(64) NULL,
            preview_revision INT NULL,
            preview_acknowledged_revision INT NULL,
            render_error_code VARCHAR(100) NULL,
            created_by_user_id BIGINT NOT NULL,
            updated_by_user_id BIGINT NOT NULL,
            published_by_user_id BIGINT NULL,
            created_at DATETIME2 NOT NULL,
            updated_at DATETIME2 NOT NULL,
            published_at DATETIME2 NULL,
            archived_at DATETIME2 NULL,
            publication_idempotency_key UNIQUEIDENTIFIER NULL,
            publication_expected_generation BIGINT NULL,
            publication_expected_current_id BIGINT NULL,
            row_version ROWVERSION NOT NULL,
            CONSTRAINT FK_OperatorContractTemplates_CreatedBy FOREIGN KEY(created_by_user_id) REFERENCES dbo.Users(user_id),
            CONSTRAINT FK_OperatorContractTemplates_UpdatedBy FOREIGN KEY(updated_by_user_id) REFERENCES dbo.Users(user_id),
            CONSTRAINT FK_OperatorContractTemplates_PublishedBy FOREIGN KEY(published_by_user_id) REFERENCES dbo.Users(user_id),
            CONSTRAINT CK_OperatorContractTemplates_Type CHECK(template_type='OperatorPartnership' AND language='vi'),
            CONSTRAINT CK_OperatorContractTemplates_Revision CHECK(draft_revision > 0 AND (version IS NULL OR version > 0)),
            CONSTRAINT CK_OperatorContractTemplates_Status CHECK(status IN ('Draft','Published','Archived')),
            CONSTRAINT CK_OperatorContractTemplates_RenderState CHECK(render_state IN ('NotRequested','Processing','Ready','Failed')),
            CONSTRAINT CK_OperatorContractTemplates_Manifest CHECK(ISJSON(variables_manifest_json)=1),
            CONSTRAINT CK_OperatorContractTemplates_Preview CHECK(
                render_state <> 'Ready' OR (preview_storage_key IS NOT NULL AND preview_sha256 IS NOT NULL
                    AND preview_revision IS NOT NULL AND preview_revision=draft_revision)),
            CONSTRAINT CK_OperatorContractTemplates_Published CHECK(
                (status='Draft' AND version IS NULL AND published_at IS NULL AND published_by_user_id IS NULL)
                OR (status IN ('Published','Archived') AND version IS NOT NULL AND published_at IS NOT NULL
                    AND published_by_user_id IS NOT NULL AND validation_valid=1 AND render_state='Ready'
                    AND preview_storage_key IS NOT NULL AND preview_sha256 IS NOT NULL
                    AND preview_revision IS NOT NULL AND preview_acknowledged_revision IS NOT NULL
                    AND preview_revision=draft_revision AND preview_acknowledged_revision=draft_revision)),
            CONSTRAINT CK_OperatorContractTemplates_Archived CHECK(
                (status='Archived' AND archived_at IS NOT NULL) OR (status<>'Archived' AND archived_at IS NULL))
        );
    END;

    IF OBJECT_ID(N'admin.ContractTemplatePublicationHeads', N'U') IS NULL
    BEGIN
        CREATE TABLE admin.ContractTemplatePublicationHeads (
            head_id BIGINT NOT NULL CONSTRAINT PK_ContractTemplatePublicationHeads PRIMARY KEY,
            template_type VARCHAR(40) NOT NULL,
            language VARCHAR(10) NOT NULL,
            current_published_template_id BIGINT NULL,
            current_version INT NOT NULL,
            generation BIGINT NOT NULL,
            row_version ROWVERSION NOT NULL,
            CONSTRAINT FK_ContractTemplatePublicationHeads_Template FOREIGN KEY(current_published_template_id)
                REFERENCES admin.OperatorContractTemplates(template_id),
            CONSTRAINT CK_ContractTemplatePublicationHeads_Type CHECK(head_id=1 AND template_type='OperatorPartnership' AND language='vi'),
            CONSTRAINT CK_ContractTemplatePublicationHeads_Generation CHECK(
                (generation=0 AND current_version=0 AND current_published_template_id IS NULL)
                OR (generation>0 AND current_version>0 AND current_published_template_id IS NOT NULL))
        );
    END;

    IF OBJECT_ID(N'admin.ContractTemplateRenderJobs', N'U') IS NULL
    BEGIN
        CREATE TABLE admin.ContractTemplateRenderJobs (
            job_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ContractTemplateRenderJobs PRIMARY KEY,
            template_id BIGINT NOT NULL,
            revision INT NOT NULL,
            source_sha256 VARCHAR(64) NOT NULL,
            status VARCHAR(12) NOT NULL,
            requested_at DATETIME2 NOT NULL,
            completed_at DATETIME2 NULL,
            claim_token UNIQUEIDENTIFIER NULL,
            lease_expires_at DATETIME2 NULL,
            attempt_count INT NOT NULL,
            failure_code VARCHAR(100) NULL,
            row_version ROWVERSION NOT NULL,
            CONSTRAINT FK_ContractTemplateRenderJobs_Template FOREIGN KEY(template_id) REFERENCES admin.OperatorContractTemplates(template_id),
            CONSTRAINT CK_ContractTemplateRenderJobs_Status CHECK(status IN ('Queued','Processing','Completed','Failed')),
            CONSTRAINT CK_ContractTemplateRenderJobs_Revision CHECK(revision>0 AND attempt_count>=0),
            CONSTRAINT CK_ContractTemplateRenderJobs_Lease CHECK(
                (status='Processing' AND claim_token IS NOT NULL AND lease_expires_at IS NOT NULL)
                OR (status<>'Processing' AND claim_token IS NULL AND lease_expires_at IS NULL))
        );
    END;

    DECLARE @expected TABLE(table_name SYSNAME, column_name SYSNAME, type_name SYSNAME, max_length SMALLINT, nullable BIT, identity_column BIT);
    INSERT @expected VALUES
        (N'OperatorContractTemplates',N'template_id',N'bigint',8,0,1),
        (N'OperatorContractTemplates',N'template_type',N'varchar',40,0,0),
        (N'OperatorContractTemplates',N'language',N'varchar',10,0,0),
        (N'OperatorContractTemplates',N'title',N'nvarchar',400,0,0),
        (N'OperatorContractTemplates',N'version',N'int',4,1,0),
        (N'OperatorContractTemplates',N'draft_revision',N'int',4,0,0),
        (N'OperatorContractTemplates',N'status',N'varchar',12,0,0),
        (N'OperatorContractTemplates',N'source_storage_key',N'nvarchar',1000,0,0),
        (N'OperatorContractTemplates',N'source_sha256',N'varchar',64,0,0),
        (N'OperatorContractTemplates',N'source_file_name',N'nvarchar',510,0,0),
        (N'OperatorContractTemplates',N'variables_manifest_json',N'nvarchar',-1,0,0),
        (N'OperatorContractTemplates',N'validation_valid',N'bit',1,0,0),
        (N'OperatorContractTemplates',N'render_state',N'varchar',16,0,0),
        (N'OperatorContractTemplates',N'preview_storage_key',N'nvarchar',1000,1,0),
        (N'OperatorContractTemplates',N'preview_sha256',N'varchar',64,1,0),
        (N'OperatorContractTemplates',N'preview_revision',N'int',4,1,0),
        (N'OperatorContractTemplates',N'preview_acknowledged_revision',N'int',4,1,0),
        (N'OperatorContractTemplates',N'render_error_code',N'varchar',100,1,0),
        (N'OperatorContractTemplates',N'created_by_user_id',N'bigint',8,0,0),
        (N'OperatorContractTemplates',N'updated_by_user_id',N'bigint',8,0,0),
        (N'OperatorContractTemplates',N'published_by_user_id',N'bigint',8,1,0),
        (N'OperatorContractTemplates',N'created_at',N'datetime2',8,0,0),
        (N'OperatorContractTemplates',N'updated_at',N'datetime2',8,0,0),
        (N'OperatorContractTemplates',N'published_at',N'datetime2',8,1,0),
        (N'OperatorContractTemplates',N'archived_at',N'datetime2',8,1,0),
        (N'OperatorContractTemplates',N'publication_idempotency_key',N'uniqueidentifier',16,1,0),
        (N'OperatorContractTemplates',N'publication_expected_generation',N'bigint',8,1,0),
        (N'OperatorContractTemplates',N'publication_expected_current_id',N'bigint',8,1,0),
        (N'OperatorContractTemplates',N'row_version',N'timestamp',8,0,0),
        (N'ContractTemplatePublicationHeads',N'head_id',N'bigint',8,0,0),
        (N'ContractTemplatePublicationHeads',N'template_type',N'varchar',40,0,0),
        (N'ContractTemplatePublicationHeads',N'language',N'varchar',10,0,0),
        (N'ContractTemplatePublicationHeads',N'current_published_template_id',N'bigint',8,1,0),
        (N'ContractTemplatePublicationHeads',N'current_version',N'int',4,0,0),
        (N'ContractTemplatePublicationHeads',N'generation',N'bigint',8,0,0),
        (N'ContractTemplatePublicationHeads',N'row_version',N'timestamp',8,0,0),
        (N'ContractTemplateRenderJobs',N'job_id',N'bigint',8,0,1),
        (N'ContractTemplateRenderJobs',N'template_id',N'bigint',8,0,0),
        (N'ContractTemplateRenderJobs',N'revision',N'int',4,0,0),
        (N'ContractTemplateRenderJobs',N'source_sha256',N'varchar',64,0,0),
        (N'ContractTemplateRenderJobs',N'status',N'varchar',12,0,0),
        (N'ContractTemplateRenderJobs',N'requested_at',N'datetime2',8,0,0),
        (N'ContractTemplateRenderJobs',N'completed_at',N'datetime2',8,1,0),
        (N'ContractTemplateRenderJobs',N'claim_token',N'uniqueidentifier',16,1,0),
        (N'ContractTemplateRenderJobs',N'lease_expires_at',N'datetime2',8,1,0),
        (N'ContractTemplateRenderJobs',N'attempt_count',N'int',4,0,0),
        (N'ContractTemplateRenderJobs',N'failure_code',N'varchar',100,1,0),
        (N'ContractTemplateRenderJobs',N'row_version',N'timestamp',8,0,0);
    IF EXISTS (SELECT 1 FROM @expected e WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns c
        WHERE c.object_id=OBJECT_ID(N'admin.'+e.table_name) AND c.name=e.column_name
            AND c.system_type_id=TYPE_ID(e.type_name) AND c.max_length=e.max_length
            AND c.is_nullable=e.nullable AND c.is_identity=e.identity_column AND c.is_computed=0
            AND (e.type_name<>N'datetime2' OR c.scale=7)))
        THROW 51190, 'Existing contract-template columns have an incompatible shape.', 1;
    IF COL_LENGTH(N'admin.OperatorContractTemplates',N'publication_idempotency_key') IS NULL
        OR COL_LENGTH(N'admin.OperatorContractTemplates',N'preview_acknowledged_revision') IS NULL
        OR COL_LENGTH(N'admin.ContractTemplatePublicationHeads',N'generation') IS NULL
        OR COL_LENGTH(N'admin.ContractTemplateRenderJobs',N'claim_token') IS NULL
        THROW 51190, 'Existing contract-template tables are incomplete; no destructive repair was attempted.', 1;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'admin.OperatorContractTemplates') AND name=N'UX_OperatorContractTemplates_Published')
        CREATE UNIQUE INDEX UX_OperatorContractTemplates_Published ON admin.OperatorContractTemplates(template_type,language) WHERE status='Published';
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'admin.OperatorContractTemplates') AND name=N'UX_OperatorContractTemplates_Version')
        CREATE UNIQUE INDEX UX_OperatorContractTemplates_Version ON admin.OperatorContractTemplates(template_type,language,version) WHERE version IS NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'admin.OperatorContractTemplates') AND name=N'UX_OperatorContractTemplates_PublicationKey')
        CREATE UNIQUE INDEX UX_OperatorContractTemplates_PublicationKey ON admin.OperatorContractTemplates(publication_idempotency_key) WHERE publication_idempotency_key IS NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'admin.ContractTemplatePublicationHeads') AND name=N'UX_ContractTemplatePublicationHeads_TypeLanguage')
        CREATE UNIQUE INDEX UX_ContractTemplatePublicationHeads_TypeLanguage ON admin.ContractTemplatePublicationHeads(template_type,language);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'admin.ContractTemplateRenderJobs') AND name=N'UX_ContractTemplateRenderJobs_Revision')
        CREATE UNIQUE INDEX UX_ContractTemplateRenderJobs_Revision ON admin.ContractTemplateRenderJobs(template_id,revision);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'admin.ContractTemplateRenderJobs') AND name=N'IX_ContractTemplateRenderJobs_Claim')
        CREATE INDEX IX_ContractTemplateRenderJobs_Claim ON admin.ContractTemplateRenderJobs(status,lease_expires_at);

    IF NOT EXISTS (SELECT 1 FROM admin.ContractTemplatePublicationHeads WHERE head_id=1)
        INSERT admin.ContractTemplatePublicationHeads(head_id,template_type,language,current_version,generation)
        VALUES(1,'OperatorPartnership','vi',0,0);
    COMMIT;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK;
    THROW;
END CATCH;
GO
