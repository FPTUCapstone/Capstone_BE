IF OBJECT_ID(N'social.TravelGroupCreationRequests', N'U') IS NULL
BEGIN
    CREATE TABLE social.TravelGroupCreationRequests (
        request_id              BIGINT IDENTITY(1,1) PRIMARY KEY,
        traveler_user_id        BIGINT NOT NULL REFERENCES dbo.Users(user_id),
        idempotency_key         UNIQUEIDENTIFIER NOT NULL,
        group_id                BIGINT NOT NULL REFERENCES social.TravelGroups(group_id) ON DELETE CASCADE,
        created_at              DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_TravelGroupCreationRequests_TravelerKey
            UNIQUE (traveler_user_id, idempotency_key)
    );
END;
GO
