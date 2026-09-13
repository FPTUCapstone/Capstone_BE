IF OBJECT_ID(N'social.TravelGroupCreationRequests', N'U') IS NULL
BEGIN
    CREATE TABLE social.TravelGroupCreationRequests (
        request_id              BIGINT IDENTITY(1,1) PRIMARY KEY,
        traveler_user_id        BIGINT NOT NULL REFERENCES dbo.Users(user_id),
        idempotency_key         UNIQUEIDENTIFIER NOT NULL,
        itinerary_id            BIGINT NOT NULL REFERENCES planning.Itineraries(itinerary_id),
        group_name              NVARCHAR(150) NOT NULL,
        group_id                BIGINT NOT NULL REFERENCES social.TravelGroups(group_id) ON DELETE CASCADE,
        created_at              DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_TravelGroupCreationRequests_TravelerKey
            UNIQUE (traveler_user_id, idempotency_key)
    );
END;
ELSE
BEGIN
    IF COL_LENGTH(N'social.TravelGroupCreationRequests', N'itinerary_id') IS NULL
    BEGIN
        ALTER TABLE social.TravelGroupCreationRequests ADD itinerary_id BIGINT NULL;
        UPDATE request
        SET itinerary_id = travel_group.itinerary_id
        FROM social.TravelGroupCreationRequests request
        INNER JOIN social.TravelGroups travel_group ON travel_group.group_id = request.group_id;
        ALTER TABLE social.TravelGroupCreationRequests ALTER COLUMN itinerary_id BIGINT NOT NULL;
        ALTER TABLE social.TravelGroupCreationRequests
            ADD CONSTRAINT FK_TravelGroupCreationRequests_Itinerary
            FOREIGN KEY (itinerary_id) REFERENCES planning.Itineraries(itinerary_id);
    END;

    IF COL_LENGTH(N'social.TravelGroupCreationRequests', N'group_name') IS NULL
    BEGIN
        ALTER TABLE social.TravelGroupCreationRequests ADD group_name NVARCHAR(150) NULL;
        UPDATE request
        SET group_name = COALESCE(travel_group.name, N'Unnamed Group')
        FROM social.TravelGroupCreationRequests request
        INNER JOIN social.TravelGroups travel_group ON travel_group.group_id = request.group_id;
        ALTER TABLE social.TravelGroupCreationRequests ALTER COLUMN group_name NVARCHAR(150) NOT NULL;
    END;
END;
GO
