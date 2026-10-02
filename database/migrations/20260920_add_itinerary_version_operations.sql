SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'planning.ItineraryVersionOperations', N'U') IS NULL
BEGIN
    CREATE TABLE planning.ItineraryVersionOperations (
        operation_id             BIGINT IDENTITY(1,1) NOT NULL,
        traveler_user_id         BIGINT NOT NULL,
        source_itinerary_id      BIGINT NOT NULL,
        operation_type           VARCHAR(20) NOT NULL,
        idempotency_key          UNIQUEIDENTIFIER NOT NULL,
        request_hash             VARCHAR(128) NOT NULL,
        result_itinerary_id      BIGINT NULL,
        created_at               DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ItineraryVersionOperations PRIMARY KEY (operation_id),
        CONSTRAINT CK_ItineraryVersionOperations_Type
            CHECK (operation_type IN ('Regenerate','Adjust')),
        CONSTRAINT UQ_ItineraryVersionOperations_TravelerKey
            UNIQUE (traveler_user_id, idempotency_key)
    );
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_key_columns AS fkc
    WHERE fkc.parent_object_id = OBJECT_ID(N'planning.ItineraryVersionOperations')
      AND fkc.parent_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'planning.ItineraryVersionOperations'), N'traveler_user_id', 'ColumnId')
      AND fkc.referenced_object_id = OBJECT_ID(N'dbo.Users')
      AND fkc.referenced_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.Users'), N'user_id', 'ColumnId'))
BEGIN
    ALTER TABLE planning.ItineraryVersionOperations
    ADD CONSTRAINT FK_ItineraryVersionOperations_Traveler
        FOREIGN KEY (traveler_user_id) REFERENCES dbo.Users(user_id);
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_key_columns AS fkc
    WHERE fkc.parent_object_id = OBJECT_ID(N'planning.ItineraryVersionOperations')
      AND fkc.parent_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'planning.ItineraryVersionOperations'), N'source_itinerary_id', 'ColumnId')
      AND fkc.referenced_object_id = OBJECT_ID(N'planning.Itineraries')
      AND fkc.referenced_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'planning.Itineraries'), N'itinerary_id', 'ColumnId'))
BEGIN
    ALTER TABLE planning.ItineraryVersionOperations
    ADD CONSTRAINT FK_ItineraryVersionOperations_Source
        FOREIGN KEY (source_itinerary_id) REFERENCES planning.Itineraries(itinerary_id);
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_key_columns AS fkc
    WHERE fkc.parent_object_id = OBJECT_ID(N'planning.ItineraryVersionOperations')
      AND fkc.parent_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'planning.ItineraryVersionOperations'), N'result_itinerary_id', 'ColumnId')
      AND fkc.referenced_object_id = OBJECT_ID(N'planning.Itineraries')
      AND fkc.referenced_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'planning.Itineraries'), N'itinerary_id', 'ColumnId'))
BEGIN
    ALTER TABLE planning.ItineraryVersionOperations
    ADD CONSTRAINT FK_ItineraryVersionOperations_Result
        FOREIGN KEY (result_itinerary_id) REFERENCES planning.Itineraries(itinerary_id);
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'planning.ItineraryVersionOperations')
      AND name = N'CK_ItineraryVersionOperations_Type')
BEGIN
    ALTER TABLE planning.ItineraryVersionOperations
    ADD CONSTRAINT CK_ItineraryVersionOperations_Type
        CHECK (operation_type IN ('Regenerate','Adjust'));
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes AS i
    WHERE i.object_id = OBJECT_ID(N'planning.ItineraryVersionOperations')
      AND i.name = N'UQ_ItineraryVersionOperations_TravelerKey')
BEGIN
    ALTER TABLE planning.ItineraryVersionOperations
    ADD CONSTRAINT UQ_ItineraryVersionOperations_TravelerKey
        UNIQUE (traveler_user_id, idempotency_key);
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes AS i
    WHERE i.object_id = OBJECT_ID(N'planning.ItineraryVersionOperations')
      AND i.name = N'IX_ItineraryVersionOperations_Source')
BEGIN
    CREATE INDEX IX_ItineraryVersionOperations_Source
        ON planning.ItineraryVersionOperations(source_itinerary_id);
END;

COMMIT TRANSACTION;
