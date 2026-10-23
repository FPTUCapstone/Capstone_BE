-- TM-79 8b: review media only. No provider execution or legacy changes.
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRY
 BEGIN TRANSACTION;
 IF OBJECT_ID(N'social.TripReviews',N'U') IS NULL
    THROW 51000, 'TM-79 media requires approved parent schema.',1;
 IF OBJECT_ID(N'social.TM79_ExpectedMediaOperations') IS NOT NULL OR OBJECT_ID(N'social.TM79_ExpectedMedia') IS NOT NULL
    THROW 51000, 'TM-79 validation objects already exist; investigate without repair.',1;
 IF (OBJECT_ID(N'social.TripReviewMediaOperations') IS NOT NULL AND OBJECT_ID(N'social.TripReviewMediaOperations',N'U') IS NULL)
 OR (OBJECT_ID(N'social.TripReviewMedia') IS NOT NULL AND OBJECT_ID(N'social.TripReviewMedia',N'U') IS NULL)
    THROW 51000, 'TM-79 media object has wrong type.',1;
 IF OBJECT_ID(N'social.TripReviewMediaOperations',N'U') IS NOT NULL
    AND COL_LENGTH(N'social.TripReviewMediaOperations',N'service_booking_id') IS NOT NULL
 BEGIN
    IF OBJECT_ID(N'social.TripReviewMedia',N'U') IS NULL
       OR COL_LENGTH(N'social.TripReviewMediaOperations',N'booking_id') IS NULL
        THROW 51000, 'TM-79 successor media shape is incomplete.',1;
    COMMIT TRANSACTION;
    RETURN;
 END;
 IF OBJECT_ID(N'social.TripReviewMediaOperations',N'U') IS NULL
 BEGIN
