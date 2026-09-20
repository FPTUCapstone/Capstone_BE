/*
    TM-70 D1: operator-tagged tour regions and whole-VND price.
    Additive/idempotent; never infer regions from legacy Tours.destination or POIs.
    A prior draft may have added Tours.destination; retain that data for owner-led
    backfill, but the public API no longer reads the column.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commerce.Tours', N'U') IS NULL
        THROW 51000, 'TM-70 migration requires commerce.Tours.', 1;

    IF EXISTS (SELECT 1 FROM commerce.Tours WHERE base_price <> FLOOR(base_price))
        THROW 51000, 'TM-70 cannot enforce whole-VND prices while fractional base_price values exist.', 1;

    IF SCHEMA_ID(N'catalog') IS NULL
        EXEC(N'CREATE SCHEMA catalog');

    IF OBJECT_ID(N'catalog.Destinations', N'U') IS NULL
    BEGIN
        IF OBJECT_ID(N'catalog.Destinations') IS NOT NULL
            THROW 51000, 'TM-70 Destinations object has the wrong type.', 1;
        CREATE TABLE catalog.Destinations (
            destination_id BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_Destinations PRIMARY KEY,
            name NVARCHAR(300) COLLATE Vietnamese_100_CI_AS NOT NULL
        );
    END;

    IF (SELECT COUNT(*) FROM sys.columns
        WHERE object_id = OBJECT_ID(N'catalog.Destinations')) <> 2
        OR NOT EXISTS (
            SELECT 1 FROM sys.columns AS c JOIN sys.types AS t
                ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(N'catalog.Destinations')
              AND c.name = N'destination_id' AND t.name = N'bigint'
              AND c.is_nullable = 0 AND c.is_identity = 1)
        OR NOT EXISTS (
            SELECT 1 FROM sys.columns AS c JOIN sys.types AS t
                ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(N'catalog.Destinations')
              AND c.name = N'name' AND t.name = N'nvarchar'
              AND c.max_length = 600 AND c.is_nullable = 0
              AND c.collation_name = N'Vietnamese_100_CI_AS')
        OR NOT EXISTS (SELECT 1 FROM sys.key_constraints
                       WHERE parent_object_id = OBJECT_ID(N'catalog.Destinations')
                         AND name = N'PK_Destinations' AND type = N'PK')
        THROW 51000, 'TM-70 Destinations table shape mismatch; manual reconciliation is required.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.key_constraints AS k
        JOIN sys.index_columns AS ic
          ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
        WHERE k.parent_object_id = OBJECT_ID(N'catalog.Destinations')
          AND k.name = N'PK_Destinations' AND ic.key_ordinal = 1
          AND COL_NAME(ic.object_id, ic.column_id) = N'destination_id'
          AND (SELECT COUNT(*) FROM sys.index_columns AS key_column
               WHERE key_column.object_id = k.parent_object_id
                 AND key_column.index_id = k.unique_index_id
                 AND key_column.key_ordinal > 0) = 1)
        THROW 51000, 'TM-70 Destinations primary key shape mismatch.', 1;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'catalog.Destinations')
                     AND name = N'UX_Destinations_Name')
        CREATE UNIQUE INDEX UX_Destinations_Name ON catalog.Destinations(name);

    IF OBJECT_ID(N'commerce.TourDestinations', N'U') IS NULL
    BEGIN
        IF OBJECT_ID(N'commerce.TourDestinations') IS NOT NULL
            THROW 51000, 'TM-70 TourDestinations object has the wrong type.', 1;
        CREATE TABLE commerce.TourDestinations (
            tour_id BIGINT NOT NULL,
            destination_id BIGINT NOT NULL,
            sequence_no INT NOT NULL,
            CONSTRAINT PK_TourDestinations PRIMARY KEY (tour_id, destination_id),
            CONSTRAINT FK_TourDestinations_Tours FOREIGN KEY (tour_id)
                REFERENCES commerce.Tours(tour_id) ON DELETE CASCADE,
            CONSTRAINT FK_TourDestinations_Destinations FOREIGN KEY (destination_id)
                REFERENCES catalog.Destinations(destination_id),
            CONSTRAINT CK_TourDestinations_SequencePositive CHECK (sequence_no > 0)
        );
    END;

    IF (SELECT COUNT(*) FROM sys.columns
        WHERE object_id = OBJECT_ID(N'commerce.TourDestinations')) <> 3
        OR EXISTS (
            SELECT 1 FROM sys.columns AS c JOIN sys.types AS t
                ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(N'commerce.TourDestinations')
              AND (c.is_nullable = 1 OR c.is_identity = 1
                   OR (c.name IN (N'tour_id', N'destination_id') AND t.name <> N'bigint')
                   OR (c.name = N'sequence_no' AND t.name <> N'int')))
        OR NOT EXISTS (SELECT 1 FROM sys.key_constraints
                       WHERE parent_object_id = OBJECT_ID(N'commerce.TourDestinations')
                         AND name = N'PK_TourDestinations' AND type = N'PK')
        OR NOT EXISTS (SELECT 1 FROM sys.foreign_keys
                       WHERE parent_object_id = OBJECT_ID(N'commerce.TourDestinations')
                         AND referenced_object_id = OBJECT_ID(N'commerce.Tours')
                         AND name = N'FK_TourDestinations_Tours'
                         AND delete_referential_action = 1
                         AND is_disabled = 0 AND is_not_trusted = 0)
        OR NOT EXISTS (SELECT 1 FROM sys.foreign_keys
                       WHERE parent_object_id = OBJECT_ID(N'commerce.TourDestinations')
                         AND referenced_object_id = OBJECT_ID(N'catalog.Destinations')
                         AND name = N'FK_TourDestinations_Destinations'
                         AND delete_referential_action = 0
                         AND is_disabled = 0 AND is_not_trusted = 0)
        OR NOT EXISTS (SELECT 1 FROM sys.check_constraints
                       WHERE parent_object_id = OBJECT_ID(N'commerce.TourDestinations')
                         AND name = N'CK_TourDestinations_SequencePositive'
                         AND is_disabled = 0 AND is_not_trusted = 0)
        THROW 51000, 'TM-70 TourDestinations table shape mismatch; manual reconciliation is required.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.key_constraints AS k
        WHERE k.parent_object_id = OBJECT_ID(N'commerce.TourDestinations')
          AND k.name = N'PK_TourDestinations'
          AND (SELECT COUNT(*) FROM sys.index_columns AS ic
               WHERE ic.object_id = k.parent_object_id
                 AND ic.index_id = k.unique_index_id AND ic.key_ordinal > 0) = 2
          AND EXISTS (SELECT 1 FROM sys.index_columns AS ic
                      WHERE ic.object_id = k.parent_object_id
                        AND ic.index_id = k.unique_index_id AND ic.key_ordinal = 1
                        AND COL_NAME(ic.object_id, ic.column_id) = N'tour_id')
          AND EXISTS (SELECT 1 FROM sys.index_columns AS ic
                      WHERE ic.object_id = k.parent_object_id
                        AND ic.index_id = k.unique_index_id AND ic.key_ordinal = 2
                        AND COL_NAME(ic.object_id, ic.column_id) = N'destination_id'))
        THROW 51000, 'TM-70 TourDestinations primary key shape mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys AS fk
        JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        WHERE fk.name = N'FK_TourDestinations_Tours'
          AND fk.parent_object_id = OBJECT_ID(N'commerce.TourDestinations')
          AND fkc.constraint_column_id = 1
          AND COL_NAME(fkc.parent_object_id, fkc.parent_column_id) = N'tour_id'
          AND COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) = N'tour_id')
        OR NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys AS fk
        JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        WHERE fk.name = N'FK_TourDestinations_Destinations'
          AND fk.parent_object_id = OBJECT_ID(N'commerce.TourDestinations')
          AND fkc.constraint_column_id = 1
          AND COL_NAME(fkc.parent_object_id, fkc.parent_column_id) = N'destination_id'
          AND COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) = N'destination_id')
        THROW 51000, 'TM-70 TourDestinations foreign key shape mismatch.', 1;

    DECLARE @sequenceDefinition NVARCHAR(MAX) = LOWER(
        OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_TourDestinations_SequencePositive', N'C')));
    SET @sequenceDefinition = REPLACE(REPLACE(REPLACE(
        REPLACE(REPLACE(@sequenceDefinition, N'[', N''), N']', N''), N'(', N''), N')', N''), N' ', N'');
    IF @sequenceDefinition IS NULL OR @sequenceDefinition <> N'sequence_no>0'
        THROW 51000, 'TM-70 TourDestinations sequence constraint definition mismatch.', 1;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'commerce.TourDestinations')
                     AND name = N'UX_TourDestinations_TourSequence')
        CREATE UNIQUE INDEX UX_TourDestinations_TourSequence
            ON commerce.TourDestinations(tour_id, sequence_no);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'commerce.TourDestinations')
                     AND name = N'IX_TourDestinations_Destination')
        CREATE INDEX IX_TourDestinations_Destination
            ON commerce.TourDestinations(destination_id);

    -- A same-named index can have different keys or uniqueness. Never silently
    -- accept it as a valid migration on a shared or cloud database.
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes AS i
        WHERE i.object_id = OBJECT_ID(N'catalog.Destinations')
          AND i.name = N'UX_Destinations_Name' AND i.is_unique = 1
          AND i.is_disabled = 0 AND i.has_filter = 0
          AND (SELECT COUNT(*) FROM sys.index_columns AS ic
               WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                 AND ic.key_ordinal > 0) = 1
          AND EXISTS (SELECT 1 FROM sys.index_columns AS ic
                      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                        AND ic.key_ordinal = 1
                        AND COL_NAME(ic.object_id, ic.column_id) = N'name'))
        THROW 51000, 'TM-70 Destinations name index shape mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes AS i
        WHERE i.object_id = OBJECT_ID(N'commerce.TourDestinations')
          AND i.name = N'UX_TourDestinations_TourSequence' AND i.is_unique = 1
          AND i.is_disabled = 0 AND i.has_filter = 0
          AND (SELECT COUNT(*) FROM sys.index_columns AS ic
               WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                 AND ic.key_ordinal > 0) = 2
          AND EXISTS (SELECT 1 FROM sys.index_columns AS ic
                      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                        AND ic.key_ordinal = 1
                        AND COL_NAME(ic.object_id, ic.column_id) = N'tour_id')
          AND EXISTS (SELECT 1 FROM sys.index_columns AS ic
                      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                        AND ic.key_ordinal = 2
                        AND COL_NAME(ic.object_id, ic.column_id) = N'sequence_no'))
        THROW 51000, 'TM-70 TourDestinations sequence index shape mismatch.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes AS i
        WHERE i.object_id = OBJECT_ID(N'commerce.TourDestinations')
          AND i.name = N'IX_TourDestinations_Destination' AND i.is_unique = 0
          AND i.is_disabled = 0 AND i.has_filter = 0
          AND (SELECT COUNT(*) FROM sys.index_columns AS ic
               WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                 AND ic.key_ordinal > 0) = 1
          AND EXISTS (SELECT 1 FROM sys.index_columns AS ic
                      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                        AND ic.key_ordinal = 1
                        AND COL_NAME(ic.object_id, ic.column_id) = N'destination_id'))
        THROW 51000, 'TM-70 TourDestinations lookup index shape mismatch.', 1;

    IF OBJECT_ID(N'commerce.CK_Tours_BasePriceWholeVnd', N'C') IS NULL
        ALTER TABLE commerce.Tours WITH CHECK
            ADD CONSTRAINT CK_Tours_BasePriceWholeVnd
            CHECK (base_price = FLOOR(base_price));

    DECLARE @definition NVARCHAR(MAX) = LOWER(
        OBJECT_DEFINITION(OBJECT_ID(N'commerce.CK_Tours_BasePriceWholeVnd', N'C')));
    DECLARE @normalizedDefinition NVARCHAR(MAX) = REPLACE(REPLACE(REPLACE(
        REPLACE(REPLACE(@definition, N'[', N''), N']', N''), N'(', N''), N')', N''), N' ', N'');
    IF @normalizedDefinition IS NULL OR @normalizedDefinition <> N'base_price=floorbase_price'
        THROW 51000, 'TM-70 whole-VND constraint definition mismatch; manual reconciliation is required.', 1;

    ALTER TABLE commerce.Tours WITH CHECK CHECK CONSTRAINT CK_Tours_BasePriceWholeVnd;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
