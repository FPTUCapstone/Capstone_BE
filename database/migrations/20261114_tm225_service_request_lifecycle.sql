-- TM-225 UC78-80. Nullable request_submitted_at opts new requests into lifecycle processing.
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'commercial.ServiceBookings', N'U') IS NULL
        OR OBJECT_ID(N'dbo.Users', N'U') IS NULL OR OBJECT_ID(N'planning.Itineraries', N'U') IS NULL
        THROW 51004, 'TM-225 requires ServiceBookings, Users and Itineraries.', 1;

    IF COL_LENGTH(N'commercial.ServiceBookings', N'request_submitted_at') IS NULL
        ALTER TABLE commercial.ServiceBookings ADD request_submitted_at DATETIME2 NULL;
    IF COL_LENGTH(N'commercial.ServiceBookings', N'actual_amount') IS NULL
        ALTER TABLE commercial.ServiceBookings ADD actual_amount DECIMAL(12,2) NULL;
    IF COL_LENGTH(N'commercial.ServiceBookings', N'rejection_reason') IS NULL
        ALTER TABLE commercial.ServiceBookings ADD rejection_reason NVARCHAR(1000) NULL;
    IF COL_LENGTH(N'commercial.ServiceBookings', N'responded_by_user_id') IS NULL
        ALTER TABLE commercial.ServiceBookings ADD responded_by_user_id BIGINT NULL;
    IF COL_LENGTH(N'commercial.ServiceBookings', N'responded_at') IS NULL
        ALTER TABLE commercial.ServiceBookings ADD responded_at DATETIME2 NULL;
    IF COL_LENGTH(N'commercial.ServiceBookings', N'outcome_by_user_id') IS NULL
        ALTER TABLE commercial.ServiceBookings ADD outcome_by_user_id BIGINT NULL;
    IF COL_LENGTH(N'commercial.ServiceBookings', N'outcome_at') IS NULL
        ALTER TABLE commercial.ServiceBookings ADD outcome_at DATETIME2 NULL;
    IF COL_LENGTH(N'commercial.ServiceBookings', N'is_auto_completed') IS NULL
        ALTER TABLE commercial.ServiceBookings ADD is_auto_completed BIT NOT NULL CONSTRAINT DF_ServiceBookings_AutoCompleted DEFAULT (0) WITH VALUES;
    IF COL_LENGTH(N'commercial.ServiceBookings', N'row_version') IS NULL
        ALTER TABLE commercial.ServiceBookings ADD row_version ROWVERSION;
    IF COL_LENGTH(N'commercial.ServiceBookings', N'itinerary_id') IS NULL
        ALTER TABLE commercial.ServiceBookings ADD itinerary_id BIGINT NULL;
    IF COL_LENGTH(N'commercial.ServiceBookings', N'service_option') IS NULL
        ALTER TABLE commercial.ServiceBookings ADD service_option NVARCHAR(200) NULL;
    IF COL_LENGTH(N'commercial.ServiceBookings', N'special_request') IS NULL
        ALTER TABLE commercial.ServiceBookings ADD special_request NVARCHAR(1000) NULL;

    DECLARE @columns TABLE (column_name SYSNAME, type_id INT, max_length SMALLINT,
        nullable BIT, expected_precision TINYINT NULL, expected_scale TINYINT NULL);

    DELETE FROM @columns;
    INSERT @columns VALUES
        (N'request_submitted_at', TYPE_ID(N'datetime2'), 8, 1, NULL, 7),
        (N'actual_amount', TYPE_ID(N'decimal'), 9, 1, 12, 2),
        (N'rejection_reason', TYPE_ID(N'nvarchar'), 2000, 1, NULL, NULL),
        (N'responded_by_user_id', TYPE_ID(N'bigint'), 8, 1, NULL, NULL),
        (N'responded_at', TYPE_ID(N'datetime2'), 8, 1, NULL, 7),
        (N'outcome_by_user_id', TYPE_ID(N'bigint'), 8, 1, NULL, NULL),
        (N'outcome_at', TYPE_ID(N'datetime2'), 8, 1, NULL, 7),
        (N'is_auto_completed', TYPE_ID(N'bit'), 1, 0, NULL, NULL),
        (N'row_version', TYPE_ID(N'timestamp'), 8, 0, NULL, NULL),
        (N'itinerary_id', TYPE_ID(N'bigint'), 8, 1, NULL, NULL),
        (N'service_option', TYPE_ID(N'nvarchar'), 400, 1, NULL, NULL),
        (N'special_request', TYPE_ID(N'nvarchar'), 2000, 1, NULL, NULL);
    IF EXISTS (SELECT 1 FROM @columns AS e WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns AS c WHERE c.object_id = OBJECT_ID(N'commercial.ServiceBookings') AND c.name = e.column_name
            AND c.system_type_id = e.type_id AND c.max_length = e.max_length
            AND c.is_nullable = e.nullable AND c.is_computed = 0 AND c.is_identity = 0
            AND (e.expected_precision IS NULL OR c.precision = e.expected_precision)
            AND (e.expected_scale IS NULL OR c.scale = e.expected_scale)))
        THROW 51004, 'commercial.ServiceBookings lifecycle columns have an incompatible shape.', 1;

    IF NOT EXISTS (SELECT 1 FROM sys.default_constraints AS d JOIN sys.columns AS c
        ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
        WHERE d.parent_object_id = OBJECT_ID(N'commercial.ServiceBookings') AND c.name = N'is_auto_completed')
    BEGIN
        IF OBJECT_ID(N'commercial.DF_ServiceBookings_AutoCompleted') IS NOT NULL
            THROW 51004, 'DF_ServiceBookings_AutoCompleted has an incompatible owner.', 1;
        EXEC(N'ALTER TABLE commercial.ServiceBookings ADD CONSTRAINT DF_ServiceBookings_AutoCompleted DEFAULT (0) FOR is_auto_completed;');
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.default_constraints AS d JOIN sys.columns AS c
        ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
        WHERE d.parent_object_id = OBJECT_ID(N'commercial.ServiceBookings') AND c.name = N'is_auto_completed'
            AND REPLACE(REPLACE(REPLACE(d.definition, N'(', N''), N')', N''), N' ', N'') = N'0')
        THROW 51004, 'ServiceBookings.is_auto_completed requires a zero default.', 1;
    IF OBJECT_ID(N'commercial.DF_ServiceBookings_AutoCompleted') IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM sys.default_constraints AS d JOIN sys.columns AS c
            ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
        WHERE d.object_id = OBJECT_ID(N'commercial.DF_ServiceBookings_AutoCompleted')
            AND d.parent_object_id = OBJECT_ID(N'commercial.ServiceBookings') AND c.name = N'is_auto_completed')
        THROW 51004, 'DF_ServiceBookings_AutoCompleted has an incompatible owner.', 1;
    IF EXISTS (SELECT 1 FROM sys.default_constraints AS d JOIN sys.columns AS c
        ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
        WHERE d.parent_object_id = OBJECT_ID(N'commercial.ServiceBookings')
            AND c.name IN (SELECT column_name FROM @columns WHERE column_name <> N'is_auto_completed'))
        THROW 51004, 'ServiceBookings nullable lifecycle columns must not have defaults.', 1;

    -- Classify only status-only checks, preserving every unrelated constraint.
    DECLARE @bookingsId INT = OBJECT_ID(N'commercial.ServiceBookings');
    DECLARE @statusColumn INT = COLUMNPROPERTY(@bookingsId, N'status', 'ColumnId');
    DECLARE @statusChecks TABLE (constraint_name SYSNAME, mixes_columns BIT);
    INSERT @statusChecks
    SELECT cc.name, CONVERT(BIT, CASE WHEN EXISTS (
        SELECT 1 FROM sys.sql_expression_dependencies AS other_dependency
        WHERE other_dependency.referencing_id = cc.object_id AND other_dependency.referenced_id = @bookingsId
            AND other_dependency.referenced_minor_id > 0 AND other_dependency.referenced_minor_id <> @statusColumn)
        THEN 1 ELSE 0 END)
    FROM sys.check_constraints AS cc
    WHERE cc.parent_object_id = @bookingsId AND (cc.parent_column_id = @statusColumn OR EXISTS (
        SELECT 1 FROM sys.sql_expression_dependencies AS dependency WHERE dependency.referencing_id = cc.object_id
            AND dependency.referenced_id = @bookingsId AND dependency.referenced_minor_id = @statusColumn));
    IF (SELECT COUNT(*) FROM @statusChecks) > 1 OR EXISTS (SELECT 1 FROM @statusChecks WHERE mixes_columns = 1)
        THROW 51004, 'ServiceBookings status CHECK constraints cannot be classified safely.', 1;
    IF OBJECT_ID(N'commercial.CK_ServiceBookings_RequestStatus') IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM sys.check_constraints WHERE object_id = OBJECT_ID(N'commercial.CK_ServiceBookings_RequestStatus')
            AND parent_object_id = @bookingsId AND name IN (SELECT constraint_name FROM @statusChecks))
        THROW 51004, 'CK_ServiceBookings_RequestStatus has an incompatible owner.', 1;
    IF EXISTS (SELECT 1 FROM commercial.ServiceBookings WHERE status NOT IN
        ('Pending','Confirmed','Cancelled','Completed','PendingConfirmation','Rejected','Expired','NoShow'))
        THROW 51004, 'ServiceBookings contains an unsupported status. Resolve it before upgrading.', 1;
    DECLARE @oldStatusCheck SYSNAME = (SELECT constraint_name FROM @statusChecks);
    IF @oldStatusCheck IS NOT NULL
    BEGIN
        DECLARE @dropStatusCheck NVARCHAR(512) = N'ALTER TABLE commercial.ServiceBookings DROP CONSTRAINT ' + QUOTENAME(@oldStatusCheck);
        EXEC sys.sp_executesql @dropStatusCheck;
    END;
    ALTER TABLE commercial.ServiceBookings ALTER COLUMN status VARCHAR(24) NOT NULL;
    ALTER TABLE commercial.ServiceBookings WITH CHECK ADD CONSTRAINT CK_ServiceBookings_RequestStatus
        CHECK (status IN ('Pending','Confirmed','Cancelled','Completed','PendingConfirmation','Rejected','Expired','NoShow'));

    IF OBJECT_ID(N'commercial.ServiceProviderRequestOperations') IS NOT NULL
        AND OBJECT_ID(N'commercial.ServiceProviderRequestOperations', N'U') IS NULL
        THROW 51004, 'ServiceProviderRequestOperations is not a table.', 1;
    IF OBJECT_ID(N'commercial.ServiceProviderRequestOperations', N'U') IS NULL
        CREATE TABLE commercial.ServiceProviderRequestOperations (
            operation_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ServiceProviderRequestOperations PRIMARY KEY,
            service_booking_id BIGINT NOT NULL,
            actor_user_id BIGINT NOT NULL,
            idempotency_key UNIQUEIDENTIFIER NOT NULL,
            operation_type VARCHAR(10) NOT NULL,
            request_hash CHAR(64) NOT NULL,
            response_json NVARCHAR(MAX) NOT NULL,
            created_at DATETIME2 NOT NULL
        );

    DELETE FROM @columns;
    INSERT @columns VALUES
        (N'service_booking_id', TYPE_ID(N'bigint'), 8, 0, NULL, NULL),
        (N'actor_user_id', TYPE_ID(N'bigint'), 8, 0, NULL, NULL),
        (N'idempotency_key', TYPE_ID(N'uniqueidentifier'), 16, 0, NULL, NULL),
        (N'operation_type', TYPE_ID(N'varchar'), 10, 0, NULL, NULL),
        (N'request_hash', TYPE_ID(N'char'), 64, 0, NULL, NULL),
        (N'response_json', TYPE_ID(N'nvarchar'), -1, 0, NULL, NULL),
        (N'created_at', TYPE_ID(N'datetime2'), 8, 0, NULL, 7);
    IF EXISTS (SELECT 1 FROM @columns AS e WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns AS c WHERE c.object_id = OBJECT_ID(N'commercial.ServiceProviderRequestOperations') AND c.name = e.column_name
            AND c.system_type_id = e.type_id AND c.max_length = e.max_length
            AND c.is_nullable = e.nullable AND c.is_computed = 0 AND c.is_identity = 0
            AND (e.expected_precision IS NULL OR c.precision = e.expected_precision)
            AND (e.expected_scale IS NULL OR c.scale = e.expected_scale)))
        THROW 51004, 'commercial.ServiceProviderRequestOperations lifecycle columns have an incompatible shape.', 1;

    DECLARE @operationsId INT = OBJECT_ID(N'commercial.ServiceProviderRequestOperations');
    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = @operationsId) <> 8
        OR NOT EXISTS (SELECT 1 FROM sys.identity_columns WHERE object_id = @operationsId AND name = N'operation_id'
            AND system_type_id = TYPE_ID(N'bigint') AND is_nullable = 0 AND seed_value = 1 AND increment_value = 1)
        THROW 51004, 'ServiceProviderRequestOperations has an incompatible shape.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints AS k WHERE k.parent_object_id = @operationsId
        AND k.type = N'PK' AND k.name = N'PK_ServiceProviderRequestOperations' AND (SELECT COUNT(*) FROM sys.index_columns AS ic
            WHERE ic.object_id = @operationsId AND ic.index_id = k.unique_index_id AND ic.key_ordinal > 0) = 1
        AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = @operationsId AND ic.index_id = k.unique_index_id
                AND ic.key_ordinal = 1 AND c.name = N'operation_id'))
        THROW 51004, 'ServiceProviderRequestOperations requires an operation_id primary key.', 1;

    -- Dynamic DDL avoids binding newly added columns before SQL Server executes their ADD.
    IF OBJECT_ID(N'commercial.FK_ServiceBookings_RespondedBy') IS NULL
        EXEC(N'ALTER TABLE commercial.ServiceBookings WITH CHECK ADD CONSTRAINT FK_ServiceBookings_RespondedBy FOREIGN KEY(responded_by_user_id) REFERENCES dbo.Users(user_id);');
    IF OBJECT_ID(N'commercial.FK_ServiceBookings_OutcomeBy') IS NULL
        EXEC(N'ALTER TABLE commercial.ServiceBookings WITH CHECK ADD CONSTRAINT FK_ServiceBookings_OutcomeBy FOREIGN KEY(outcome_by_user_id) REFERENCES dbo.Users(user_id);');
    IF OBJECT_ID(N'commercial.FK_ServiceBookings_Itinerary') IS NULL
        EXEC(N'ALTER TABLE commercial.ServiceBookings WITH CHECK ADD CONSTRAINT FK_ServiceBookings_Itinerary FOREIGN KEY(itinerary_id) REFERENCES planning.Itineraries(itinerary_id);');
    IF OBJECT_ID(N'commercial.FK_ServiceProviderRequestOperations_Booking') IS NULL
        EXEC(N'ALTER TABLE commercial.ServiceProviderRequestOperations WITH CHECK ADD CONSTRAINT FK_ServiceProviderRequestOperations_Booking FOREIGN KEY(service_booking_id) REFERENCES commercial.ServiceBookings(service_booking_id);');
    IF OBJECT_ID(N'commercial.FK_ServiceProviderRequestOperations_Actor') IS NULL
        EXEC(N'ALTER TABLE commercial.ServiceProviderRequestOperations WITH CHECK ADD CONSTRAINT FK_ServiceProviderRequestOperations_Actor FOREIGN KEY(actor_user_id) REFERENCES dbo.Users(user_id);');

    DECLARE @foreignKeys TABLE (constraint_name SYSNAME, table_id INT, column_name SYSNAME,
        referenced_id INT, referenced_column SYSNAME);
    INSERT @foreignKeys VALUES
        (N'FK_ServiceBookings_RespondedBy', OBJECT_ID(N'commercial.ServiceBookings'), N'responded_by_user_id', OBJECT_ID(N'dbo.Users'), N'user_id'),
        (N'FK_ServiceBookings_OutcomeBy', OBJECT_ID(N'commercial.ServiceBookings'), N'outcome_by_user_id', OBJECT_ID(N'dbo.Users'), N'user_id'),
        (N'FK_ServiceBookings_Itinerary', OBJECT_ID(N'commercial.ServiceBookings'), N'itinerary_id', OBJECT_ID(N'planning.Itineraries'), N'itinerary_id'),
        (N'FK_ServiceProviderRequestOperations_Booking', OBJECT_ID(N'commercial.ServiceProviderRequestOperations'), N'service_booking_id', OBJECT_ID(N'commercial.ServiceBookings'), N'service_booking_id'),
        (N'FK_ServiceProviderRequestOperations_Actor', OBJECT_ID(N'commercial.ServiceProviderRequestOperations'), N'actor_user_id', OBJECT_ID(N'dbo.Users'), N'user_id');
    IF EXISTS (SELECT 1 FROM @foreignKeys AS expected WHERE NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys AS fk WHERE fk.parent_object_id = expected.table_id
            AND fk.name = expected.constraint_name AND fk.referenced_object_id = expected.referenced_id
            AND fk.is_disabled = 0 AND fk.is_not_trusted = 0
            AND fk.delete_referential_action = 0 AND fk.update_referential_action = 0
            AND (SELECT COUNT(*) FROM sys.foreign_key_columns AS fc WHERE fc.constraint_object_id = fk.object_id) = 1
            AND EXISTS (SELECT 1 FROM sys.foreign_key_columns AS fc
                JOIN sys.columns AS source ON source.object_id = fc.parent_object_id AND source.column_id = fc.parent_column_id
                JOIN sys.columns AS target ON target.object_id = fc.referenced_object_id AND target.column_id = fc.referenced_column_id
                WHERE fc.constraint_object_id = fk.object_id AND source.name = expected.column_name
                    AND target.name = expected.referenced_column)))
        THROW 51004, 'Service request lifecycle foreign keys have an incompatible shape.', 1;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = @operationsId AND name = N'UX_ServiceProviderRequestOperations_ActorKey')
        CREATE UNIQUE INDEX UX_ServiceProviderRequestOperations_ActorKey
            ON commercial.ServiceProviderRequestOperations(actor_user_id, idempotency_key);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = @bookingsId AND name = N'IX_ServiceBookings_RequestLifecycle')
        EXEC(N'CREATE INDEX IX_ServiceBookings_RequestLifecycle ON commercial.ServiceBookings(status, request_submitted_at, start_datetime)
            WHERE request_submitted_at IS NOT NULL;');
    IF NOT EXISTS (SELECT 1 FROM sys.indexes AS i WHERE i.object_id = OBJECT_ID(N'commercial.ServiceProviderRequestOperations')
        AND i.name = N'UX_ServiceProviderRequestOperations_ActorKey' AND i.is_unique = 1 AND i.is_disabled = 0
        AND i.has_filter = 0
        AND (SELECT COUNT(*) FROM sys.index_columns WHERE object_id = i.object_id AND index_id = i.index_id) = 2
        AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 AND c.name = N'actor_user_id' AND ic.is_descending_key = 0)
        AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 2 AND c.name = N'idempotency_key' AND ic.is_descending_key = 0))
        THROW 51004, 'UX_ServiceProviderRequestOperations_ActorKey has an incompatible shape.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes AS i WHERE i.object_id = OBJECT_ID(N'commercial.ServiceBookings')
        AND i.name = N'IX_ServiceBookings_RequestLifecycle' AND i.is_unique = 0 AND i.is_disabled = 0
        AND REPLACE(REPLACE(REPLACE(i.filter_definition, N'(', N''), N')', N''), N' ', N'') = N'[request_submitted_at]ISNOTNULL'
        AND (SELECT COUNT(*) FROM sys.index_columns WHERE object_id = i.object_id AND index_id = i.index_id) = 3
        AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 AND c.name = N'status' AND ic.is_descending_key = 0)
        AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 2 AND c.name = N'request_submitted_at' AND ic.is_descending_key = 0)
        AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 3 AND c.name = N'start_datetime' AND ic.is_descending_key = 0))
        THROW 51004, 'IX_ServiceBookings_RequestLifecycle has an incompatible shape.', 1;

    IF OBJECT_ID(N'commercial.CK_ServiceProviderRequestOperations_Type') IS NOT NULL
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE object_id = OBJECT_ID(N'commercial.CK_ServiceProviderRequestOperations_Type')
            AND parent_object_id = @operationsId)
            THROW 51004, 'CK_ServiceProviderRequestOperations_Type has an incompatible owner.', 1;
        ALTER TABLE commercial.ServiceProviderRequestOperations DROP CONSTRAINT CK_ServiceProviderRequestOperations_Type;
    END;
    ALTER TABLE commercial.ServiceProviderRequestOperations WITH CHECK ADD CONSTRAINT CK_ServiceProviderRequestOperations_Type
        CHECK (operation_type IN ('Decision','Outcome'));

    IF OBJECT_ID(N'commercial.CK_ServiceProviderRequestOperations_Response') IS NOT NULL
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE object_id = OBJECT_ID(N'commercial.CK_ServiceProviderRequestOperations_Response')
            AND parent_object_id = @operationsId)
            THROW 51004, 'CK_ServiceProviderRequestOperations_Response has an incompatible owner.', 1;
        ALTER TABLE commercial.ServiceProviderRequestOperations DROP CONSTRAINT CK_ServiceProviderRequestOperations_Response;
    END;
    ALTER TABLE commercial.ServiceProviderRequestOperations WITH CHECK ADD CONSTRAINT CK_ServiceProviderRequestOperations_Response
        CHECK (ISJSON(response_json) = 1);
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
