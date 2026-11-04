/* TM-70 A4.1a R2.S1: inactive epoch metadata and noncycling allocator only.
   No publication, active epoch, worker, journal, or production seed is introduced.
   Run standalone on one connection. Compatible reruns never consume/reset a number. */
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF @@TRANCOUNT <> 0
    THROW 51070, 'TM70.A41a.AmbientTransaction', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @lockResult int;
    EXEC @lockResult = sys.sp_getapplock
        @Resource = N'TM70:A41A:EpochAllocator:Migration:v1',
        @LockMode = 'Exclusive', @LockOwner = 'Transaction',
        @LockTimeout = 15000, @DbPrincipal = 'public';
    IF @lockResult < 0 OR @lockResult IS NULL
        THROW 51071, 'TM70.A41a.MigrationLock', 1;

    IF COALESCE(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'), 0) <> 1
        THROW 51072, 'TM70.A41a.MetadataVisibility', 1;

    IF SCHEMA_ID(N'commerce') IS NULL
        OR OBJECT_ID(N'commerce.Tours', N'U') IS NULL
        OR OBJECT_ID(N'commerce.TourSchedules', N'U') IS NULL
        OR OBJECT_ID(N'dbo.Users', N'U') IS NULL
        OR NOT EXISTS (SELECT 1 FROM sys.check_constraints
            WHERE parent_object_id=OBJECT_ID(N'commerce.Tours') AND name=N'CK_Tours_Status'
              AND is_disabled=0 AND is_not_trusted=0 AND definition LIKE N'%''Published''%')
        OR NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id
            WHERE c.object_id=OBJECT_ID(N'commerce.Tours') AND c.name=N'destination_summary'
              AND t.name=N'nvarchar' AND c.max_length=600 AND c.is_nullable=0
              AND c.default_object_id<>0)
        THROW 51073, 'TM70.A41a.Prerequisite', 1;

    DECLARE @triggerBody nvarchar(max) = N'CREATE TRIGGER commerce.TR_TourIndexEpochs_BindingAndInactiveLifecycle_v1
ON commerce.TourIndexEpochs AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        IF EXISTS (SELECT 1 FROM inserted WHERE epoch_state <> ''Provisioned'')
            THROW 51078, ''TM70.A41a.InvalidTransition'', 1;
        RETURN;
    END;
    IF (SELECT COUNT_BIG(*) FROM inserted) <> (SELECT COUNT_BIG(*) FROM deleted)
        THROW 51077, ''TM70.A41a.ImmutableBinding'', 1;
    IF EXISTS (
        SELECT index_epoch,build_id,CONVERT(varbinary(512),collection_name),
            CONVERT(varbinary(512),model_version),CONVERT(varbinary(128),document_format_version),
            CONVERT(varbinary(128),policy_version),created_at_utc FROM inserted
        EXCEPT
        SELECT index_epoch,build_id,CONVERT(varbinary(512),collection_name),
            CONVERT(varbinary(512),model_version),CONVERT(varbinary(128),document_format_version),
            CONVERT(varbinary(128),policy_version),created_at_utc FROM deleted)
        OR EXISTS (
        SELECT index_epoch,build_id,CONVERT(varbinary(512),collection_name),
            CONVERT(varbinary(512),model_version),CONVERT(varbinary(128),document_format_version),
            CONVERT(varbinary(128),policy_version),created_at_utc FROM deleted
        EXCEPT
        SELECT index_epoch,build_id,CONVERT(varbinary(512),collection_name),
            CONVERT(varbinary(512),model_version),CONVERT(varbinary(128),document_format_version),
            CONVERT(varbinary(128),policy_version),created_at_utc FROM inserted)
        THROW 51077, ''TM70.A41a.ImmutableBinding'', 1;
    IF EXISTS (
        SELECT 1 FROM inserted i JOIN deleted d ON d.index_epoch=i.index_epoch
        WHERE NOT (
            (d.epoch_state=''Provisioned'' AND i.epoch_state IN (''Provisioned'',''Paused'',''Retired''))
            OR (d.epoch_state=''Paused'' AND i.epoch_state IN (''Paused'',''Retired''))
            OR (d.epoch_state=''Retired'' AND i.epoch_state=''Retired'')))
        THROW 51078, ''TM70.A41a.InvalidTransition'', 1;
