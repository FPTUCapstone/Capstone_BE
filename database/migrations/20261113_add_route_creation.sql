-- TM-101 / original SRS UC-54. Additive database-first migration; safe to repeat.
-- Existing rows are backfilled Active only if both existing endpoints are Active.
-- Unsafe legacy data stops the migration for Owner review, without changing it.
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'catalog.RouteSegments', N'U') IS NULL
        THROW 51000, 'Required catalog.RouteSegments table is missing.', 1;
    -- Fail closed on unsupported base schema before adding any columns.
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'segment_id'
        AND TYPE_NAME(system_type_id)=N'bigint' AND max_length=8 AND is_nullable=0 AND is_computed=0 AND is_identity=1)
        THROW 51000, 'Incompatible RouteSegments.segment_id column.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'from_poi_id'
        AND TYPE_NAME(system_type_id)=N'bigint' AND max_length=8 AND is_nullable=0 AND is_computed=0)
        THROW 51000, 'Incompatible RouteSegments.from_poi_id column.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'to_poi_id'
        AND TYPE_NAME(system_type_id)=N'bigint' AND max_length=8 AND is_nullable=0 AND is_computed=0)
        THROW 51000, 'Incompatible RouteSegments.to_poi_id column.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'transport_mode'
        AND TYPE_NAME(system_type_id)=N'varchar' AND max_length=20 AND is_nullable=0 AND is_computed=0)
        THROW 51000, 'Incompatible RouteSegments.transport_mode column.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'distance_km'
        AND TYPE_NAME(system_type_id)=N'decimal' AND max_length=5 AND is_nullable=0 AND is_computed=0 AND precision=6 AND scale=2)
        THROW 51000, 'Incompatible RouteSegments.distance_km column.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'est_duration_minutes'
        AND TYPE_NAME(system_type_id)=N'int' AND max_length=4 AND is_nullable=0 AND is_computed=0)
        THROW 51000, 'Incompatible RouteSegments.est_duration_minutes column.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'difficulty'
        AND TYPE_NAME(system_type_id)=N'varchar' AND max_length=10 AND is_nullable=1 AND is_computed=0)
        THROW 51000, 'Incompatible RouteSegments.difficulty column.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'slope_percent'
        AND TYPE_NAME(system_type_id)=N'decimal' AND max_length=5 AND is_nullable=1 AND is_computed=0 AND precision=4 AND scale=1)
        THROW 51000, 'Incompatible RouteSegments.slope_percent column.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'safety_score'
        AND TYPE_NAME(system_type_id)=N'decimal' AND max_length=5 AND is_nullable=1 AND is_computed=0 AND precision=3 AND scale=1)
        THROW 51000, 'Incompatible RouteSegments.safety_score column.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'scenic_score'
        AND TYPE_NAME(system_type_id)=N'decimal' AND max_length=5 AND is_nullable=1 AND is_computed=0 AND precision=3 AND scale=1)
        THROW 51000, 'Incompatible RouteSegments.scenic_score column.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'created_at'
        AND TYPE_NAME(system_type_id)=N'datetime2' AND max_length=8 AND is_nullable=0 AND is_computed=0 AND scale=7)
        THROW 51000, 'Incompatible RouteSegments.created_at column.', 1;

    IF COL_LENGTH(N'catalog.RouteSegments', N'status') IS NULL
    BEGIN
        IF EXISTS (SELECT 1 FROM catalog.RouteSegments r WITH (UPDLOCK,HOLDLOCK)
            LEFT JOIN catalog.POIs f WITH (HOLDLOCK) ON f.poi_id=r.from_poi_id
            LEFT JOIN catalog.POIs t WITH (HOLDLOCK) ON t.poi_id=r.to_poi_id
            WHERE f.poi_id IS NULL OR t.poi_id IS NULL OR f.status <> 'Active' OR t.status <> 'Active')
            THROW 51000, 'AMBIGUOUS_REQUIREMENT: legacy routes have non-Active endpoints. Preserve data and obtain Owner backfill decision.', 1;
        ALTER TABLE catalog.RouteSegments ADD status VARCHAR(10) NOT NULL
            CONSTRAINT DF_RouteSegments_Status DEFAULT ('Active') WITH VALUES;
    END;
    IF COL_LENGTH(N'catalog.RouteSegments', N'route_note') IS NULL
        ALTER TABLE catalog.RouteSegments ADD route_note NVARCHAR(2000) NULL;
    IF COL_LENGTH(N'catalog.RouteSegments', N'created_by') IS NULL
        ALTER TABLE catalog.RouteSegments ADD created_by BIGINT NULL;
    IF COL_LENGTH(N'catalog.RouteSegments', N'idempotency_key') IS NULL
        ALTER TABLE catalog.RouteSegments ADD idempotency_key UNIQUEIDENTIFIER NULL;

    -- Detect schema drift before accepting a partially compatible installation.
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'status'
        AND TYPE_NAME(system_type_id)=N'varchar' AND max_length=10 AND is_nullable=0 AND is_computed=0 AND is_identity=0)
        THROW 51000, 'Incompatible RouteSegments.status column.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'route_note'
        AND TYPE_NAME(system_type_id)=N'nvarchar' AND max_length=4000 AND is_nullable=1 AND is_computed=0 AND is_identity=0)
        THROW 51000, 'Incompatible RouteSegments.route_note column.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'created_by'
        AND TYPE_NAME(system_type_id)=N'bigint' AND is_nullable=1 AND is_computed=0 AND is_identity=0)
        THROW 51000, 'Incompatible RouteSegments.created_by column.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'idempotency_key'
        AND TYPE_NAME(system_type_id)=N'uniqueidentifier' AND is_nullable=1 AND is_computed=0 AND is_identity=0)
        THROW 51000, 'Incompatible RouteSegments.idempotency_key column.', 1;

    EXEC(N'IF EXISTS (SELECT 1 FROM catalog.RouteSegments WHERE status NOT IN (''Active'',''Inactive'')
        OR (idempotency_key IS NOT NULL AND (created_by IS NULL OR idempotency_key=''00000000-0000-0000-0000-000000000000'')))
        THROW 51000, ''Invalid existing route lifecycle or operation identity.'', 1;');
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'CK_RouteSegments_Status')
        EXEC(N'ALTER TABLE catalog.RouteSegments WITH CHECK ADD CONSTRAINT CK_RouteSegments_Status CHECK (status IN (''Active'',''Inactive''));');
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'CK_RouteSegments_Operation')
        EXEC(N'ALTER TABLE catalog.RouteSegments WITH CHECK ADD CONSTRAINT CK_RouteSegments_Operation CHECK (idempotency_key IS NULL OR (created_by IS NOT NULL AND idempotency_key<>''00000000-0000-0000-0000-000000000000''));');
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'FK_RouteSegments_CreatedBy')
        EXEC(N'ALTER TABLE catalog.RouteSegments WITH CHECK ADD CONSTRAINT FK_RouteSegments_CreatedBy FOREIGN KEY (created_by) REFERENCES dbo.Users(user_id);');
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name=N'UX_RouteSegments_ActorOperation')
        EXEC(N'CREATE UNIQUE INDEX UX_RouteSegments_ActorOperation ON catalog.RouteSegments(created_by,idempotency_key) WHERE idempotency_key IS NOT NULL;');

    -- Ask SQL Server to canonicalize the expected expressions using the same parser as the real constraints.
    CREATE TABLE #TM101ExpectedChecks (
        status varchar(10), idempotency_key uniqueidentifier, created_by bigint,
        from_poi_id bigint, to_poi_id bigint, distance_km decimal(6,2), est_duration_minutes int, transport_mode varchar(20),
        CHECK (status IN ('Active','Inactive')),
        CHECK (idempotency_key IS NULL OR (created_by IS NOT NULL AND idempotency_key<>'00000000-0000-0000-0000-000000000000')),
        CHECK (from_poi_id <> to_poi_id),
        CHECK (distance_km > 0),
        CHECK (est_duration_minutes > 0),
        CHECK (transport_mode IN ('Walking','Motorbike','Car','PublicTransit'))
    );
    -- Validate definitions and enforcement state, including objects that predate this migration.
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'catalog.RouteSegments')
        AND name=N'CK_RouteSegments_Status' AND is_disabled=0 AND is_not_trusted=0 AND is_not_for_replication=0
        AND definition COLLATE Latin1_General_100_BIN2 IN (SELECT e.definition COLLATE Latin1_General_100_BIN2 FROM tempdb.sys.check_constraints e WHERE e.parent_object_id=OBJECT_ID(N'tempdb..#TM101ExpectedChecks') AND e.definition LIKE N'%[[]status]%'))
        THROW 51000, 'Incompatible CK_RouteSegments_Status definition or enforcement state.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'catalog.RouteSegments')
        AND name=N'CK_RouteSegments_Operation' AND is_disabled=0 AND is_not_trusted=0 AND is_not_for_replication=0
        AND definition COLLATE Latin1_General_100_BIN2 IN (SELECT e.definition COLLATE Latin1_General_100_BIN2 FROM tempdb.sys.check_constraints e WHERE e.parent_object_id=OBJECT_ID(N'tempdb..#TM101ExpectedChecks') AND e.definition LIKE N'%[[]idempotency_key]%'))
        THROW 51000, 'Incompatible CK_RouteSegments_Operation definition or enforcement state.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys f WHERE f.parent_object_id=OBJECT_ID(N'catalog.RouteSegments')
        AND f.name=N'FK_RouteSegments_CreatedBy' AND f.referenced_object_id=OBJECT_ID(N'dbo.Users')
        AND f.is_disabled=0 AND f.is_not_trusted=0 AND f.is_not_for_replication=0
        AND f.delete_referential_action=0 AND f.update_referential_action=0
        AND (SELECT COUNT(*) FROM sys.foreign_key_columns c WHERE c.constraint_object_id=f.object_id)=1
        AND EXISTS (SELECT 1 FROM sys.foreign_key_columns c WHERE c.constraint_object_id=f.object_id
            AND COL_NAME(c.parent_object_id,c.parent_column_id)=N'created_by'
            AND COL_NAME(c.referenced_object_id,c.referenced_column_id)=N'user_id'))
        THROW 51000, 'Incompatible FK_RouteSegments_CreatedBy definition or enforcement state.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID(N'catalog.RouteSegments')
        AND i.name=N'UX_RouteSegments_ActorOperation' AND i.is_unique=1 AND i.type=2
        AND i.is_disabled=0 AND i.is_hypothetical=0 AND i.ignore_dup_key=0 AND i.has_filter=1
        AND LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(i.filter_definition,N' ',N''),N'[',N''),N']',N''),N'(',N''),N')',N''),CHAR(13),N''),CHAR(10),N''))=N'idempotency_keyisnotnull'
        AND (SELECT COUNT(*) FROM sys.index_columns c WHERE c.object_id=i.object_id AND c.index_id=i.index_id)=2
        AND EXISTS (SELECT 1 FROM sys.index_columns c WHERE c.object_id=i.object_id AND c.index_id=i.index_id
            AND c.key_ordinal=1 AND c.is_descending_key=0 AND c.is_included_column=0
            AND COL_NAME(c.object_id,c.column_id)=N'created_by')
        AND EXISTS (SELECT 1 FROM sys.index_columns c WHERE c.object_id=i.object_id AND c.index_id=i.index_id
            AND c.key_ordinal=2 AND c.is_descending_key=0 AND c.is_included_column=0
            AND COL_NAME(c.object_id,c.column_id)=N'idempotency_key'))
        THROW 51000, 'Incompatible UX_RouteSegments_ActorOperation definition or enforcement state.', 1;

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'catalog.RouteSegments')
        AND is_disabled=0 AND is_not_trusted=0 AND is_not_for_replication=0
        AND definition COLLATE Latin1_General_100_BIN2 IN (SELECT e.definition COLLATE Latin1_General_100_BIN2 FROM tempdb.sys.check_constraints e WHERE e.parent_object_id=OBJECT_ID(N'tempdb..#TM101ExpectedChecks') AND e.definition LIKE N'%[[]from_poi_id]%'))
        THROW 51000, 'Incompatible RouteSegments base CHECK: from_poi_id <> to_poi_id.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'catalog.RouteSegments')
        AND is_disabled=0 AND is_not_trusted=0 AND is_not_for_replication=0
        AND definition COLLATE Latin1_General_100_BIN2 IN (SELECT e.definition COLLATE Latin1_General_100_BIN2 FROM tempdb.sys.check_constraints e WHERE e.parent_object_id=OBJECT_ID(N'tempdb..#TM101ExpectedChecks') AND e.definition LIKE N'%[[]distance_km]%'))
        THROW 51000, 'Incompatible RouteSegments base CHECK: distance_km > 0.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'catalog.RouteSegments')
        AND is_disabled=0 AND is_not_trusted=0 AND is_not_for_replication=0
        AND definition COLLATE Latin1_General_100_BIN2 IN (SELECT e.definition COLLATE Latin1_General_100_BIN2 FROM tempdb.sys.check_constraints e WHERE e.parent_object_id=OBJECT_ID(N'tempdb..#TM101ExpectedChecks') AND e.definition LIKE N'%[[]est_duration_minutes]%'))
        THROW 51000, 'Incompatible RouteSegments base CHECK: est_duration_minutes > 0.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'catalog.RouteSegments')
        AND is_disabled=0 AND is_not_trusted=0 AND is_not_for_replication=0
        AND definition COLLATE Latin1_General_100_BIN2 IN (SELECT e.definition COLLATE Latin1_General_100_BIN2 FROM tempdb.sys.check_constraints e WHERE e.parent_object_id=OBJECT_ID(N'tempdb..#TM101ExpectedChecks') AND e.definition LIKE N'%[[]transport_mode]%'))
        THROW 51000, 'Incompatible RouteSegments base CHECK: transport_mode IN (''Walking'',''Motorbike'',''Car'',''PublicTransit'').', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys f WHERE f.parent_object_id=OBJECT_ID(N'catalog.RouteSegments')
        AND f.referenced_object_id=OBJECT_ID(N'catalog.POIs') AND f.is_disabled=0 AND f.is_not_trusted=0
        AND f.is_not_for_replication=0 AND f.delete_referential_action=0 AND f.update_referential_action=0
        AND (SELECT COUNT(*) FROM sys.foreign_key_columns c WHERE c.constraint_object_id=f.object_id)=1
        AND EXISTS (SELECT 1 FROM sys.foreign_key_columns c WHERE c.constraint_object_id=f.object_id
            AND COL_NAME(c.parent_object_id,c.parent_column_id)=N'from_poi_id'
            AND COL_NAME(c.referenced_object_id,c.referenced_column_id)=N'poi_id'))
        THROW 51000, 'Incompatible RouteSegments.from_poi_id foreign key.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys f WHERE f.parent_object_id=OBJECT_ID(N'catalog.RouteSegments')
        AND f.referenced_object_id=OBJECT_ID(N'catalog.POIs') AND f.is_disabled=0 AND f.is_not_trusted=0
        AND f.is_not_for_replication=0 AND f.delete_referential_action=0 AND f.update_referential_action=0
        AND (SELECT COUNT(*) FROM sys.foreign_key_columns c WHERE c.constraint_object_id=f.object_id)=1
        AND EXISTS (SELECT 1 FROM sys.foreign_key_columns c WHERE c.constraint_object_id=f.object_id
            AND COL_NAME(c.parent_object_id,c.parent_column_id)=N'to_poi_id'
            AND COL_NAME(c.referenced_object_id,c.referenced_column_id)=N'poi_id'))
        THROW 51000, 'Incompatible RouteSegments.to_poi_id foreign key.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID(N'catalog.RouteSegments')
        AND i.is_primary_key=1 AND i.is_unique=1 AND i.is_disabled=0 AND i.has_filter=0 AND i.ignore_dup_key=0
        AND (SELECT COUNT(*) FROM sys.index_columns c WHERE c.object_id=i.object_id AND c.index_id=i.index_id AND c.key_ordinal>0)=1
        AND EXISTS (SELECT 1 FROM sys.index_columns c WHERE c.object_id=i.object_id AND c.index_id=i.index_id
            AND c.key_ordinal=1 AND COL_NAME(c.object_id,c.column_id)=N'segment_id'))
        THROW 51000, 'Incompatible RouteSegments.segment_id primary key.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID(N'catalog.RouteSegments')
        AND i.name=N'UQ_RouteSegments' AND i.is_unique=1 AND i.is_disabled=0 AND i.is_hypothetical=0
        AND i.has_filter=0 AND i.ignore_dup_key=0
        AND (SELECT COUNT(*) FROM sys.index_columns c WHERE c.object_id=i.object_id AND c.index_id=i.index_id AND c.key_ordinal>0)=3
        AND EXISTS (SELECT 1 FROM sys.index_columns c WHERE c.object_id=i.object_id AND c.index_id=i.index_id
            AND c.key_ordinal=1 AND COL_NAME(c.object_id,c.column_id)=N'from_poi_id')
        AND EXISTS (SELECT 1 FROM sys.index_columns c WHERE c.object_id=i.object_id AND c.index_id=i.index_id
            AND c.key_ordinal=2 AND COL_NAME(c.object_id,c.column_id)=N'to_poi_id')
        AND EXISTS (SELECT 1 FROM sys.index_columns c WHERE c.object_id=i.object_id AND c.index_id=i.index_id
            AND c.key_ordinal=3 AND COL_NAME(c.object_id,c.column_id)=N'transport_mode'))
        THROW 51000, 'Incompatible UQ_RouteSegments definition or enforcement state.', 1;

    IF NOT EXISTS (SELECT 1 FROM sys.default_constraints d
        WHERE d.parent_object_id=OBJECT_ID(N'catalog.RouteSegments')
        AND COL_NAME(d.parent_object_id,d.parent_column_id)=N'status'
        AND REPLACE(REPLACE(d.definition,N'(',N''),N')',N'')=N'''Active''')
        THROW 51000, 'Incompatible RouteSegments.status default.', 1;

    DECLARE @expectedTrigger nvarchar(max)=N'CREATE OR ALTER TRIGGER catalog.TR_RouteSegments_ActiveEndpoints ON catalog.RouteSegments AFTER INSERT, UPDATE AS
    BEGIN
        SET NOCOUNT ON;
        IF EXISTS (SELECT 1 FROM inserted r
            LEFT JOIN catalog.POIs f WITH (HOLDLOCK) ON f.poi_id=r.from_poi_id
            LEFT JOIN catalog.POIs t WITH (HOLDLOCK) ON t.poi_id=r.to_poi_id
            WHERE r.status=''Active'' AND (f.poi_id IS NULL OR t.poi_id IS NULL OR f.status<>''Active'' OR t.status<>''Active''))
            THROW 51000, ''MSG141: Active routes require two distinct Active POIs.'', 1;
    END;';
    DECLARE @existingTrigger nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'catalog.TR_RouteSegments_ActiveEndpoints'));
    IF OBJECT_ID(N'catalog.TR_RouteSegments_ActiveEndpoints') IS NOT NULL
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.triggers WHERE object_id=OBJECT_ID(N'catalog.TR_RouteSegments_ActiveEndpoints')
            AND (is_disabled=1 OR is_instead_of_trigger=1 OR parent_id<>OBJECT_ID(N'catalog.RouteSegments')))
            THROW 51000, 'Incompatible TR_RouteSegments_ActiveEndpoints state.', 1;
        -- Normalize only the CREATE spelling and formatting around the otherwise exact module text.
        SET @existingTrigger=REPLACE(REPLACE(REPLACE(@existingTrigger,N'CREATE OR ALTER TRIGGER',N'CREATE TRIGGER'),CHAR(13),N''),CHAR(10),N'');
        SET @expectedTrigger=REPLACE(REPLACE(REPLACE(@expectedTrigger,N'CREATE OR ALTER TRIGGER',N'CREATE TRIGGER'),CHAR(13),N''),CHAR(10),N'');
        SET @existingTrigger=SUBSTRING(@existingTrigger,CHARINDEX(N'TRIGGER',@existingTrigger),LEN(@existingTrigger));
        SET @expectedTrigger=SUBSTRING(@expectedTrigger,CHARINDEX(N'TRIGGER',@expectedTrigger),LEN(@expectedTrigger));
        IF @existingTrigger IS NULL OR @existingTrigger COLLATE Latin1_General_100_BIN2 <> @expectedTrigger COLLATE Latin1_General_100_BIN2
            THROW 51000, 'Incompatible TR_RouteSegments_ActiveEndpoints definition.', 1;
    END
    ELSE EXEC(@expectedTrigger);
    DROP TABLE #TM101ExpectedChecks;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
