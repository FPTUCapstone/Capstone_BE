-- TM-79 Tasks 3a/6b: canonical parent only. POI evidence/children/media remain gated.
-- Upgrade only the fully verified pre-deferral parent shape to nullable policy metadata.
-- Never rewrite legacy social.Reviews or existing parent values.
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'commerce.Bookings',N'U') IS NULL OR OBJECT_ID(N'dbo.Users',N'U') IS NULL
       OR OBJECT_ID(N'commerce.Tours',N'U') IS NULL OR OBJECT_ID(N'planning.Itineraries',N'U') IS NULL
        THROW 51000, 'TM-79 requires the recorded v7 booking/subject dependencies.', 1;
    IF OBJECT_ID(N'social.TM79_ExpectedParent') IS NOT NULL
        THROW 51000, 'TM-79 validation object already exists; preserve it and investigate.', 1;
    IF OBJECT_ID(N'social.TripReviews') IS NOT NULL AND OBJECT_ID(N'social.TripReviews',N'U') IS NULL
        THROW 51000, 'TM-79 TripReviews object has the wrong type.', 1;
    -- A later approved migration owns the additive typed-parent shape. Fresh
    -- installs already carrying both successor columns must not be downgraded.
    IF OBJECT_ID(N'social.TripReviews',N'U') IS NOT NULL
       AND (COL_LENGTH(N'social.TripReviews',N'service_booking_id') IS NOT NULL
            OR COL_LENGTH(N'social.TripReviews',N'poi_id') IS NOT NULL)
    BEGIN
        IF COL_LENGTH(N'social.TripReviews',N'service_booking_id') IS NULL
           OR COL_LENGTH(N'social.TripReviews',N'poi_id') IS NULL
            THROW 51000, 'TM-79 successor parent columns are only partially present.', 1;
        COMMIT TRANSACTION;
        RETURN;
    END;
    IF OBJECT_ID(N'social.TripReviews',N'U') IS NULL
    BEGIN
CREATE TABLE social.TripReviews (
    trip_review_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TripReviews PRIMARY KEY,
    booking_id BIGINT NOT NULL CONSTRAINT FK_TripReviews_Booking REFERENCES commerce.Bookings(booking_id),
    traveler_user_id BIGINT NOT NULL CONSTRAINT FK_TripReviews_Traveler REFERENCES dbo.Users(user_id),
    tour_id BIGINT NULL CONSTRAINT FK_TripReviews_Tour REFERENCES commerce.Tours(tour_id),
    itinerary_id BIGINT NULL CONSTRAINT FK_TripReviews_Itinerary REFERENCES planning.Itineraries(itinerary_id),
    overall_rating TINYINT NOT NULL,
    title NVARCHAR(200) COLLATE Vietnamese_100_CI_AS NOT NULL,
    content NVARCHAR(1000) COLLATE Vietnamese_100_CI_AS NOT NULL,
    route_pacing VARCHAR(9) NULL,
    csp_rating TINYINT NULL,
    publish_display_name BIT NOT NULL,
    public_display_name NVARCHAR(MAX) COLLATE Vietnamese_100_CI_AS NOT NULL,
    publication_status VARCHAR(9) NOT NULL,
    policy_version NVARCHAR(MAX) COLLATE Vietnamese_100_CI_AS NULL,
    created_at DATETIME2(7) NOT NULL,
    edit_deadline DATETIME2(7) NOT NULL,
    updated_at DATETIME2(7) NOT NULL,
    version ROWVERSION NOT NULL,
    CONSTRAINT CK_TripReviews_Overall CHECK (overall_rating BETWEEN 1 AND 5),
    CONSTRAINT CK_TripReviews_Csp CHECK (csp_rating BETWEEN 1 AND 5),
    CONSTRAINT CK_TripReviews_Pacing CHECK (route_pacing IN ('tooTight','wellPaced','tooLoose')),
    CONSTRAINT CK_TripReviews_Subject CHECK (
        (tour_id IS NOT NULL AND itinerary_id IS NULL) OR
        (tour_id IS NULL AND itinerary_id IS NOT NULL)),
    CONSTRAINT CK_TripReviews_Publication CHECK (publication_status = 'Published'),
    CONSTRAINT CK_TripReviews_Title CHECK (LEN(title) > 0),
    CONSTRAINT CK_TripReviews_Content CHECK (LEN(content) > 0),
    -- NULL denotes unscreened text; reject the .NET whitespace set for non-null versions.
    CONSTRAINT CK_TripReviews_Policy CHECK (policy_version IS NULL OR LEN(TRIM(
        NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+
        NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+
        NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+
        NCHAR(8239)+NCHAR(8287)+NCHAR(12288) FROM policy_version COLLATE Latin1_General_100_BIN2)) > 0),
    CONSTRAINT CK_TripReviews_Display CHECK (LEN(public_display_name) > 0),
    CONSTRAINT CK_TripReviews_Deadline CHECK (edit_deadline = DATEADD(day, 7, created_at))
);
CREATE UNIQUE INDEX UX_TripReviews_Booking ON social.TripReviews(booking_id);
    END
    ELSE
    BEGIN
