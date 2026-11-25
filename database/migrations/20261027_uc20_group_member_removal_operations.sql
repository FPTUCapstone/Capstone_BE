/* UC-20: durable result for exact member-removal retries, including after rejoining. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'social.GroupMemberRemovalOperations', N'U') IS NULL
BEGIN
    CREATE TABLE social.GroupMemberRemovalOperations (
        remove_operation_id BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_GroupMemberRemovalOperations PRIMARY KEY,
        host_user_id BIGINT NOT NULL,
        group_id BIGINT NOT NULL,
        member_user_id BIGINT NOT NULL,
        member_name NVARCHAR(150) NOT NULL,
        idempotency_key UNIQUEIDENTIFIER NOT NULL,
        removed_at DATETIME2 NOT NULL,
        CONSTRAINT UQ_GroupMemberRemovalOperations_HostKey
            UNIQUE (host_user_id, idempotency_key),
        CONSTRAINT CK_GroupMemberRemovalOperations_DistinctUsers
            CHECK (host_user_id <> member_user_id),
        CONSTRAINT FK_GroupMemberRemovalOperations_Host
            FOREIGN KEY (host_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_GroupMemberRemovalOperations_Member
            FOREIGN KEY (member_user_id) REFERENCES dbo.Users(user_id),
        CONSTRAINT FK_GroupMemberRemovalOperations_Group
            FOREIGN KEY (group_id) REFERENCES social.TravelGroups(group_id)
    );
END;

COMMIT TRANSACTION;
