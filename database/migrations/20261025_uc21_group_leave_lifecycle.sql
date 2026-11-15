/* UC-21: explicit travel-group lifecycle and durable leave replay. Deploy before the BE release. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

/* Do not guess whether a legacy group without an active Host was already closed. */
IF COL_LENGTH(N'social.TravelGroups', N'status') IS NULL
   AND EXISTS (
       SELECT 1
       FROM social.TravelGroups AS g
       WHERE NOT EXISTS (
           SELECT 1 FROM social.GroupMembers AS m
           WHERE m.group_id = g.group_id
             AND m.user_id = g.host_user_id
             AND m.status = 'Active'))
BEGIN
    THROW 51021, 'UC-21 migration requires manual review of groups without an Active Host.', 1;
END;

IF COL_LENGTH(N'social.TravelGroups', N'status') IS NULL
BEGIN
    ALTER TABLE social.TravelGroups
        ADD status VARCHAR(10) NOT NULL
            CONSTRAINT DF_TravelGroups_Status DEFAULT ('Active') WITH VALUES;
END;

IF COL_LENGTH(N'social.TravelGroups', N'closed_at') IS NULL
BEGIN
    ALTER TABLE social.TravelGroups ADD closed_at DATETIME2 NULL;
END;

/* Compile lifecycle reads only after both new columns exist. The transaction spans GO batches. */
GO

IF EXISTS (
    SELECT 1 FROM social.TravelGroups
    WHERE status NOT IN ('Active', 'Closed')
       OR (status = 'Active' AND closed_at IS NOT NULL)
       OR (status = 'Closed' AND closed_at IS NULL))
BEGIN
    THROW 51022, 'UC-21 migration found inconsistent lifecycle values.', 1;
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'social.TravelGroups')
      AND name = N'CK_TravelGroups_Lifecycle')
BEGIN
    ALTER TABLE social.TravelGroups
        ADD CONSTRAINT CK_TravelGroups_Lifecycle
        CHECK ((status = 'Active' AND closed_at IS NULL)
            OR (status = 'Closed' AND closed_at IS NOT NULL));
END;

IF OBJECT_ID(N'social.GroupLeaveOperations', N'U') IS NULL
BEGIN
    CREATE TABLE social.GroupLeaveOperations (
        leave_operation_id BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_GroupLeaveOperations PRIMARY KEY,
        traveler_user_id BIGINT NOT NULL,
        group_id BIGINT NOT NULL,
        idempotency_key UNIQUEIDENTIFIER NOT NULL,
        group_closed BIT NOT NULL,
        new_host_user_id BIGINT NULL,
        left_at DATETIME2 NOT NULL,
        CONSTRAINT UQ_GroupLeaveOperations_TravelerKey
            UNIQUE (traveler_user_id, idempotency_key),
        CONSTRAINT CK_GroupLeaveOperations_Outcome
            CHECK (new_host_user_id IS NULL OR (group_closed = 0 AND new_host_user_id > 0)),
        CONSTRAINT FK_GroupLeaveOperations_Traveler
            FOREIGN KEY (traveler_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_GroupLeaveOperations_Group
            FOREIGN KEY (group_id) REFERENCES social.TravelGroups(group_id)
    );
END;

COMMIT TRANSACTION;
