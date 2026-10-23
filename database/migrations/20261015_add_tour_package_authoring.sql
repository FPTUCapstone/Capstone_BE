/*
    UC-35: production authoring data for commercial tour packages.
    Additive/idempotent SQL Server migration. Package terms and policy snapshots
    are kept with the product so later template edits cannot rewrite an offer.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commerce.Tours', N'U') IS NULL
        THROW 51000, 'UC-35 requires commerce.Tours.', 1;
    IF OBJECT_ID(N'commerce.TourSchedules', N'U') IS NULL
        THROW 51000, 'UC-35 requires commerce.TourSchedules.', 1;
    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        THROW 51000, 'UC-35 requires dbo.Users.', 1;

    /* The public state is distinct from an approved-but-not-published draft. */
    DECLARE @tourStatusConstraint SYSNAME;
    DECLARE @dropTourStatusConstraintSql NVARCHAR(MAX);
    WHILE EXISTS (
        SELECT 1
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.Tours')
          AND parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'commerce.Tours'), N'status', 'ColumnId'))
    BEGIN
        SELECT TOP (1) @tourStatusConstraint = name
        FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.Tours')
          AND parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'commerce.Tours'), N'status', 'ColumnId');
        SET @dropTourStatusConstraintSql =
            N'ALTER TABLE commerce.Tours DROP CONSTRAINT ' + QUOTENAME(@tourStatusConstraint) + N';';
        EXEC sys.sp_executesql @dropTourStatusConstraintSql;
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.Tours')
          AND name = N'CK_Tours_Status')
        ALTER TABLE commerce.Tours ADD CONSTRAINT CK_Tours_Status CHECK
            (status IN ('Draft','Pending','Approved','Rejected','Published','Inactive'));

    IF COL_LENGTH(N'commerce.Tours', N'time_zone_id') IS NULL
        ALTER TABLE commerce.Tours ADD time_zone_id NVARCHAR(100) NULL;
    IF COL_LENGTH(N'commerce.Tours', N'highlights') IS NULL
        ALTER TABLE commerce.Tours ADD highlights NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'commerce.Tours', N'inclusions') IS NULL
        ALTER TABLE commerce.Tours ADD inclusions NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'commerce.Tours', N'exclusions') IS NULL
        ALTER TABLE commerce.Tours ADD exclusions NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'commerce.Tours', N'traveller_requirements') IS NULL
        ALTER TABLE commerce.Tours ADD traveller_requirements NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'commerce.Tours', N'cancellation_policy_template_id') IS NULL
        ALTER TABLE commerce.Tours ADD cancellation_policy_template_id BIGINT NULL;

    IF COL_LENGTH(N'commerce.TourSchedules', N'minimum_capacity') IS NULL
    BEGIN
        EXEC sys.sp_executesql N'ALTER TABLE commerce.TourSchedules ADD minimum_capacity INT NULL;';
        EXEC sys.sp_executesql N'UPDATE commerce.TourSchedules SET minimum_capacity = 1 WHERE minimum_capacity IS NULL;';
        EXEC sys.sp_executesql N'ALTER TABLE commerce.TourSchedules ALTER COLUMN minimum_capacity INT NOT NULL;';
        EXEC sys.sp_executesql N'ALTER TABLE commerce.TourSchedules ADD CONSTRAINT CK_TourSchedules_MinimumCapacity
            CHECK (minimum_capacity > 0 AND minimum_capacity <= total_capacity);';
    END;
    /* Existing writers (seeds, other use cases, raw SQL) insert schedules without this column.
       A default of 1 keeps them valid; it also repairs databases that applied the earlier
       version of this migration, which left the column NOT NULL with no default. */
    IF NOT EXISTS (
        SELECT 1 FROM sys.default_constraints
        WHERE parent_object_id = OBJECT_ID(N'commerce.TourSchedules')
          AND parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'commerce.TourSchedules'), N'minimum_capacity', 'ColumnId'))
        EXEC sys.sp_executesql N'ALTER TABLE commerce.TourSchedules
            ADD CONSTRAINT DF_TourSchedules_MinimumCapacity DEFAULT 1 FOR minimum_capacity;';

    IF COL_LENGTH(N'commerce.TourSchedules', N'registration_deadline_at') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commerce.TourSchedules ADD registration_deadline_at DATETIME2 NULL;';
    IF COL_LENGTH(N'commerce.TourSchedules', N'adult_price') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commerce.TourSchedules ADD adult_price DECIMAL(12,2) NULL;';
    IF COL_LENGTH(N'commerce.TourSchedules', N'child_price') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commerce.TourSchedules ADD child_price DECIMAL(12,2) NULL;';
    IF COL_LENGTH(N'commerce.TourSchedules', N'infant_price') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commerce.TourSchedules ADD infant_price DECIMAL(12,2) NULL;';

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE parent_object_id = OBJECT_ID(N'commerce.TourSchedules')
                     AND name = N'CK_TourSchedules_RegistrationDeadline')
        EXEC sys.sp_executesql N'ALTER TABLE commerce.TourSchedules ADD CONSTRAINT CK_TourSchedules_RegistrationDeadline
            CHECK (registration_deadline_at IS NULL OR registration_deadline_at < start_datetime);';
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE parent_object_id = OBJECT_ID(N'commerce.TourSchedules')
                     AND name = N'CK_TourSchedules_SellingPrices')
        EXEC sys.sp_executesql N'ALTER TABLE commerce.TourSchedules ADD CONSTRAINT CK_TourSchedules_SellingPrices
            CHECK ((adult_price IS NULL OR adult_price > 0)
               AND (child_price IS NULL OR child_price >= 0)
               AND (infant_price IS NULL OR infant_price >= 0));';

    IF OBJECT_ID(N'commerce.CancellationPolicyTemplates', N'U') IS NULL
    BEGIN
        CREATE TABLE commerce.CancellationPolicyTemplates (
            cancellation_policy_template_id BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_CancellationPolicyTemplates PRIMARY KEY,
            name NVARCHAR(200) NOT NULL,
            description NVARCHAR(1000) NULL,
            is_active BIT NOT NULL CONSTRAINT DF_CancellationPolicyTemplates_Active DEFAULT 1,
            created_at DATETIME2 NOT NULL CONSTRAINT DF_CancellationPolicyTemplates_CreatedAt DEFAULT SYSUTCDATETIME(),
            updated_at DATETIME2 NOT NULL CONSTRAINT DF_CancellationPolicyTemplates_UpdatedAt DEFAULT SYSUTCDATETIME()
        );
        CREATE UNIQUE INDEX UX_CancellationPolicyTemplates_Name
            ON commerce.CancellationPolicyTemplates(name);
    END;

    IF OBJECT_ID(N'commerce.CancellationPolicyTemplateRules', N'U') IS NULL
    BEGIN
        CREATE TABLE commerce.CancellationPolicyTemplateRules (
            cancellation_policy_template_rule_id BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_CancellationPolicyTemplateRules PRIMARY KEY,
            cancellation_policy_template_id BIGINT NOT NULL,
            days_before_departure INT NOT NULL,
            cancellation_fee_percent DECIMAL(5,2) NOT NULL,
            CONSTRAINT FK_CancellationPolicyTemplateRules_Template FOREIGN KEY (cancellation_policy_template_id)
                REFERENCES commerce.CancellationPolicyTemplates(cancellation_policy_template_id) ON DELETE CASCADE,
            CONSTRAINT CK_CancellationPolicyTemplateRules_Days CHECK (days_before_departure >= 0),
            CONSTRAINT CK_CancellationPolicyTemplateRules_Fee CHECK (cancellation_fee_percent BETWEEN 0 AND 100),
            CONSTRAINT UQ_CancellationPolicyTemplateRules_Cutoff
                UNIQUE (cancellation_policy_template_id, days_before_departure)
        );
    END;

    /* Operations may change this template later; this seed makes a usable
       baseline available on a fresh environment without hard-coding it in C#. */
    IF NOT EXISTS (
        SELECT 1 FROM commerce.CancellationPolicyTemplates WHERE name = N'Standard scheduled departure')
    BEGIN
        INSERT INTO commerce.CancellationPolicyTemplates (name, description)
        VALUES (N'Standard scheduled departure', N'Default configurable policy for scheduled tour departures.');

        DECLARE @standardPolicyId BIGINT = SCOPE_IDENTITY();
        INSERT INTO commerce.CancellationPolicyTemplateRules (
            cancellation_policy_template_id, days_before_departure, cancellation_fee_percent)
        VALUES
            (@standardPolicyId, 30, 0),
            (@standardPolicyId, 15, 25),
            (@standardPolicyId, 7, 50),
            (@standardPolicyId, 0, 100);
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys
                   WHERE name = N'FK_Tours_CancellationPolicyTemplates'
                     AND parent_object_id = OBJECT_ID(N'commerce.Tours'))
        ALTER TABLE commerce.Tours ADD CONSTRAINT FK_Tours_CancellationPolicyTemplates
            FOREIGN KEY (cancellation_policy_template_id)
            REFERENCES commerce.CancellationPolicyTemplates(cancellation_policy_template_id);

    IF OBJECT_ID(N'commerce.TourCancellationPolicyRules', N'U') IS NULL
    BEGIN
        CREATE TABLE commerce.TourCancellationPolicyRules (
            tour_cancellation_policy_rule_id BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_TourCancellationPolicyRules PRIMARY KEY,
            tour_id BIGINT NOT NULL,
            days_before_departure INT NOT NULL,
            cancellation_fee_percent DECIMAL(5,2) NOT NULL,
            CONSTRAINT FK_TourCancellationPolicyRules_Tour FOREIGN KEY (tour_id)
                REFERENCES commerce.Tours(tour_id) ON DELETE CASCADE,
            CONSTRAINT CK_TourCancellationPolicyRules_Days CHECK (days_before_departure >= 0),
            CONSTRAINT CK_TourCancellationPolicyRules_Fee CHECK (cancellation_fee_percent BETWEEN 0 AND 100),
            CONSTRAINT UQ_TourCancellationPolicyRules_Cutoff UNIQUE (tour_id, days_before_departure)
        );
    END;

    IF OBJECT_ID(N'commerce.TourPackageCreateOperations', N'U') IS NULL
    BEGIN
        CREATE TABLE commerce.TourPackageCreateOperations (
            create_operation_id BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_TourPackageCreateOperations PRIMARY KEY,
            actor_user_id BIGINT NOT NULL,
            idempotency_key UNIQUEIDENTIFIER NOT NULL,
            payload_fingerprint CHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            tour_id BIGINT NULL,
            created_at DATETIME2 NOT NULL CONSTRAINT DF_TourPackageCreateOperations_CreatedAt DEFAULT SYSUTCDATETIME(),
            completed_at DATETIME2 NULL,
            CONSTRAINT FK_TourPackageCreateOperations_Actor FOREIGN KEY (actor_user_id)
                REFERENCES dbo.Users(user_id),
            CONSTRAINT FK_TourPackageCreateOperations_Tour FOREIGN KEY (tour_id)
                REFERENCES commerce.Tours(tour_id),
            CONSTRAINT CK_TourPackageCreateOperations_Fingerprint CHECK
                (LEN(payload_fingerprint) = 64 AND payload_fingerprint NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2),
            CONSTRAINT UQ_TourPackageCreateOperations_ActorKey UNIQUE (actor_user_id, idempotency_key)
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