CREATE TABLE social.TripReviewMediaOperations (
 operation_id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_TripReviewMediaOperations PRIMARY KEY,
 batch_id UNIQUEIDENTIFIER NOT NULL,
 booking_id BIGINT NOT NULL CONSTRAINT FK_TripReviewMediaOperations_Booking REFERENCES commerce.Bookings(booking_id),
 traveler_user_id BIGINT NOT NULL CONSTRAINT FK_TripReviewMediaOperations_Traveler REFERENCES dbo.Users(user_id),
 sort_order TINYINT NOT NULL,
 public_id VARCHAR(255) COLLATE Latin1_General_100_BIN2 NOT NULL,
 state VARCHAR(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
 content_type VARCHAR(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
 extension VARCHAR(5) COLLATE Latin1_General_100_BIN2 NOT NULL,
 input_byte_length BIGINT NOT NULL,
 stored_byte_length BIGINT NULL,
 width INT NOT NULL, height INT NOT NULL,
 delivery_url NVARCHAR(2048) COLLATE Vietnamese_100_CI_AS NULL,
 created_at DATETIME2(7) NOT NULL, updated_at DATETIME2(7) NOT NULL,
 uploaded_at DATETIME2(7) NULL, adopted_at DATETIME2(7) NULL, cleaned_at DATETIME2(7) NULL,
 version ROWVERSION NOT NULL,
 CONSTRAINT CK_TripReviewMediaOperations_Slot CHECK(sort_order BETWEEN 0 AND 4),
 CONSTRAINT CK_TripReviewMediaOperations_PublicId CHECK(LEN(TRIM(CHAR(9)+CHAR(10)+CHAR(13)+CHAR(32) FROM public_id))>0),
 CONSTRAINT CK_TripReviewMediaOperations_Format CHECK(
  (content_type='image/jpeg' AND extension='.jpg') OR
  (content_type='image/png' AND extension='.png') OR
  (content_type='image/webp' AND extension='.webp')),
 CONSTRAINT CK_TripReviewMediaOperations_Bytes CHECK(input_byte_length BETWEEN 1 AND 5000000 AND (stored_byte_length IS NULL OR stored_byte_length>0)),
 CONSTRAINT CK_TripReviewMediaOperations_Pixels CHECK(width>0 AND height>0 AND CONVERT(BIGINT,width)*height<=24000000),
 CONSTRAINT CK_TripReviewMediaOperations_Url CHECK(delivery_url IS NULL OR LEN(TRIM(NCHAR(9)+NCHAR(10)+NCHAR(13)+NCHAR(32) FROM delivery_url))>0),
 CONSTRAINT CK_TripReviewMediaOperations_State CHECK(
  (state='Reserved' AND delivery_url IS NULL AND stored_byte_length IS NULL AND uploaded_at IS NULL AND adopted_at IS NULL AND cleaned_at IS NULL)
  OR (state='Uploaded' AND delivery_url IS NOT NULL AND stored_byte_length IS NOT NULL AND uploaded_at IS NOT NULL AND adopted_at IS NULL AND cleaned_at IS NULL)
  OR (state='Adopted' AND delivery_url IS NOT NULL AND stored_byte_length IS NOT NULL AND uploaded_at IS NOT NULL AND adopted_at IS NOT NULL AND cleaned_at IS NULL)
  OR (state IN ('CleanupPending','Cleaned') AND adopted_at IS NULL
    AND ((delivery_url IS NULL AND stored_byte_length IS NULL AND uploaded_at IS NULL)
      OR (delivery_url IS NOT NULL AND stored_byte_length IS NOT NULL AND uploaded_at IS NOT NULL))
    AND ((state='CleanupPending' AND cleaned_at IS NULL) OR (state='Cleaned' AND cleaned_at IS NOT NULL))))
);
CREATE UNIQUE INDEX UX_TripReviewMediaOperations_PublicId ON social.TripReviewMediaOperations(public_id);
CREATE UNIQUE INDEX UX_TripReviewMediaOperations_BatchSlot ON social.TripReviewMediaOperations(batch_id,sort_order);
CREATE INDEX IX_TripReviewMediaOperations_Recovery ON social.TripReviewMediaOperations(state,updated_at,operation_id);

 END;
 IF OBJECT_ID(N'social.TripReviewMedia',N'U') IS NULL
 BEGIN
CREATE TABLE social.TripReviewMedia (
 media_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TripReviewMedia PRIMARY KEY,
 trip_review_id BIGINT NOT NULL CONSTRAINT FK_TripReviewMedia_Review REFERENCES social.TripReviews(trip_review_id),
 operation_id UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_TripReviewMedia_Operation REFERENCES social.TripReviewMediaOperations(operation_id),
 sort_order TINYINT NOT NULL CONSTRAINT CK_TripReviewMedia_Slot CHECK(sort_order BETWEEN 0 AND 4),
 created_at DATETIME2(7) NOT NULL
);
CREATE UNIQUE INDEX UX_TripReviewMedia_Operation ON social.TripReviewMedia(operation_id);
CREATE UNIQUE INDEX UX_TripReviewMedia_ReviewSlot ON social.TripReviewMedia(trip_review_id,sort_order);
 END;
CREATE TABLE social.TM79_ExpectedMediaOperations (
 operation_id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_TM79_ExpectedMediaOperations PRIMARY KEY,
 batch_id UNIQUEIDENTIFIER NOT NULL,
 booking_id BIGINT NOT NULL CONSTRAINT FK_TM79_ExpectedMediaOperations_Booking REFERENCES commerce.Bookings(booking_id),
 traveler_user_id BIGINT NOT NULL CONSTRAINT FK_TM79_ExpectedMediaOperations_Traveler REFERENCES dbo.Users(user_id),
 sort_order TINYINT NOT NULL,
 public_id VARCHAR(255) COLLATE Latin1_General_100_BIN2 NOT NULL,
 state VARCHAR(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
 content_type VARCHAR(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
 extension VARCHAR(5) COLLATE Latin1_General_100_BIN2 NOT NULL,
 input_byte_length BIGINT NOT NULL,
 stored_byte_length BIGINT NULL,
 width INT NOT NULL, height INT NOT NULL,
 delivery_url NVARCHAR(2048) COLLATE Vietnamese_100_CI_AS NULL,
 created_at DATETIME2(7) NOT NULL, updated_at DATETIME2(7) NOT NULL,
 uploaded_at DATETIME2(7) NULL, adopted_at DATETIME2(7) NULL, cleaned_at DATETIME2(7) NULL,
 version ROWVERSION NOT NULL,
 CONSTRAINT CK_TM79_ExpectedMediaOperations_Slot CHECK(sort_order BETWEEN 0 AND 4),
 CONSTRAINT CK_TM79_ExpectedMediaOperations_PublicId CHECK(LEN(TRIM(CHAR(9)+CHAR(10)+CHAR(13)+CHAR(32) FROM public_id))>0),
 CONSTRAINT CK_TM79_ExpectedMediaOperations_Format CHECK(
  (content_type='image/jpeg' AND extension='.jpg') OR
  (content_type='image/png' AND extension='.png') OR
  (content_type='image/webp' AND extension='.webp')),
 CONSTRAINT CK_TM79_ExpectedMediaOperations_Bytes CHECK(input_byte_length BETWEEN 1 AND 5000000 AND (stored_byte_length IS NULL OR stored_byte_length>0)),
 CONSTRAINT CK_TM79_ExpectedMediaOperations_Pixels CHECK(width>0 AND height>0 AND CONVERT(BIGINT,width)*height<=24000000),
 CONSTRAINT CK_TM79_ExpectedMediaOperations_Url CHECK(delivery_url IS NULL OR LEN(TRIM(NCHAR(9)+NCHAR(10)+NCHAR(13)+NCHAR(32) FROM delivery_url))>0),
 CONSTRAINT CK_TM79_ExpectedMediaOperations_State CHECK(
  (state='Reserved' AND delivery_url IS NULL AND stored_byte_length IS NULL AND uploaded_at IS NULL AND adopted_at IS NULL AND cleaned_at IS NULL)
  OR (state='Uploaded' AND delivery_url IS NOT NULL AND stored_byte_length IS NOT NULL AND uploaded_at IS NOT NULL AND adopted_at IS NULL AND cleaned_at IS NULL)
  OR (state='Adopted' AND delivery_url IS NOT NULL AND stored_byte_length IS NOT NULL AND uploaded_at IS NOT NULL AND adopted_at IS NOT NULL AND cleaned_at IS NULL)
  OR (state IN ('CleanupPending','Cleaned') AND adopted_at IS NULL
    AND ((delivery_url IS NULL AND stored_byte_length IS NULL AND uploaded_at IS NULL)
      OR (delivery_url IS NOT NULL AND stored_byte_length IS NOT NULL AND uploaded_at IS NOT NULL))
    AND ((state='CleanupPending' AND cleaned_at IS NULL) OR (state='Cleaned' AND cleaned_at IS NOT NULL))))
);
CREATE UNIQUE INDEX UX_TM79_ExpectedMediaOperations_PublicId ON social.TM79_ExpectedMediaOperations(public_id);
CREATE UNIQUE INDEX UX_TM79_ExpectedMediaOperations_BatchSlot ON social.TM79_ExpectedMediaOperations(batch_id,sort_order);
CREATE INDEX IX_TM79_ExpectedMediaOperations_Recovery ON social.TM79_ExpectedMediaOperations(state,updated_at,operation_id);
CREATE TABLE social.TM79_ExpectedMedia (
 media_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TM79_ExpectedMedia PRIMARY KEY,
 trip_review_id BIGINT NOT NULL CONSTRAINT FK_TM79_ExpectedMedia_Review REFERENCES social.TripReviews(trip_review_id),
 operation_id UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_TM79_ExpectedMedia_Operation REFERENCES social.TM79_ExpectedMediaOperations(operation_id),
 sort_order TINYINT NOT NULL CONSTRAINT CK_TM79_ExpectedMedia_Slot CHECK(sort_order BETWEEN 0 AND 4),
 created_at DATETIME2(7) NOT NULL
);
CREATE UNIQUE INDEX UX_TM79_ExpectedMedia_Operation ON social.TM79_ExpectedMedia(operation_id);
CREATE UNIQUE INDEX UX_TM79_ExpectedMedia_ReviewSlot ON social.TM79_ExpectedMedia(trip_review_id,sort_order);
 DECLARE @inventory TABLE(side int,item nvarchar(max) COLLATE Latin1_General_100_BIN2);
 DECLARE @side int=0, @target int;
 WHILE @side<4
 BEGIN
 SET @target=CASE @side WHEN 0 THEN OBJECT_ID(N'social.TripReviewMediaOperations')
 WHEN 1 THEN OBJECT_ID(N'social.TM79_ExpectedMediaOperations')
 WHEN 2 THEN OBJECT_ID(N'social.TripReviewMedia')
 ELSE OBJECT_ID(N'social.TM79_ExpectedMedia') END;
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
            INSERT @inventory(side,item) SELECT @side, REPLACE(REPLACE(item,N'TM79_ExpectedMediaOperations',N'TripReviewMediaOperations'),N'TM79_ExpectedMedia',N'TripReviewMedia') FROM Inventory;
 INSERT @inventory(side,item)
 SELECT @side,CONCAT(N'IDENTITY|',name,N'|',CONVERT(nvarchar(40),seed_value),N'|',CONVERT(nvarchar(40),increment_value))
 FROM sys.identity_columns WHERE object_id=@target;
 SET @side+=1;
 END;
 IF EXISTS(SELECT item FROM @inventory WHERE side IN (0,2) EXCEPT SELECT item FROM @inventory WHERE side IN (1,3))
 OR EXISTS(SELECT item FROM @inventory WHERE side IN (1,3) EXCEPT SELECT item FROM @inventory WHERE side IN (0,2))
 OR (SELECT COUNT(*) FROM @inventory WHERE side IN(0,2))<>(SELECT COUNT(*) FROM @inventory WHERE side IN(1,3))
    THROW 51000, 'TM-79 media inventory mismatch; no automatic repair.',1;
 DROP TABLE social.TM79_ExpectedMedia;
 DROP TABLE social.TM79_ExpectedMediaOperations;
 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