END';

    DECLARE @checks table (constraint_name sysname PRIMARY KEY, predicate_text nvarchar(max), expected_definition nvarchar(max));
    INSERT @checks VALUES
    (N'CK_TourIndexEpochs_IndexEpoch', N'index_epoch <> CONVERT(uniqueidentifier,''00000000-0000-0000-0000-000000000000'')', N'([index_epoch]<>CONVERT([uniqueidentifier],''00000000-0000-0000-0000-000000000000''))'),
    (N'CK_TourIndexEpochs_BuildId', N'build_id <> CONVERT(uniqueidentifier,''00000000-0000-0000-0000-000000000000'')', N'([build_id]<>CONVERT([uniqueidentifier],''00000000-0000-0000-0000-000000000000''))'),
    (N'CK_TourIndexEpochs_Collection', N'DATALENGTH(collection_name)>0 AND DATALENGTH(collection_name)=DATALENGTH(LTRIM(RTRIM(collection_name)))', N'(datalength([collection_name])>(0) AND datalength([collection_name])=datalength(ltrim(rtrim([collection_name]))))'),
    (N'CK_TourIndexEpochs_Model', N'DATALENGTH(model_version)>0 AND DATALENGTH(model_version)=DATALENGTH(LTRIM(RTRIM(model_version)))', N'(datalength([model_version])>(0) AND datalength([model_version])=datalength(ltrim(rtrim([model_version]))))'),
    (N'CK_TourIndexEpochs_Document', N'DATALENGTH(document_format_version)>0 AND DATALENGTH(document_format_version)=DATALENGTH(LTRIM(RTRIM(document_format_version)))', N'(datalength([document_format_version])>(0) AND datalength([document_format_version])=datalength(ltrim(rtrim([document_format_version]))))'),
    (N'CK_TourIndexEpochs_Policy', N'DATALENGTH(policy_version)>0 AND DATALENGTH(policy_version)=DATALENGTH(LTRIM(RTRIM(policy_version)))', N'(datalength([policy_version])>(0) AND datalength([policy_version])=datalength(ltrim(rtrim([policy_version]))))'),
    (N'CK_TourIndexEpochs_InactiveState', N'(epoch_state=''Provisioned'' AND DATALENGTH(epoch_state)=11) OR (epoch_state=''Paused'' AND DATALENGTH(epoch_state)=6) OR (epoch_state=''Retired'' AND DATALENGTH(epoch_state)=7)', N'([epoch_state]=''Provisioned'' AND datalength([epoch_state])=(11) OR [epoch_state]=''Paused'' AND datalength([epoch_state])=(6) OR [epoch_state]=''Retired'' AND datalength([epoch_state])=(7))');

    DECLARE @tableId int=OBJECT_ID(N'commerce.TourIndexEpochs'),
        @sequenceId int=OBJECT_ID(N'commerce.TourIndexGenerationSequence'),
        @triggerId int=OBJECT_ID(N'commerce.TR_TourIndexEpochs_BindingAndInactiveLifecycle_v1');
    DECLARE @new bit=CASE WHEN @tableId IS NULL AND @sequenceId IS NULL AND @triggerId IS NULL THEN 1 ELSE 0 END;
    IF @new=1
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.objects o JOIN @checks e ON e.constraint_name=o.name WHERE o.schema_id=SCHEMA_ID(N'commerce'))
            OR EXISTS (SELECT 1 FROM sys.objects WHERE schema_id=SCHEMA_ID(N'commerce') AND name IN (N'PK_TourIndexEpochs',N'UQ_TourIndexEpochs_BuildId',N'UQ_TourIndexEpochs_Collection'))
            THROW 51075, 'TM70.A41a.IncompatibleSchema', 1;
        CREATE TABLE commerce.TourIndexEpochs (
            index_epoch uniqueidentifier NOT NULL,
            build_id uniqueidentifier NOT NULL,
            collection_name nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            model_version nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            document_format_version nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            policy_version nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            epoch_state varchar(24) COLLATE Latin1_General_100_BIN2 NOT NULL,
            created_at_utc datetime2(7) NOT NULL,
            row_version rowversion NOT NULL,
            CONSTRAINT PK_TourIndexEpochs PRIMARY KEY CLUSTERED (index_epoch ASC),
            CONSTRAINT UQ_TourIndexEpochs_BuildId UNIQUE NONCLUSTERED (build_id ASC),
            CONSTRAINT UQ_TourIndexEpochs_Collection UNIQUE NONCLUSTERED (collection_name ASC)
        );
        DECLARE @name sysname, @predicate nvarchar(max), @ddl nvarchar(max);
        DECLARE check_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT constraint_name,predicate_text FROM @checks ORDER BY constraint_name;
        OPEN check_cursor; FETCH NEXT FROM check_cursor INTO @name,@predicate;
        WHILE @@FETCH_STATUS=0
        BEGIN
            SET @ddl=N'ALTER TABLE commerce.TourIndexEpochs WITH CHECK ADD CONSTRAINT '+QUOTENAME(@name)+N' CHECK ('+@predicate+N');';
            EXEC sys.sp_executesql @ddl;
            FETCH NEXT FROM check_cursor INTO @name,@predicate;
        END;
        CLOSE check_cursor; DEALLOCATE check_cursor;
        CREATE SEQUENCE commerce.TourIndexGenerationSequence AS bigint
            START WITH 1 INCREMENT BY 1 MINVALUE 1 MAXVALUE 9223372036854775807 NO CYCLE NO CACHE;
        EXEC sys.sp_executesql @triggerBody;
        SELECT @tableId=OBJECT_ID(N'commerce.TourIndexEpochs'),@sequenceId=OBJECT_ID(N'commerce.TourIndexGenerationSequence'),
            @triggerId=OBJECT_ID(N'commerce.TR_TourIndexEpochs_BindingAndInactiveLifecycle_v1');
    END
    ELSE
    BEGIN
        IF (@tableId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id=@tableId))
            OR (@sequenceId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.sequences WHERE object_id=@sequenceId))
            OR (@triggerId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.triggers WHERE object_id=@triggerId AND parent_id=@tableId))
            THROW 51075, 'TM70.A41a.IncompatibleSchema', 1;
        IF @tableId IS NULL OR @sequenceId IS NULL OR @triggerId IS NULL
            THROW 51074, 'TM70.A41a.PartialSchema', 1;
        IF EXISTS (SELECT 1 FROM @checks e WHERE NOT EXISTS (SELECT 1 FROM sys.check_constraints c WHERE c.parent_object_id=@tableId AND c.name=e.constraint_name))
            OR (SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id=@tableId)<3
            THROW 51074, 'TM70.A41a.PartialSchema', 1;
    END;

    IF CONVERT(varbinary(256),SCHEMA_NAME((SELECT schema_id FROM sys.objects WHERE object_id=@tableId)))<>CONVERT(varbinary(256),N'commerce')
        OR EXISTS (SELECT 1 FROM (VALUES (@tableId,N'TourIndexEpochs'),(@sequenceId,N'TourIndexGenerationSequence'),
            (@triggerId,N'TR_TourIndexEpochs_BindingAndInactiveLifecycle_v1')) e(id,name)
            JOIN sys.objects o ON o.object_id=e.id
            WHERE CONVERT(varbinary(256),o.name)<>CONVERT(varbinary(256),e.name)
              OR CONVERT(varbinary(256),SCHEMA_NAME(o.schema_id))<>CONVERT(varbinary(256),N'commerce'))
        THROW 51075, 'TM70.A41a.IncompatibleSchema.Names', 1;
    DECLARE @columns table (id int PRIMARY KEY, name sysname, type_id int, bytes smallint, scale tinyint, collation_name sysname NULL);
    INSERT @columns VALUES (1,N'index_epoch',36,16,0,NULL),(2,N'build_id',36,16,0,NULL),
        (3,N'collection_name',231,512,0,N'Latin1_General_100_BIN2'),(4,N'model_version',231,512,0,N'Latin1_General_100_BIN2'),
        (5,N'document_format_version',231,128,0,N'Latin1_General_100_BIN2'),(6,N'policy_version',231,128,0,N'Latin1_General_100_BIN2'),
        (7,N'epoch_state',167,24,0,N'Latin1_General_100_BIN2'),(8,N'created_at_utc',42,8,7,NULL),(9,N'row_version',189,8,0,NULL);
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id=@tableId)<>9
        OR EXISTS (SELECT 1 FROM @columns e LEFT JOIN sys.columns c ON c.object_id=@tableId AND c.column_id=e.id
            WHERE c.column_id IS NULL OR CONVERT(varbinary(256),c.name)<>CONVERT(varbinary(256),e.name)
              OR c.system_type_id<>e.type_id OR c.user_type_id<>e.type_id OR c.max_length<>e.bytes OR c.scale<>e.scale
              OR (e.id=8 AND c.precision<>27) OR c.is_nullable<>0 OR c.is_identity<>0 OR c.is_computed<>0 OR c.default_object_id<>0 OR c.rule_object_id<>0
              OR ISNULL(c.collation_name,N'') COLLATE Latin1_General_100_BIN2<>ISNULL(e.collation_name,N'') COLLATE Latin1_General_100_BIN2)
        OR EXISTS (SELECT 1 FROM sys.tables WHERE object_id=@tableId AND (is_memory_optimized<>0 OR temporal_type<>0))
        OR EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=@tableId)
        THROW 51075, 'TM70.A41a.IncompatibleSchema.Columns', 1;

    DECLARE @keys table (name sysname PRIMARY KEY, column_name sysname, index_type int, key_type char(2));
    INSERT @keys VALUES (N'PK_TourIndexEpochs',N'index_epoch',1,'PK'),(N'UQ_TourIndexEpochs_BuildId',N'build_id',2,'UQ'),(N'UQ_TourIndexEpochs_Collection',N'collection_name',2,'UQ');
    IF (SELECT COUNT(*) FROM sys.indexes WHERE object_id=@tableId AND index_id>0)<>3
        OR (SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id=@tableId)<>3
        OR EXISTS (SELECT 1 FROM @keys e
            LEFT JOIN sys.key_constraints k ON k.parent_object_id=@tableId AND k.name=e.name
            LEFT JOIN sys.indexes i ON i.object_id=@tableId AND i.index_id=k.unique_index_id
            WHERE k.object_id IS NULL OR k.type COLLATE Latin1_General_100_BIN2<>e.key_type COLLATE Latin1_General_100_BIN2 OR CONVERT(varbinary(256),i.name)<>CONVERT(varbinary(256),e.name)
              OR i.type<>e.index_type OR i.is_unique<>1 OR i.is_disabled<>0 OR i.has_filter<>0 OR i.is_hypothetical<>0 OR i.ignore_dup_key<>0
              OR (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=@tableId AND ic.index_id=i.index_id)<>1
              OR NOT EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE ic.object_id=@tableId AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND ic.is_descending_key=0 AND ic.is_included_column=0 AND c.name=e.column_name))
        THROW 51075, 'TM70.A41a.IncompatibleSchema.Keys', 1;

    IF (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=@tableId)<>7
        OR EXISTS (SELECT 1 FROM @checks e LEFT JOIN sys.check_constraints c ON c.parent_object_id=@tableId AND c.name=e.constraint_name
            WHERE c.object_id IS NULL OR CONVERT(varbinary(256),c.name)<>CONVERT(varbinary(256),e.constraint_name) OR c.is_disabled<>0 OR c.is_not_trusted<>0 OR c.is_not_for_replication<>0
              OR CONVERT(varbinary(max),c.definition)<>CONVERT(varbinary(max),e.expected_definition))
    BEGIN
        DECLARE @checkDiagnostic nvarchar(2048)=N'TM70.A41a.IncompatibleSchema.Checks';
        -- Controlled schema expressions only: never source values or credentials.
        SELECT TOP(1) @checkDiagnostic=@checkDiagnostic+N':'+e.constraint_name+N':'+ISNULL(c.definition,N'<missing>')
        FROM @checks e LEFT JOIN sys.check_constraints c ON c.parent_object_id=@tableId AND c.name=e.constraint_name
        WHERE CONVERT(varbinary(max),c.definition)<>CONVERT(varbinary(max),e.expected_definition);
        THROW 51075,@checkDiagnostic,1;
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE object_id=@sequenceId AND system_type_id=127 AND user_type_id=127
        AND CONVERT(bigint,start_value)=1 AND CONVERT(bigint,minimum_value)=1 AND CONVERT(bigint,maximum_value)=9223372036854775807
        AND CONVERT(bigint,increment)=1 AND is_cycling=0 AND is_cached=0 AND cache_size IS NULL)
        THROW 51075, 'TM70.A41a.IncompatibleSchema.Sequence', 1;
    IF (SELECT COUNT(*) FROM sys.triggers WHERE parent_id=@tableId)<>1
        OR NOT EXISTS (SELECT 1 FROM sys.triggers t JOIN sys.sql_modules m ON m.object_id=t.object_id
            WHERE t.object_id=@triggerId AND t.parent_id=@tableId AND t.is_disabled=0 AND t.is_instead_of_trigger=0 AND t.is_not_for_replication=0
              AND m.uses_ansi_nulls=1 AND m.uses_quoted_identifier=1 AND m.execute_as_principal_id IS NULL AND m.definition IS NOT NULL
              AND CONVERT(varbinary(max),REPLACE(m.definition,CHAR(13)+CHAR(10),CHAR(10)))=CONVERT(varbinary(max),REPLACE(@triggerBody,CHAR(13)+CHAR(10),CHAR(10))))
        OR (SELECT COUNT(*) FROM sys.trigger_events WHERE object_id=@triggerId)<>2
        OR EXISTS (SELECT 1 FROM sys.trigger_events WHERE object_id=@triggerId AND type_desc NOT IN ('INSERT','UPDATE'))
        THROW 51075, 'TM70.A41a.IncompatibleSchema.Trigger', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