CREATE TABLE social.TM79_ExpectedParent (
    trip_review_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TM79_ExpectedParent PRIMARY KEY,
    booking_id BIGINT NOT NULL CONSTRAINT FK_TM79_ExpectedParent_Booking REFERENCES commerce.Bookings(booking_id),
    traveler_user_id BIGINT NOT NULL CONSTRAINT FK_TM79_ExpectedParent_Traveler REFERENCES dbo.Users(user_id),
    tour_id BIGINT NULL CONSTRAINT FK_TM79_ExpectedParent_Tour REFERENCES commerce.Tours(tour_id),
    itinerary_id BIGINT NULL CONSTRAINT FK_TM79_ExpectedParent_Itinerary REFERENCES planning.Itineraries(itinerary_id),
    overall_rating TINYINT NOT NULL,
    title NVARCHAR(200) COLLATE Vietnamese_100_CI_AS NOT NULL,
    content NVARCHAR(1000) COLLATE Vietnamese_100_CI_AS NOT NULL,
    route_pacing VARCHAR(9) NULL,
    csp_rating TINYINT NULL,
    publish_display_name BIT NOT NULL,
    public_display_name NVARCHAR(MAX) COLLATE Vietnamese_100_CI_AS NOT NULL,
    publication_status VARCHAR(9) NOT NULL,
    policy_version NVARCHAR(MAX) COLLATE Vietnamese_100_CI_AS NULL,
    created_at DATETIME2(7) NOT NULL,
    edit_deadline DATETIME2(7) NOT NULL,
    updated_at DATETIME2(7) NOT NULL,
    version ROWVERSION NOT NULL,
    CONSTRAINT CK_TM79_ExpectedParent_Overall CHECK (overall_rating BETWEEN 1 AND 5),
    CONSTRAINT CK_TM79_ExpectedParent_Csp CHECK (csp_rating BETWEEN 1 AND 5),
    CONSTRAINT CK_TM79_ExpectedParent_Pacing CHECK (route_pacing IN ('tooTight','wellPaced','tooLoose')),
    CONSTRAINT CK_TM79_ExpectedParent_Subject CHECK (
        (tour_id IS NOT NULL AND itinerary_id IS NULL) OR
        (tour_id IS NULL AND itinerary_id IS NOT NULL)),
    CONSTRAINT CK_TM79_ExpectedParent_Publication CHECK (publication_status = 'Published'),
    CONSTRAINT CK_TM79_ExpectedParent_Title CHECK (LEN(title) > 0),
    CONSTRAINT CK_TM79_ExpectedParent_Content CHECK (LEN(content) > 0),
    CONSTRAINT CK_TM79_ExpectedParent_Policy CHECK (policy_version IS NULL OR LEN(TRIM(
        NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+
        NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+
        NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+
        NCHAR(8239)+NCHAR(8287)+NCHAR(12288) FROM policy_version COLLATE Latin1_General_100_BIN2)) > 0),
    CONSTRAINT CK_TM79_ExpectedParent_Display CHECK (LEN(public_display_name) > 0),
    CONSTRAINT CK_TM79_ExpectedParent_Deadline CHECK (edit_deadline = DATEADD(day, 7, created_at))
);
CREATE UNIQUE INDEX UX_TM79_ExpectedParent_Booking ON social.TM79_ExpectedParent(booking_id);
        DECLARE @upgradePolicyNullability BIT = 0;
        IF EXISTS(SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'social.TripReviews') AND name=N'policy_version' AND is_nullable=0)
        BEGIN
            -- Compare every inventory entry with the known Task 3a shape before changing the parent.
            -- A NOT NULL column alone does not authorize an upgrade of an arbitrary table.
            SET @upgradePolicyNullability = 1;
            ALTER TABLE social.TM79_ExpectedParent DROP CONSTRAINT CK_TM79_ExpectedParent_Policy;
            ALTER TABLE social.TM79_ExpectedParent ALTER COLUMN policy_version NVARCHAR(MAX) COLLATE Vietnamese_100_CI_AS NOT NULL;
            ALTER TABLE social.TM79_ExpectedParent ADD CONSTRAINT CK_TM79_ExpectedParent_Policy CHECK (LEN(policy_version) > 0);
        END;
        DECLARE @actual TABLE(item NVARCHAR(MAX));
        DECLARE @expected TABLE(item NVARCHAR(MAX));
        DECLARE @target INT = OBJECT_ID(N'social.TripReviews');

            WITH TargetTables AS (SELECT @target AS object_id), Inventory AS (
                SELECT CONCAT(N'TABLE|', SCHEMA_NAME(o.schema_id), N'.', o.name) AS item
                FROM sys.objects AS o
                JOIN TargetTables AS target ON target.object_id = o.object_id
                WHERE o.type = N'U'

                UNION ALL

                SELECT CONCAT(
                    N'COLUMN|', SCHEMA_NAME(o.schema_id), N'.', o.name, N'|',
                    c.name, N'|', t.name, N'|', c.max_length,
                    N'|', c.precision, N'|', c.scale, N'|', c.is_nullable,
                    N'|', COALESCE(c.collation_name, N'<NULL>'), N'|', c.is_identity,
                    N'|', c.is_computed, N'|', COALESCE(dc.definition, N'<NULL>'))
                FROM sys.columns AS c
                JOIN sys.objects AS o ON o.object_id = c.object_id
                JOIN TargetTables AS target ON target.object_id = c.object_id
                JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                LEFT JOIN sys.default_constraints AS dc
                    ON dc.parent_object_id = c.object_id
                    AND dc.parent_column_id = c.column_id

                UNION ALL

                SELECT CONCAT(
                    N'INDEX|', SCHEMA_NAME(o.schema_id), N'.', o.name, N'|',
                    i.type, N'|', i.is_unique, N'|', i.is_primary_key,
                    N'|', i.is_unique_constraint, N'|', i.is_disabled, N'|', i.ignore_dup_key,
                    N'|', i.has_filter, N'|', COALESCE(i.filter_definition, N'<NULL>'),
                    N'|', STUFF((
                        SELECT N',' + COL_NAME(ic.object_id, ic.column_id)
                            + CASE WHEN ic.is_descending_key = 1 THEN N':DESC' ELSE N':ASC' END
                        FROM sys.index_columns AS ic
                        WHERE ic.object_id = i.object_id
                            AND ic.index_id = i.index_id
                            AND ic.key_ordinal > 0
                        ORDER BY ic.key_ordinal
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''),
                    N'|', STUFF((
                        SELECT N',' + COL_NAME(ic.object_id, ic.column_id)
                        FROM sys.index_columns AS ic
                        WHERE ic.object_id = i.object_id
                            AND ic.index_id = i.index_id
                            AND ic.is_included_column = 1
                        ORDER BY ic.index_column_id
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''))
                FROM sys.indexes AS i
                JOIN sys.objects AS o ON o.object_id = i.object_id
                JOIN TargetTables AS target ON target.object_id = i.object_id
                WHERE i.index_id > 0 AND i.is_hypothetical = 0

                UNION ALL

                SELECT CONCAT(
                    N'FOREIGN_KEY|', SCHEMA_NAME(parent_object.schema_id), N'.', parent_object.name,
                    N'|', SCHEMA_NAME(referenced_object.schema_id), N'.', referenced_object.name,
                    N'|', fk.delete_referential_action, N'|', fk.update_referential_action,
                    N'|', fk.is_disabled, N'|', fk.is_not_trusted, N'|', STUFF((
                        SELECT N',' + COL_NAME(fkc.parent_object_id, fkc.parent_column_id)
                            + N'->' + COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id)
                        FROM sys.foreign_key_columns AS fkc
                        WHERE fkc.constraint_object_id = fk.object_id
                        ORDER BY fkc.constraint_column_id
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''))
                FROM sys.foreign_keys AS fk
                JOIN sys.objects AS parent_object ON parent_object.object_id = fk.parent_object_id
                JOIN sys.objects AS referenced_object ON referenced_object.object_id = fk.referenced_object_id
                JOIN TargetTables AS target ON target.object_id = fk.parent_object_id

                UNION ALL

                SELECT CONCAT(
                    N'CHECK|', SCHEMA_NAME(o.schema_id), N'.', o.name,
                    N'|', cc.is_disabled, N'|', cc.is_not_trusted, N'|',
                    cc.definition)
                FROM sys.check_constraints AS cc
                JOIN sys.objects AS o ON o.object_id = cc.parent_object_id
                JOIN TargetTables AS target ON target.object_id = cc.parent_object_id
            )
            INSERT @actual(item) SELECT item FROM Inventory;

        SET @target = OBJECT_ID(N'social.TM79_ExpectedParent');

            WITH TargetTables AS (SELECT @target AS object_id), Inventory AS (
                SELECT CONCAT(N'TABLE|', SCHEMA_NAME(o.schema_id), N'.', o.name) AS item
                FROM sys.objects AS o
                JOIN TargetTables AS target ON target.object_id = o.object_id
                WHERE o.type = N'U'

                UNION ALL

                SELECT CONCAT(
                    N'COLUMN|', SCHEMA_NAME(o.schema_id), N'.', o.name, N'|',
                    c.name, N'|', t.name, N'|', c.max_length,
                    N'|', c.precision, N'|', c.scale, N'|', c.is_nullable,
                    N'|', COALESCE(c.collation_name, N'<NULL>'), N'|', c.is_identity,
                    N'|', c.is_computed, N'|', COALESCE(dc.definition, N'<NULL>'))
                FROM sys.columns AS c
                JOIN sys.objects AS o ON o.object_id = c.object_id
                JOIN TargetTables AS target ON target.object_id = c.object_id
                JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                LEFT JOIN sys.default_constraints AS dc
                    ON dc.parent_object_id = c.object_id
                    AND dc.parent_column_id = c.column_id

                UNION ALL

                SELECT CONCAT(
                    N'INDEX|', SCHEMA_NAME(o.schema_id), N'.', o.name, N'|',
                    i.type, N'|', i.is_unique, N'|', i.is_primary_key,
                    N'|', i.is_unique_constraint, N'|', i.is_disabled, N'|', i.ignore_dup_key,
                    N'|', i.has_filter, N'|', COALESCE(i.filter_definition, N'<NULL>'),
                    N'|', STUFF((
                        SELECT N',' + COL_NAME(ic.object_id, ic.column_id)
                            + CASE WHEN ic.is_descending_key = 1 THEN N':DESC' ELSE N':ASC' END
                        FROM sys.index_columns AS ic
                        WHERE ic.object_id = i.object_id
                            AND ic.index_id = i.index_id
                            AND ic.key_ordinal > 0
                        ORDER BY ic.key_ordinal
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''),
                    N'|', STUFF((
                        SELECT N',' + COL_NAME(ic.object_id, ic.column_id)
                        FROM sys.index_columns AS ic
                        WHERE ic.object_id = i.object_id
                            AND ic.index_id = i.index_id
                            AND ic.is_included_column = 1
                        ORDER BY ic.index_column_id
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''))
                FROM sys.indexes AS i
                JOIN sys.objects AS o ON o.object_id = i.object_id
                JOIN TargetTables AS target ON target.object_id = i.object_id
                WHERE i.index_id > 0 AND i.is_hypothetical = 0

                UNION ALL

                SELECT CONCAT(
                    N'FOREIGN_KEY|', SCHEMA_NAME(parent_object.schema_id), N'.', parent_object.name,
                    N'|', SCHEMA_NAME(referenced_object.schema_id), N'.', referenced_object.name,
                    N'|', fk.delete_referential_action, N'|', fk.update_referential_action,
                    N'|', fk.is_disabled, N'|', fk.is_not_trusted, N'|', STUFF((
                        SELECT N',' + COL_NAME(fkc.parent_object_id, fkc.parent_column_id)
                            + N'->' + COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id)
                        FROM sys.foreign_key_columns AS fkc
                        WHERE fkc.constraint_object_id = fk.object_id
                        ORDER BY fkc.constraint_column_id
                        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''))
                FROM sys.foreign_keys AS fk
                JOIN sys.objects AS parent_object ON parent_object.object_id = fk.parent_object_id
                JOIN sys.objects AS referenced_object ON referenced_object.object_id = fk.referenced_object_id
                JOIN TargetTables AS target ON target.object_id = fk.parent_object_id

                UNION ALL

                SELECT CONCAT(
                    N'CHECK|', SCHEMA_NAME(o.schema_id), N'.', o.name,
                    N'|', cc.is_disabled, N'|', cc.is_not_trusted, N'|',
                    cc.definition)
                FROM sys.check_constraints AS cc
                JOIN sys.objects AS o ON o.object_id = cc.parent_object_id
                JOIN TargetTables AS target ON target.object_id = cc.parent_object_id
            )
            INSERT @expected(item) SELECT item FROM Inventory;

        IF EXISTS(SELECT item FROM @actual EXCEPT SELECT REPLACE(item,N'social.TM79_ExpectedParent',N'social.TripReviews') FROM @expected)
           OR EXISTS(SELECT REPLACE(item,N'social.TM79_ExpectedParent',N'social.TripReviews') FROM @expected EXCEPT SELECT item FROM @actual)
            THROW 51000, 'TM-79 existing parent inventory differs from the canonical contract; no automatic repair performed.',1;
        DROP TABLE social.TM79_ExpectedParent;
        IF @upgradePolicyNullability = 1
        BEGIN
            ALTER TABLE social.TripReviews DROP CONSTRAINT CK_TripReviews_Policy;
            ALTER TABLE social.TripReviews ALTER COLUMN policy_version NVARCHAR(MAX) COLLATE Vietnamese_100_CI_AS NULL;
            -- WITH CHECK validates existing values; incompatible data rolls back the whole upgrade.
            ALTER TABLE social.TripReviews WITH CHECK ADD CONSTRAINT CK_TripReviews_Policy CHECK (policy_version IS NULL OR LEN(TRIM(
        NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+
        NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+
        NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+
        NCHAR(8239)+NCHAR(8287)+NCHAR(12288) FROM policy_version COLLATE Latin1_General_100_BIN2)) > 0);
        END;
    END;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
