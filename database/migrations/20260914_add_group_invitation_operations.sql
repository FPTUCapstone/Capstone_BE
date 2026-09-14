SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'social.GroupInvitationOperations', N'U') IS NULL
BEGIN
    CREATE TABLE social.GroupInvitationOperations (
        operation_id         BIGINT IDENTITY(1,1) PRIMARY KEY,
        traveler_user_id     BIGINT NOT NULL REFERENCES dbo.Users(user_id),
        group_id             BIGINT NOT NULL REFERENCES social.TravelGroups(group_id),
        operation_type       VARCHAR(20) NOT NULL
            CHECK (operation_type IN ('GetOrCreate', 'Regenerate')),
        idempotency_key      UNIQUEIDENTIFIER NOT NULL,
        invitation_id        BIGINT NOT NULL REFERENCES social.GroupInvitations(invitation_id) ON DELETE CASCADE,
        created_at           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_GroupInvitationOperations_TravelerKey
            UNIQUE (traveler_user_id, idempotency_key)
    );
END;

COMMIT TRANSACTION;
