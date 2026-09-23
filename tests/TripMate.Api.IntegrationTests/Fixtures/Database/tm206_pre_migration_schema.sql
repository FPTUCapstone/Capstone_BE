-- TM-206 migration-test snapshot of the smallest pre-TourMedia schema needed
-- to prove upgrade safety. It intentionally includes representative Tour,
-- TourSchedule, POI, and POIPhoto rows so tests can prove that the additive
-- migration neither rewrites nor confuses Tour-owned and POI-owned media.

CREATE SCHEMA catalog;
GO
CREATE SCHEMA commerce;
GO

CREATE TABLE dbo.Users (
    user_id BIGINT NOT NULL CONSTRAINT PK_Users PRIMARY KEY
);
GO

CREATE TABLE dbo.OperatorProfiles (
    user_id BIGINT NOT NULL CONSTRAINT PK_OperatorProfiles PRIMARY KEY,
    CONSTRAINT FK_OperatorProfiles_Users FOREIGN KEY (user_id)
        REFERENCES dbo.Users(user_id)
);
GO

CREATE TABLE catalog.POICategories (
    category_id INT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_POICategories PRIMARY KEY,
    name NVARCHAR(100) NOT NULL
);
GO

CREATE TABLE catalog.POIs (
    poi_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_POIs PRIMARY KEY,
    category_id INT NOT NULL,
    name NVARCHAR(200) NOT NULL,
    latitude DECIMAL(9,6) NOT NULL,
    longitude DECIMAL(9,6) NOT NULL,
    CONSTRAINT FK_POIs_Categories FOREIGN KEY (category_id)
        REFERENCES catalog.POICategories(category_id)
);
GO

CREATE TABLE catalog.POIPhotos (
    photo_id BIGINT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_POIPhotos PRIMARY KEY,
    poi_id BIGINT NOT NULL,
    url NVARCHAR(500) NOT NULL,
    caption NVARCHAR(200) NULL,
    sort_order INT NOT NULL CONSTRAINT DF_POIPhotos_SortOrder DEFAULT 0,
    CONSTRAINT FK_POIPhotos_POIs FOREIGN KEY (poi_id)
        REFERENCES catalog.POIs(poi_id) ON DELETE CASCADE
);
GO
CREATE INDEX IX_POIPhotos_POI ON catalog.POIPhotos(poi_id);
GO

CREATE TABLE commerce.Tours (
    tour_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Tours PRIMARY KEY,
    operator_user_id BIGINT NOT NULL,
    title NVARCHAR(200) NOT NULL,
    description NVARCHAR(MAX) NULL,
    destination NVARCHAR(300) COLLATE Vietnamese_100_CI_AS NULL,
    base_price DECIMAL(12,2) NOT NULL,
    duration_days INT NOT NULL CONSTRAINT DF_Tours_DurationDays DEFAULT 1,
    status VARCHAR(12) NOT NULL CONSTRAINT DF_Tours_Status DEFAULT 'Draft',
    rejection_reason NVARCHAR(500) NULL,
    reviewed_by BIGINT NULL,
    reviewed_at DATETIME2 NULL,
    published_at DATETIME2 NULL,
    created_at DATETIME2 NOT NULL CONSTRAINT DF_Tours_CreatedAt DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL CONSTRAINT DF_Tours_UpdatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Tours_Operator FOREIGN KEY (operator_user_id)
        REFERENCES dbo.OperatorProfiles(user_id),
    CONSTRAINT FK_Tours_Reviewer FOREIGN KEY (reviewed_by)
        REFERENCES dbo.Users(user_id),
    CONSTRAINT CK_Tours_BasePriceNonNegative CHECK (base_price >= 0),
    CONSTRAINT CK_Tours_BasePriceWholeVnd CHECK (base_price = FLOOR(base_price)),
    CONSTRAINT CK_Tours_DurationPositive CHECK (duration_days > 0),
    CONSTRAINT CK_Tours_Status CHECK (
        status IN ('Draft','Pending','Approved','Rejected','Inactive'))
);
GO

CREATE TABLE commerce.TourSchedules (
    schedule_id BIGINT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_TourSchedules PRIMARY KEY,
    tour_id BIGINT NOT NULL,
    start_datetime DATETIME2 NOT NULL,
    end_datetime DATETIME2 NOT NULL,
    total_capacity INT NOT NULL,
    reserved_capacity INT NOT NULL
        CONSTRAINT DF_TourSchedules_ReservedCapacity DEFAULT 0,
    status VARCHAR(12) NOT NULL
        CONSTRAINT DF_TourSchedules_Status DEFAULT 'Scheduled',
    created_at DATETIME2 NOT NULL
        CONSTRAINT DF_TourSchedules_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_TourSchedules_Tours FOREIGN KEY (tour_id)
        REFERENCES commerce.Tours(tour_id),
    CONSTRAINT CK_TourSchedules_CapacityPositive CHECK (total_capacity > 0),
    CONSTRAINT CK_TourSchedules_ReservedNonNegative CHECK (reserved_capacity >= 0),
    CONSTRAINT CK_TourSchedules_CapacityWithinLimit
        CHECK (reserved_capacity <= total_capacity),
    CONSTRAINT CK_TourSchedules_DateOrder
        CHECK (end_datetime > start_datetime),
    CONSTRAINT CK_TourSchedules_Status
        CHECK (status IN ('Scheduled','Cancelled','Completed'))
);
GO

INSERT INTO dbo.Users (user_id) VALUES (101), (102);
INSERT INTO dbo.OperatorProfiles (user_id) VALUES (101);

SET IDENTITY_INSERT catalog.POICategories ON;
INSERT INTO catalog.POICategories (category_id, name)
VALUES (201, N'Legacy category');
SET IDENTITY_INSERT catalog.POICategories OFF;

SET IDENTITY_INSERT catalog.POIs ON;
INSERT INTO catalog.POIs
    (poi_id, category_id, name, latitude, longitude)
VALUES
    (3001, 201, N'Legacy POI', 16.054407, 108.202164);
SET IDENTITY_INSERT catalog.POIs OFF;

SET IDENTITY_INSERT catalog.POIPhotos ON;
INSERT INTO catalog.POIPhotos
    (photo_id, poi_id, url, caption, sort_order)
VALUES
    (4001, 3001, N'https://example.invalid/poi-legacy.jpg',
     N'Existing POI-owned image', 1);
SET IDENTITY_INSERT catalog.POIPhotos OFF;

SET IDENTITY_INSERT commerce.Tours ON;
INSERT INTO commerce.Tours
    (tour_id, operator_user_id, title, destination, base_price,
     duration_days, status, reviewed_by, reviewed_at, published_at,
     created_at, updated_at)
VALUES
    (5001, 101, N'Existing approved Tour', N'Đà Nẵng', 1250000.00,
     3, 'Approved', 102, '2026-09-01T02:00:00', '2026-09-02T02:00:00',
     '2026-08-20T01:00:00', '2026-09-02T02:00:00');
SET IDENTITY_INSERT commerce.Tours OFF;

SET IDENTITY_INSERT commerce.TourSchedules ON;
INSERT INTO commerce.TourSchedules
    (schedule_id, tour_id, start_datetime, end_datetime,
     total_capacity, reserved_capacity, status, created_at)
VALUES
    (6001, 5001, '2026-10-01T01:00:00', '2026-10-04T01:00:00',
     12, 2, 'Scheduled', '2026-09-03T01:00:00');
SET IDENTITY_INSERT commerce.TourSchedules OFF;
GO
