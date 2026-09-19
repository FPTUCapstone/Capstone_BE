SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'social.GroupJoinOperations', N'U') IS NULL
BEGIN
    CREATE TABLE social.GroupJoinOperations (
        join_operation_id    BIGINT IDENTITY(1,1) NOT NULL,
        traveler_user_id     BIGINT NOT NULL,
        group_id             BIGINT NOT NULL,
        invitation_id        BIGINT NOT NULL,
        invitation_code      VARCHAR(20) NOT NULL,
        idempotency_key      UNIQUEIDENTIFIER NOT NULL,
        created_at           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_GroupJoinOperations PRIMARY KEY (join_operation_id),
        CONSTRAINT FK_GroupJoinOperations_Traveler
            FOREIGN KEY (traveler_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_GroupJoinOperations_Group
            FOREIGN KEY (group_id) REFERENCES social.TravelGroups(group_id),
        CONSTRAINT FK_GroupJoinOperations_Invitation
            FOREIGN KEY (invitation_id) REFERENCES social.GroupInvitations(invitation_id) ON DELETE CASCADE,
        CONSTRAINT UQ_GroupJoinOperations_TravelerKey
            UNIQUE (traveler_user_id, idempotency_key)
    );
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_key_columns AS foreign_key_column
    WHERE foreign_key_column.parent_object_id = OBJECT_ID(N'social.GroupJoinOperations')
      AND foreign_key_column.parent_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'social.GroupJoinOperations'), N'traveler_user_id', 'ColumnId')
      AND foreign_key_column.referenced_object_id = OBJECT_ID(N'dbo.Users')
      AND foreign_key_column.referenced_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'dbo.Users'), N'user_id', 'ColumnId'))
BEGIN
    ALTER TABLE social.GroupJoinOperations
    ADD CONSTRAINT FK_GroupJoinOperations_Traveler
        FOREIGN KEY (traveler_user_id) REFERENCES dbo.Users(user_id);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_key_columns AS foreign_key_column
    WHERE foreign_key_column.parent_object_id = OBJECT_ID(N'social.GroupJoinOperations')
      AND foreign_key_column.parent_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'social.GroupJoinOperations'), N'group_id', 'ColumnId')
      AND foreign_key_column.referenced_object_id = OBJECT_ID(N'social.TravelGroups')
      AND foreign_key_column.referenced_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'social.TravelGroups'), N'group_id', 'ColumnId'))
BEGIN
    ALTER TABLE social.GroupJoinOperations
    ADD CONSTRAINT FK_GroupJoinOperations_Group
        FOREIGN KEY (group_id) REFERENCES social.TravelGroups(group_id);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_key_columns AS foreign_key_column
    WHERE foreign_key_column.parent_object_id = OBJECT_ID(N'social.GroupJoinOperations')
      AND foreign_key_column.parent_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'social.GroupJoinOperations'), N'invitation_id', 'ColumnId')
      AND foreign_key_column.referenced_object_id = OBJECT_ID(N'social.GroupInvitations')
      AND foreign_key_column.referenced_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'social.GroupInvitations'), N'invitation_id', 'ColumnId'))
BEGIN
    ALTER TABLE social.GroupJoinOperations
    ADD CONSTRAINT FK_GroupJoinOperations_Invitation
        FOREIGN KEY (invitation_id) REFERENCES social.GroupInvitations(invitation_id) ON DELETE CASCADE;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes AS [index]
    WHERE [index].object_id = OBJECT_ID(N'social.GroupJoinOperations')
      AND [index].is_unique = 1
      AND (SELECT COUNT(*)
           FROM sys.index_columns AS index_column
           WHERE index_column.object_id = [index].object_id
             AND index_column.index_id = [index].index_id
             AND index_column.key_ordinal > 0) = 2
      AND EXISTS (
          SELECT 1
          FROM sys.index_columns AS index_column
          WHERE index_column.object_id = [index].object_id
            AND index_column.index_id = [index].index_id
            AND index_column.key_ordinal = 1
            AND COL_NAME(index_column.object_id, index_column.column_id) = N'traveler_user_id')
      AND EXISTS (
          SELECT 1
          FROM sys.index_columns AS index_column
          WHERE index_column.object_id = [index].object_id
            AND index_column.index_id = [index].index_id
            AND index_column.key_ordinal = 2
            AND COL_NAME(index_column.object_id, index_column.column_id) = N'idempotency_key'))
BEGIN
    ALTER TABLE social.GroupJoinOperations
    ADD CONSTRAINT UQ_GroupJoinOperations_TravelerKey
        UNIQUE (traveler_user_id, idempotency_key);
END;

COMMIT TRANSACTION;
