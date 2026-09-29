SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'social.GroupInvitationOperations', N'U') IS NULL
BEGIN
    CREATE TABLE social.GroupInvitationOperations (
        operation_id         BIGINT IDENTITY(1,1) NOT NULL,
        traveler_user_id     BIGINT NOT NULL,
        group_id             BIGINT NOT NULL,
        operation_type       VARCHAR(20) NOT NULL
            CONSTRAINT CK_GroupInvitationOperations_OperationType
                CHECK (operation_type IN ('GetOrCreate', 'Regenerate')),
        idempotency_key      UNIQUEIDENTIFIER NOT NULL,
        invitation_id        BIGINT NOT NULL,
        created_at           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_GroupInvitationOperations PRIMARY KEY (operation_id),
        CONSTRAINT FK_GroupInvitationOperations_Traveler
            FOREIGN KEY (traveler_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_GroupInvitationOperations_Group
            FOREIGN KEY (group_id) REFERENCES social.TravelGroups(group_id),
        CONSTRAINT FK_GroupInvitationOperations_Invitation
            FOREIGN KEY (invitation_id) REFERENCES social.GroupInvitations(invitation_id) ON DELETE CASCADE,
        CONSTRAINT UQ_GroupInvitationOperations_TravelerKey
            UNIQUE (traveler_user_id, idempotency_key)
    );
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_key_columns AS foreign_key_column
    WHERE foreign_key_column.parent_object_id = OBJECT_ID(N'social.GroupInvitationOperations')
      AND foreign_key_column.parent_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'social.GroupInvitationOperations'), N'traveler_user_id', 'ColumnId')
      AND foreign_key_column.referenced_object_id = OBJECT_ID(N'dbo.Users')
      AND foreign_key_column.referenced_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'dbo.Users'), N'user_id', 'ColumnId'))
BEGIN
    ALTER TABLE social.GroupInvitationOperations
    ADD CONSTRAINT FK_GroupInvitationOperations_Traveler
        FOREIGN KEY (traveler_user_id) REFERENCES dbo.Users(user_id);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_key_columns AS foreign_key_column
    WHERE foreign_key_column.parent_object_id = OBJECT_ID(N'social.GroupInvitationOperations')
      AND foreign_key_column.parent_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'social.GroupInvitationOperations'), N'group_id', 'ColumnId')
      AND foreign_key_column.referenced_object_id = OBJECT_ID(N'social.TravelGroups')
      AND foreign_key_column.referenced_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'social.TravelGroups'), N'group_id', 'ColumnId'))
BEGIN
    ALTER TABLE social.GroupInvitationOperations
    ADD CONSTRAINT FK_GroupInvitationOperations_Group
        FOREIGN KEY (group_id) REFERENCES social.TravelGroups(group_id);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_key_columns AS foreign_key_column
    WHERE foreign_key_column.parent_object_id = OBJECT_ID(N'social.GroupInvitationOperations')
      AND foreign_key_column.parent_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'social.GroupInvitationOperations'), N'invitation_id', 'ColumnId')
      AND foreign_key_column.referenced_object_id = OBJECT_ID(N'social.GroupInvitations')
      AND foreign_key_column.referenced_column_id = COLUMNPROPERTY(
          OBJECT_ID(N'social.GroupInvitations'), N'invitation_id', 'ColumnId'))
BEGIN
    ALTER TABLE social.GroupInvitationOperations
    ADD CONSTRAINT FK_GroupInvitationOperations_Invitation
        FOREIGN KEY (invitation_id) REFERENCES social.GroupInvitations(invitation_id) ON DELETE CASCADE;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'social.GroupInvitationOperations')
      AND definition LIKE N'%operation_type%'
      AND definition LIKE N'%GetOrCreate%'
      AND definition LIKE N'%Regenerate%')
BEGIN
    ALTER TABLE social.GroupInvitationOperations
    ADD CONSTRAINT CK_GroupInvitationOperations_OperationType
        CHECK (operation_type IN ('GetOrCreate', 'Regenerate'));
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes AS [index]
    WHERE [index].object_id = OBJECT_ID(N'social.GroupInvitationOperations')
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
    ALTER TABLE social.GroupInvitationOperations
    ADD CONSTRAINT UQ_GroupInvitationOperations_TravelerKey
        UNIQUE (traveler_user_id, idempotency_key);
END;

COMMIT TRANSACTION;
