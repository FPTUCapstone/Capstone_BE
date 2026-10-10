/*
TM-70 V2 Phase 1.1: introduce the server-owned TourCategory taxonomy.

The migration is deliberately transitional and additive:
- existing Tours keep category_id = NULL;
- no category taxonomy row is fabricated;
- final NOT NULL enforcement waits for Product/BA-approved seeds and backfill.

The explicit shape checks below are intentional. This migration is rerunnable
against long-lived environments, so an object with the expected name but an
incompatible definition must fail atomically instead of being accepted as if
the TM-70 contract had already been installed.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF SCHEMA_ID(N'catalog') IS NULL
        EXEC(N'CREATE SCHEMA catalog');

    IF OBJECT_ID(N'commerce.Tours', N'U') IS NULL
        THROW 51000, 'TM-70 TourCategory migration requires commerce.Tours.', 1;

    IF OBJECT_ID(N'catalog.TourCategories', N'U') IS NULL
    BEGIN
        CREATE TABLE catalog.TourCategories (
            category_id INT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_TourCategories PRIMARY KEY,
            code VARCHAR(50) COLLATE Latin1_General_100_CI_AS NOT NULL,
            name NVARCHAR(100) COLLATE Latin1_General_100_CI_AS NOT NULL,
            is_active BIT NOT NULL
                CONSTRAINT DF_TourCategories_IsActive DEFAULT 1,
            CONSTRAINT CK_TourCategories_CodeNotBlank
                CHECK (LEN(LTRIM(RTRIM(code))) > 0),
            CONSTRAINT CK_TourCategories_NameNotBlank
                CHECK (LEN(LTRIM(RTRIM(name))) > 0)
        );
    END;

    IF (
        SELECT COUNT(*)
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'catalog.TourCategories')
    ) <> 4
        THROW 51000, 'TM-70 TourCategories table shape mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns AS c
        JOIN sys.types AS t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'catalog.TourCategories')
          AND c.name = N'category_id'
          AND t.name = N'int'
          AND c.is_identity = 1
          AND c.is_nullable = 0)
        THROW 51000, 'TM-70 TourCategories.category_id shape mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns AS c
        JOIN sys.types AS t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'catalog.TourCategories')
          AND c.name = N'code'
          AND t.name = N'varchar'
          AND c.max_length = 50
          AND c.is_nullable = 0
          AND c.collation_name = N'Latin1_General_100_CI_AS')
        THROW 51000, 'TM-70 TourCategories.code shape mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns AS c
        JOIN sys.types AS t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'catalog.TourCategories')
          AND c.name = N'name'
          AND t.name = N'nvarchar'
          AND c.max_length = 200
          AND c.is_nullable = 0
          AND c.collation_name = N'Latin1_General_100_CI_AS')
        THROW 51000, 'TM-70 TourCategories.name shape mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'catalog.TourCategories')
          AND name = N'is_active'
          AND system_type_id = TYPE_ID(N'bit')
          AND is_nullable = 0)
        THROW 51000, 'TM-70 TourCategories.is_active shape mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.key_constraints AS kc
        JOIN sys.indexes AS i
          ON i.object_id = kc.parent_object_id
         AND i.index_id = kc.unique_index_id
        JOIN sys.index_columns AS ic
          ON ic.object_id = i.object_id
         AND ic.index_id = i.index_id
        JOIN sys.columns AS c
          ON c.object_id = ic.object_id
         AND c.column_id = ic.column_id
        WHERE kc.parent_object_id = OBJECT_ID(N'catalog.TourCategories')
          AND kc.name = N'PK_TourCategories'
          AND kc.type = N'PK'
          AND i.is_unique = 1
          AND i.is_disabled = 0
          AND ic.is_included_column = 0
          AND ic.key_ordinal = 1
          AND ic.is_descending_key = 0
          AND c.name = N'category_id'
          AND NOT EXISTS (
              SELECT 1
              FROM sys.index_columns AS extra
              WHERE extra.object_id = i.object_id
                AND extra.index_id = i.index_id
                AND extra.is_included_column = 0
                AND extra.key_ordinal > 1))
        THROW 51000, 'TM-70 TourCategories primary key shape mismatch.', 1;

    IF (
        NOT EXISTS (
            SELECT 1
            FROM sys.check_constraints AS cc
            WHERE cc.parent_object_id = OBJECT_ID(N'catalog.TourCategories')
              AND cc.name = N'CK_TourCategories_CodeNotBlank'
              AND cc.is_disabled = 0
              AND cc.is_not_trusted = 0
              AND LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                  cc.definition, N' ', N''), CHAR(9), N''), CHAR(13), N''), CHAR(10), N''),
                  N'[', N''), N']', N''), N'(', N''), N')', N'')) =
                      N'lenltrimrtrimcode>0')
        OR NOT EXISTS (
            SELECT 1
            FROM sys.check_constraints AS cc
            WHERE cc.parent_object_id = OBJECT_ID(N'catalog.TourCategories')
              AND cc.name = N'CK_TourCategories_NameNotBlank'
              AND cc.is_disabled = 0
              AND cc.is_not_trusted = 0
              AND LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                  cc.definition, N' ', N''), CHAR(9), N''), CHAR(13), N''), CHAR(10), N''),
                  N'[', N''), N']', N''), N'(', N''), N')', N'')) =
                      N'lenltrimrtrimname>0')
    )
        THROW 51000, 'TM-70 TourCategories required-value constraint shape mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.default_constraints AS dc
        JOIN sys.columns AS c
          ON c.object_id = dc.parent_object_id
         AND c.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID(N'catalog.TourCategories')
          AND dc.name = N'DF_TourCategories_IsActive'
          AND c.name = N'is_active'
          AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
              dc.definition, N' ', N''), CHAR(9), N''), CHAR(13), N''), CHAR(10), N''),
              N'(', N''), N')', N'') = N'1')
        THROW 51000, 'TM-70 TourCategories.is_active default shape mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'catalog.TourCategories')
          AND name = N'UX_TourCategories_Code')
        CREATE UNIQUE INDEX UX_TourCategories_Code
            ON catalog.TourCategories(code);

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'catalog.TourCategories')
          AND name = N'UX_TourCategories_Code'
          AND type = 2
          AND is_unique = 1
          AND has_filter = 0
          AND is_disabled = 0
          AND is_hypothetical = 0)
        THROW 51000, 'TM-70 TourCategories code index shape mismatch.', 1;

    IF (
        SELECT COUNT(*)
        FROM sys.index_columns AS ic
        JOIN sys.indexes AS i
          ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        JOIN sys.columns AS c
          ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'catalog.TourCategories')
          AND i.name = N'UX_TourCategories_Code'
          AND ic.is_included_column = 0
          AND ic.key_ordinal = 1
          AND ic.is_descending_key = 0
          AND c.name = N'code'
    ) <> 1 OR (
        SELECT COUNT(*)
        FROM sys.index_columns AS ic
        JOIN sys.indexes AS i
          ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        WHERE i.object_id = OBJECT_ID(N'catalog.TourCategories')
          AND i.name = N'UX_TourCategories_Code'
          AND ic.is_included_column = 0
    ) <> 1
        THROW 51000, 'TM-70 TourCategories code index keys mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'catalog.TourCategories')
          AND name = N'IX_TourCategories_ActiveName')
        CREATE INDEX IX_TourCategories_ActiveName
            ON catalog.TourCategories(is_active, name, category_id);

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'catalog.TourCategories')
          AND name = N'IX_TourCategories_ActiveName'
          AND type = 2
          AND is_unique = 0
          AND has_filter = 0
          AND is_disabled = 0
          AND is_hypothetical = 0)
       OR (
        SELECT COUNT(*)
        FROM sys.index_columns AS ic
        JOIN sys.indexes AS i
          ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        WHERE i.object_id = OBJECT_ID(N'catalog.TourCategories')
          AND i.name = N'IX_TourCategories_ActiveName'
    ) <> 3
       OR (
        SELECT COUNT(*)
        FROM sys.index_columns AS ic
        JOIN sys.indexes AS i
          ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        JOIN sys.columns AS c
          ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'catalog.TourCategories')
          AND i.name = N'IX_TourCategories_ActiveName'
          AND ic.is_included_column = 0
          AND ic.is_descending_key = 0
          AND ((ic.key_ordinal = 1 AND c.name = N'is_active')
            OR (ic.key_ordinal = 2 AND c.name = N'name')
            OR (ic.key_ordinal = 3 AND c.name = N'category_id'))
    ) <> 3
        THROW 51000, 'TM-70 TourCategories active-name index keys mismatch.', 1;

    IF COL_LENGTH(N'commerce.Tours', N'category_id') IS NULL
        ALTER TABLE commerce.Tours ADD category_id INT NULL;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns AS c
        JOIN sys.types AS t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'commerce.Tours')
          AND c.name = N'category_id'
          AND t.name = N'int'
          AND c.is_nullable = 1)
        THROW 51000, 'TM-70 Tours.category_id shape mismatch.', 1;

    IF OBJECT_ID(N'commerce.FK_Tours_TourCategories', N'F') IS NULL
        ALTER TABLE commerce.Tours WITH CHECK
            ADD CONSTRAINT FK_Tours_TourCategories
            FOREIGN KEY (category_id)
            REFERENCES catalog.TourCategories(category_id);

    IF NOT EXISTS (
        SELECT 1
        FROM sys.foreign_keys AS fk
        JOIN sys.foreign_key_columns AS fkc
          ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns AS parent_column
          ON parent_column.object_id = fkc.parent_object_id
         AND parent_column.column_id = fkc.parent_column_id
        JOIN sys.columns AS referenced_column
          ON referenced_column.object_id = fkc.referenced_object_id
         AND referenced_column.column_id = fkc.referenced_column_id
        WHERE fk.parent_object_id = OBJECT_ID(N'commerce.Tours')
          AND fk.referenced_object_id = OBJECT_ID(N'catalog.TourCategories')
          AND fk.name = N'FK_Tours_TourCategories'
          AND fk.delete_referential_action = 0
          AND fk.update_referential_action = 0
          AND fk.is_disabled = 0
          AND fk.is_not_trusted = 0
          AND fkc.constraint_column_id = 1
          AND parent_column.name = N'category_id'
          AND referenced_column.name = N'category_id'
          AND NOT EXISTS (
              SELECT 1
              FROM sys.foreign_key_columns AS extra
              WHERE extra.constraint_object_id = fk.object_id
                AND extra.constraint_column_id > 1))
        THROW 51000, 'TM-70 Tours category foreign key shape mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'commerce.Tours')
          AND name = N'IX_Tours_Category')
        CREATE INDEX IX_Tours_Category ON commerce.Tours(category_id);

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'commerce.Tours')
          AND name = N'IX_Tours_Category'
          AND type = 2
          AND is_unique = 0
          AND has_filter = 0
          AND is_disabled = 0
          AND is_hypothetical = 0)
       OR (
        SELECT COUNT(*)
        FROM sys.index_columns AS ic
        JOIN sys.indexes AS i
          ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        WHERE i.object_id = OBJECT_ID(N'commerce.Tours')
          AND i.name = N'IX_Tours_Category'
    ) <> 1
       OR NOT EXISTS (
        SELECT 1
        FROM sys.index_columns AS ic
        JOIN sys.indexes AS i
          ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        JOIN sys.columns AS c
          ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'commerce.Tours')
          AND i.name = N'IX_Tours_Category'
          AND ic.is_included_column = 0
          AND ic.is_descending_key = 0
          AND ic.key_ordinal = 1
          AND c.name = N'category_id')
        THROW 51000, 'TM-70 Tours category index keys mismatch.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
