-- TM-70 migration-test snapshot of the relevant schema at
-- origin/develop@86516e2a85394341e6fad45bc78e3899c363ead6.
-- This intentionally contains only the parent, target, and child tables needed to
-- prove that the additive migration preserves rows, identities, and foreign keys.

CREATE SCHEMA commerce;
GO

CREATE TABLE dbo.Users (
    user_id BIGINT NOT NULL PRIMARY KEY
);
GO

CREATE TABLE dbo.OperatorProfiles (
    user_id BIGINT NOT NULL PRIMARY KEY
);
GO

CREATE TABLE commerce.Tours (
    tour_id BIGINT IDENTITY(1,1) PRIMARY KEY,
    operator_user_id BIGINT NOT NULL REFERENCES dbo.OperatorProfiles(user_id),
    title NVARCHAR(200) NOT NULL,
    description NVARCHAR(MAX) NULL,
    base_price DECIMAL(12,2) NOT NULL CHECK (base_price >= 0),
    duration_days INT NOT NULL DEFAULT 1 CHECK (duration_days > 0),
    status VARCHAR(12) NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft','Pending','Approved','Rejected','Inactive')),
    rejection_reason NVARCHAR(500) NULL,
    reviewed_by BIGINT NULL REFERENCES dbo.Users(user_id),
    reviewed_at DATETIME2 NULL,
    published_at DATETIME2 NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_Tours_Operator ON commerce.Tours(operator_user_id);
CREATE INDEX IX_Tours_Status ON commerce.Tours(status);
GO

CREATE TABLE commerce.TourSchedules (
    schedule_id BIGINT IDENTITY(1,1) PRIMARY KEY,
    tour_id BIGINT NOT NULL REFERENCES commerce.Tours(tour_id),
    start_datetime DATETIME2 NOT NULL,
    end_datetime DATETIME2 NOT NULL,
    meeting_point NVARCHAR(500) NULL,
    total_capacity INT NOT NULL CHECK (total_capacity > 0),
    reserved_capacity INT NOT NULL DEFAULT 0 CHECK (reserved_capacity >= 0),
    status VARCHAR(12) NOT NULL DEFAULT 'Scheduled'
        CHECK (status IN ('Scheduled','Cancelled','Completed')),
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_TourSchedules_CapacityWithinLimit
        CHECK (reserved_capacity <= total_capacity),
    CONSTRAINT CK_TourSchedules_DateOrder CHECK (end_datetime > start_datetime)
);
GO
