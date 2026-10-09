-- TM-79 8c: additive operational recovery only; never infer absent uploads.
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRY
 BEGIN TRANSACTION;
 IF OBJECT_ID(N'social.TripReviewMediaOperations',N'U') IS NULL
  THROW 51000, 'TM-79 recovery requires the media journal.',1;
 IF OBJECT_ID(N'social.TM79_ExpectedRecovery') IS NOT NULL
  THROW 51000, 'TM-79 recovery validation object already exists.',1;
 IF OBJECT_ID(N'social.TripReviewMediaRecovery') IS NOT NULL AND OBJECT_ID(N'social.TripReviewMediaRecovery',N'U') IS NULL
  THROW 51000, 'TM-79 recovery object has wrong type.',1;
 IF OBJECT_ID(N'social.TripReviewMediaRecovery',N'U') IS NULL
 BEGIN
CREATE TABLE social.TripReviewMediaRecovery (
 operation_id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_TripReviewMediaRecovery PRIMARY KEY,
 upload_fence UNIQUEIDENTIFIER NULL,
 upload_lease_until DATETIME2(7) NULL,
 upload_outcome VARCHAR(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
 cleanup_fence UNIQUEIDENTIFIER NULL,
 cleanup_lease_until DATETIME2(7) NULL,
 attempts INT NOT NULL CONSTRAINT DF_TripReviewMediaRecovery_Attempts DEFAULT(0),
 next_attempt_at DATETIME2(7) NULL,
 exhausted BIT NOT NULL CONSTRAINT DF_TripReviewMediaRecovery_Exhausted DEFAULT(0),
 last_failure_code VARCHAR(24) COLLATE Latin1_General_100_BIN2 NULL,
 last_failure_at DATETIME2(7) NULL,
 version ROWVERSION NOT NULL,
 CONSTRAINT FK_TripReviewMediaRecovery_Operation FOREIGN KEY(operation_id) REFERENCES social.TripReviewMediaOperations(operation_id),
 CONSTRAINT CK_TripReviewMediaRecovery_Upload CHECK(upload_outcome IN ('NeverDispatched','Unknown','Succeeded','Rejected')),
 CONSTRAINT CK_TripReviewMediaRecovery_UploadFence CHECK((upload_fence IS NULL AND upload_lease_until IS NULL) OR (upload_fence IS NOT NULL AND upload_lease_until IS NOT NULL)),
 CONSTRAINT CK_TripReviewMediaRecovery_CleanupFence CHECK((cleanup_fence IS NULL AND cleanup_lease_until IS NULL) OR (cleanup_fence IS NOT NULL AND cleanup_lease_until IS NOT NULL)),
 CONSTRAINT CK_TripReviewMediaRecovery_Attempts CHECK(attempts BETWEEN 0 AND 8),
 CONSTRAINT CK_TripReviewMediaRecovery_Exhausted CHECK(exhausted=0 OR next_attempt_at IS NULL),
 CONSTRAINT CK_TripReviewMediaRecovery_Failure CHECK((last_failure_code IS NULL AND last_failure_at IS NULL) OR (last_failure_code IS NOT NULL AND last_failure_at IS NOT NULL AND last_failure_code IN ('Transient','Permanent','Configuration','OutcomeUnknown')))
);
CREATE INDEX IX_TripReviewMediaRecovery_Due ON social.TripReviewMediaRecovery(exhausted,next_attempt_at,operation_id);
 END;
CREATE TABLE social.TM79_ExpectedRecovery (
 operation_id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_TM79_ExpectedRecovery PRIMARY KEY,
 upload_fence UNIQUEIDENTIFIER NULL,
 upload_lease_until DATETIME2(7) NULL,
 upload_outcome VARCHAR(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
 cleanup_fence UNIQUEIDENTIFIER NULL,
 cleanup_lease_until DATETIME2(7) NULL,
 attempts INT NOT NULL CONSTRAINT DF_TM79_ExpectedRecovery_Attempts DEFAULT(0),
 next_attempt_at DATETIME2(7) NULL,
 exhausted BIT NOT NULL CONSTRAINT DF_TM79_ExpectedRecovery_Exhausted DEFAULT(0),
 last_failure_code VARCHAR(24) COLLATE Latin1_General_100_BIN2 NULL,
 last_failure_at DATETIME2(7) NULL,
 version ROWVERSION NOT NULL,
 CONSTRAINT FK_TM79_ExpectedRecovery_Operation FOREIGN KEY(operation_id) REFERENCES social.TripReviewMediaOperations(operation_id),
 CONSTRAINT CK_TM79_ExpectedRecovery_Upload CHECK(upload_outcome IN ('NeverDispatched','Unknown','Succeeded','Rejected')),
 CONSTRAINT CK_TM79_ExpectedRecovery_UploadFence CHECK((upload_fence IS NULL AND upload_lease_until IS NULL) OR (upload_fence IS NOT NULL AND upload_lease_until IS NOT NULL)),
 CONSTRAINT CK_TM79_ExpectedRecovery_CleanupFence CHECK((cleanup_fence IS NULL AND cleanup_lease_until IS NULL) OR (cleanup_fence IS NOT NULL AND cleanup_lease_until IS NOT NULL)),
 CONSTRAINT CK_TM79_ExpectedRecovery_Attempts CHECK(attempts BETWEEN 0 AND 8),
 CONSTRAINT CK_TM79_ExpectedRecovery_Exhausted CHECK(exhausted=0 OR next_attempt_at IS NULL),
 CONSTRAINT CK_TM79_ExpectedRecovery_Failure CHECK((last_failure_code IS NULL AND last_failure_at IS NULL) OR (last_failure_code IS NOT NULL AND last_failure_at IS NOT NULL AND last_failure_code IN ('Transient','Permanent','Configuration','OutcomeUnknown')))
);
CREATE INDEX IX_TM79_ExpectedRecovery_Due ON social.TM79_ExpectedRecovery(exhausted,next_attempt_at,operation_id);
 DECLARE @inventory TABLE(side INT,item NVARCHAR(MAX));
 DECLARE @side INT=0,@target INT;
 WHILE @side<2
 BEGIN
 SET @target=CASE @side WHEN 0 THEN OBJECT_ID(N'social.TripReviewMediaRecovery') ELSE OBJECT_ID(N'social.TM79_ExpectedRecovery') END;
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
            INSERT @inventory(side,item) SELECT @side, REPLACE(item,N'TM79_ExpectedRecovery',N'TripReviewMediaRecovery') FROM Inventory;

 SET @side+=1;
 END;
 IF EXISTS(SELECT item FROM @inventory WHERE side=0 EXCEPT SELECT item FROM @inventory WHERE side=1)
 OR EXISTS(SELECT item FROM @inventory WHERE side=1 EXCEPT SELECT item FROM @inventory WHERE side=0)
 OR (SELECT COUNT(*) FROM @inventory WHERE side=0)<>(SELECT COUNT(*) FROM @inventory WHERE side=1)
  THROW 51000, 'TM-79 recovery inventory mismatch; no automatic repair.',1;
 DROP TABLE social.TM79_ExpectedRecovery;
 -- A complete persisted upload tuple is durable success evidence even when a
 -- pre-8c failure already moved the operation to CleanupPending/Cleaned.
 -- An all-NULL tuple remains unknown: state/elapsed time cannot prove absence.
 INSERT social.TripReviewMediaRecovery(operation_id,upload_outcome)
 SELECT o.operation_id,CASE
  WHEN o.delivery_url IS NOT NULL AND o.stored_byte_length IS NOT NULL AND o.uploaded_at IS NOT NULL THEN 'Succeeded'
  ELSE 'Unknown' END
 FROM social.TripReviewMediaOperations o
 WHERE NOT EXISTS(SELECT 1 FROM social.TripReviewMediaRecovery r WHERE r.operation_id=o.operation_id);
 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
