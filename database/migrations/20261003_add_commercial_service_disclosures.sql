/*
    TM-76 / UC-30: truthful public commercial-service disclosures.
    This migration upgrades pre-UC-30 schema safely. It backfills known
    legacy nulls, then canonicalizes all fields, defaults and checks.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commercial.Services', N'U') IS NULL
        THROW 51000, 'TM-76 migration requires commercial.Services.', 1;

    IF COL_LENGTH(N'commercial.Services', N'currency_code') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commercial.Services ADD currency_code VARCHAR(3) NULL;';
    IF COL_LENGTH(N'commercial.Services', N'price_includes_tax') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commercial.Services ADD price_includes_tax BIT NULL;';
    IF COL_LENGTH(N'commercial.Services', N'refundable_deposit_amount') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commercial.Services ADD refundable_deposit_amount DECIMAL(12,2) NULL;';
    IF COL_LENGTH(N'commercial.Services', N'cover_image_url') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commercial.Services ADD cover_image_url NVARCHAR(500) NULL;';
    IF COL_LENGTH(N'commercial.Services', N'fulfilment_location_label') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commercial.Services ADD fulfilment_location_label NVARCHAR(300) NULL;';
    IF COL_LENGTH(N'commercial.Services', N'pickup_or_arrival_instructions') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commercial.Services ADD pickup_or_arrival_instructions NVARCHAR(1000) NULL;';
    IF COL_LENGTH(N'commercial.Services', N'cancellation_policy_summary') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commercial.Services ADD cancellation_policy_summary NVARCHAR(500) NULL;';
    IF COL_LENGTH(N'commercial.Services', N'last_updated_at') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commercial.Services ADD last_updated_at DATETIME2 NULL;';

    EXEC sys.sp_executesql N'
        UPDATE commercial.Services SET currency_code = ''VND'' WHERE currency_code IS NULL;
        UPDATE commercial.Services SET price_includes_tax = 0 WHERE price_includes_tax IS NULL;
        UPDATE commercial.Services SET last_updated_at = SYSUTCDATETIME() WHERE last_updated_at IS NULL;

        IF EXISTS (SELECT 1 FROM commercial.Services WHERE currency_code <> ''VND'')
            THROW 51000, ''TM-76 supports VND commercial services only.'', 1;
        IF EXISTS (SELECT 1 FROM commercial.Services WHERE refundable_deposit_amount < 0)
            THROW 51000, ''TM-76 cannot upgrade negative refundable deposits.'', 1;
        IF EXISTS (SELECT 1 FROM commercial.Services WHERE cover_image_url IS NOT NULL
            AND cover_image_url NOT LIKE ''https://%'')
            THROW 51000, ''TM-76 requires HTTPS commercial-service cover images.'', 1;';

    -- Remove every default constraint currently attached to the affected columns before
    -- canonicalizing their type/nullability. SQL Server otherwise treats a legacy default
    -- as a dependent object and may reject ALTER COLUMN.
    DECLARE @defaultConstraint SYSNAME;
    DECLARE @dropDefaultConstraintSql NVARCHAR(MAX);
    SELECT @defaultConstraint = constraint_info.name
    FROM sys.default_constraints AS constraint_info
    INNER JOIN sys.columns AS column_info
        ON column_info.object_id = constraint_info.parent_object_id
        AND column_info.column_id = constraint_info.parent_column_id
    WHERE constraint_info.parent_object_id = OBJECT_ID(N'commercial.Services')
        AND column_info.name = N'currency_code';
    IF @defaultConstraint IS NOT NULL
    BEGIN
        SET @dropDefaultConstraintSql =
            N'ALTER TABLE commercial.Services DROP CONSTRAINT ' + QUOTENAME(@defaultConstraint);
        EXEC sys.sp_executesql @dropDefaultConstraintSql;
    END;

    SET @defaultConstraint = NULL;
    SELECT @defaultConstraint = constraint_info.name
    FROM sys.default_constraints AS constraint_info
    INNER JOIN sys.columns AS column_info
        ON column_info.object_id = constraint_info.parent_object_id
        AND column_info.column_id = constraint_info.parent_column_id
    WHERE constraint_info.parent_object_id = OBJECT_ID(N'commercial.Services')
        AND column_info.name = N'price_includes_tax';
    IF @defaultConstraint IS NOT NULL
    BEGIN
        SET @dropDefaultConstraintSql =
            N'ALTER TABLE commercial.Services DROP CONSTRAINT ' + QUOTENAME(@defaultConstraint);
        EXEC sys.sp_executesql @dropDefaultConstraintSql;
    END;

    SET @defaultConstraint = NULL;
    SELECT @defaultConstraint = constraint_info.name
    FROM sys.default_constraints AS constraint_info
    INNER JOIN sys.columns AS column_info
        ON column_info.object_id = constraint_info.parent_object_id
        AND column_info.column_id = constraint_info.parent_column_id
    WHERE constraint_info.parent_object_id = OBJECT_ID(N'commercial.Services')
        AND column_info.name = N'last_updated_at';
    IF @defaultConstraint IS NOT NULL
    BEGIN
        SET @dropDefaultConstraintSql =
            N'ALTER TABLE commercial.Services DROP CONSTRAINT ' + QUOTENAME(@defaultConstraint);
        EXEC sys.sp_executesql @dropDefaultConstraintSql;
    END;

    -- These names are owned by TM-76. Dropping then recreating them repairs a same-name
    -- wrong-shape constraint without touching unrelated legacy constraints.
    IF OBJECT_ID(N'commercial.CK_Services_CurrencyCode', N'C') IS NOT NULL
        ALTER TABLE commercial.Services DROP CONSTRAINT CK_Services_CurrencyCode;
    IF OBJECT_ID(N'commercial.CK_Services_RefundableDepositNonNegative', N'C') IS NOT NULL
        ALTER TABLE commercial.Services DROP CONSTRAINT CK_Services_RefundableDepositNonNegative;
    IF OBJECT_ID(N'commercial.CK_Services_CoverImageHttps', N'C') IS NOT NULL
        ALTER TABLE commercial.Services DROP CONSTRAINT CK_Services_CoverImageHttps;
    IF OBJECT_ID(N'commercial.CK_Services_PickupInstructionsLength', N'C') IS NOT NULL
        ALTER TABLE commercial.Services DROP CONSTRAINT CK_Services_PickupInstructionsLength;
    IF OBJECT_ID(N'commercial.CK_Services_CancellationSummaryLength', N'C') IS NOT NULL
        ALTER TABLE commercial.Services DROP CONSTRAINT CK_Services_CancellationSummaryLength;

    EXEC sys.sp_executesql N'
        ALTER TABLE commercial.Services ALTER COLUMN currency_code VARCHAR(3) NOT NULL;
        ALTER TABLE commercial.Services ALTER COLUMN price_includes_tax BIT NOT NULL;
        ALTER TABLE commercial.Services ALTER COLUMN refundable_deposit_amount DECIMAL(12,2) NULL;
        ALTER TABLE commercial.Services ALTER COLUMN cover_image_url NVARCHAR(500) NULL;
        ALTER TABLE commercial.Services ALTER COLUMN fulfilment_location_label NVARCHAR(300) NULL;
        ALTER TABLE commercial.Services ALTER COLUMN pickup_or_arrival_instructions NVARCHAR(1000) NULL;
        ALTER TABLE commercial.Services ALTER COLUMN cancellation_policy_summary NVARCHAR(500) NULL;
        ALTER TABLE commercial.Services ALTER COLUMN last_updated_at DATETIME2 NOT NULL;';
    ALTER TABLE commercial.ServiceProviders ALTER COLUMN name
        NVARCHAR(150) COLLATE Vietnamese_100_CI_AS NOT NULL;
    ALTER TABLE commercial.Services ALTER COLUMN name
        NVARCHAR(200) COLLATE Vietnamese_100_CI_AS NOT NULL;

    EXEC sys.sp_executesql N'
        ALTER TABLE commercial.Services ADD CONSTRAINT DF_Services_CurrencyCode
            DEFAULT ''VND'' FOR currency_code;
        ALTER TABLE commercial.Services ADD CONSTRAINT DF_Services_PriceIncludesTax
            DEFAULT 0 FOR price_includes_tax;
        ALTER TABLE commercial.Services ADD CONSTRAINT DF_Services_LastUpdatedAt
            DEFAULT SYSUTCDATETIME() FOR last_updated_at;

        ALTER TABLE commercial.Services ADD CONSTRAINT CK_Services_CurrencyCode
            CHECK (currency_code = ''VND'');
        ALTER TABLE commercial.Services ADD CONSTRAINT CK_Services_RefundableDepositNonNegative
            CHECK (refundable_deposit_amount IS NULL OR refundable_deposit_amount >= 0);
        ALTER TABLE commercial.Services ADD CONSTRAINT CK_Services_CoverImageHttps
            CHECK (cover_image_url IS NULL OR cover_image_url LIKE ''https://%'');
        ALTER TABLE commercial.Services ADD CONSTRAINT CK_Services_PickupInstructionsLength
            CHECK (pickup_or_arrival_instructions IS NULL
                OR LEN(pickup_or_arrival_instructions) <= 1000);
        ALTER TABLE commercial.Services ADD CONSTRAINT CK_Services_CancellationSummaryLength
            CHECK (cancellation_policy_summary IS NULL
                OR LEN(cancellation_policy_summary) <= 500);';

    EXEC(N'
        CREATE OR ALTER TRIGGER commercial.TR_Services_SetLastUpdatedAt
        ON commercial.Services
        AFTER UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;

            IF UPDATE(provider_id) OR UPDATE(service_category) OR UPDATE(name) OR UPDATE(description)
                OR UPDATE(poi_id) OR UPDATE(price_amount) OR UPDATE(price_unit)
                OR UPDATE(capacity) OR UPDATE(attributes_json) OR UPDATE(availability_status)
                OR UPDATE(currency_code) OR UPDATE(price_includes_tax)
                OR UPDATE(refundable_deposit_amount) OR UPDATE(cover_image_url)
                OR UPDATE(fulfilment_location_label) OR UPDATE(pickup_or_arrival_instructions)
                OR UPDATE(cancellation_policy_summary)
            BEGIN
                UPDATE service
                SET last_updated_at = SYSUTCDATETIME()
                FROM commercial.Services AS service
                INNER JOIN inserted AS changed ON changed.service_id = service.service_id;
            END;
        END;');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
