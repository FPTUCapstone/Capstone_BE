CREATE SCHEMA catalog;
GO
CREATE SCHEMA planning;
GO

CREATE TABLE dbo.Users (
    user_id BIGINT IDENTITY(1,1) PRIMARY KEY
);
GO

CREATE TABLE catalog.POIs (
    poi_id BIGINT IDENTITY(1,1) PRIMARY KEY
);
GO

CREATE TABLE planning.SchedulingRequests (
    request_id BIGINT IDENTITY(1,1) PRIMARY KEY,
    traveler_user_id BIGINT NOT NULL REFERENCES dbo.Users(user_id),
    start_latitude DECIMAL(9,6) NOT NULL,
    start_longitude DECIMAL(9,6) NOT NULL,
    destination_latitude DECIMAL(9,6) NULL,
    destination_longitude DECIMAL(9,6) NULL,
    available_minutes INT NOT NULL,
    search_radius_km DECIMAL(6,2) NULL,
    budget DECIMAL(12,2) NULL,
    mandatory_poi_ids_json NVARCHAR(500) NULL,
    preferences_snapshot_json NVARCHAR(MAX) NULL,
    status VARCHAR(12) NOT NULL DEFAULT 'Pending',
    requested_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    completed_at DATETIME2 NULL
);
GO

CREATE TABLE planning.ItineraryItems (
    item_id BIGINT IDENTITY(1,1) PRIMARY KEY,
    poi_id BIGINT NOT NULL REFERENCES catalog.POIs(poi_id),
    sequence_no INT NOT NULL,
    planned_arrival DATETIME2 NULL,
    planned_departure DATETIME2 NULL,
    stay_duration_minutes INT NOT NULL DEFAULT 60,
    is_mandatory BIT NOT NULL DEFAULT 0
);
GO
