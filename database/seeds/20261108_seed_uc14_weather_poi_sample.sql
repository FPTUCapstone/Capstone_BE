/*
   UC-14 sample POIs for real-time alert testing (Da Nang).

   Taken from the team's POI dataset file (tripmate.ndjson, scraped 2026-10-21). Only name, coordinates,
   address, category, visit duration, cost, opening hours and the Indoor/Outdoor/Mixed value are imported.
   No photos, ratings, reviews, place ids or phone numbers are copied.

   What each POI exercises:
   - Outdoor + reviewed: Dinh Ban Co, Bai tam Lien Chieu, Son Tra Tinh Vien (rain/thunderstorm alerts).
   - Indoor: Bao tang Ho Chi Minh (closed on day_of_week 1 and 5), Bao tang 3D Art In Paradise.
   - Mixed: Cho Han, Cho Dem An Thuong (opens 18:00, so a daytime visit raises a POIClosure alert).
   Mixed POIs never raise rain-only alerts (only Outdoor does); that is the UC-14 policy, not a seed bug.

   The Indoor/Outdoor value follows the dataset's category rule (Nature/Beach = Outdoor, Museum = Indoor,
   Culture = Mixed). This script stamps indoor_outdoor_reviewed_at_utc because the rows above were
   checked by hand; do not reuse it to bulk-stamp other POIs.

   Requires schema v7 plus migration 20261028_uc14_tomorrow_weather.sql. Idempotent: rerunning updates
   the same rows (matched by name and coordinates).
*/
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'catalog.POIs', N'indoor_outdoor_reviewed_at_utc') IS NULL
        THROW 51015, 'Apply 20261028_uc14_tomorrow_weather.sql before this seed.', 1;

    DECLARE @VerifiedAtUtc DATETIME2 = '2026-10-21T00:00:00';

    DECLARE @PoiSeed TABLE
    (
        name NVARCHAR(200) COLLATE Latin1_General_100_CI_AI NOT NULL,
        category_name NVARCHAR(100) NOT NULL,
        latitude DECIMAL(9, 6) NOT NULL,
        longitude DECIMAL(9, 6) NOT NULL,
        address NVARCHAR(400) NULL,
        indoor_outdoor VARCHAR(10) NOT NULL,
        average_visit_duration_minutes INT NOT NULL,
        has_shelter BIT NOT NULL,
        estimated_visit_cost DECIMAL(12, 2) NULL,
        source_url NVARCHAR(500) NOT NULL
    );

    INSERT @PoiSeed VALUES
        (N'Đỉnh Bàn Cờ', N'Nature', 16.118937, 108.271983,
            N'Bàn cờ, Sơn Trà, Đà Nẵng, Việt Nam', 'Outdoor', 90, 0, 0, N'https://www.google.com/maps?cid=7629724912977954540'),
        (N'Bãi tắm Liên Chiểu', N'Beach', 16.088756, 108.154841,
            N'35Q3+GW4, Nguyễn Tất Thành, Hòa Khánh, Đà Nẵng, Việt Nam', 'Outdoor', 120, 0, 0, N'https://www.google.com/maps?cid=4330726239558322072'),
        (N'Sơn Trà Tịnh Viên', N'Nature', 16.114732, 108.257247,
            N'Lương Hữu Khánh, Sơn Trà, Đà Nẵng, Việt Nam', 'Outdoor', 90, 0, 0, N'https://www.google.com/maps?cid=9165646042076952942'),
        (N'Bảo tàng Hồ Chí Minh', N'Museum', 16.048681, 108.217581,
            N'3 Duy Tân, Hòa Cường, Đà Nẵng, Việt Nam', 'Indoor', 90, 1, 50000, N'https://www.google.com/maps?cid=2965507376873198390'),
        (N'Bảo tàng 3D Art In Paradise', N'Museum', 16.095428, 108.243109,
            N'Lô 10 Trần Nhân Tông, Sơn Trà, Đà Nẵng, Việt Nam', 'Indoor', 90, 1, 50000, N'https://www.google.com/maps?cid=10503400368954609584'),
        (N'Chợ Hàn', N'Culture', 16.068084, 108.224069,
            N'119 Trần Phú, Hải Châu, Đà Nẵng, Việt Nam', 'Mixed', 90, 1, 0, N'https://www.google.com/maps?cid=10426483548095678271'),
        (N'Chợ Đêm An Thượng', N'Culture', 16.050270, 108.247707,
            N'Trần Bạch Đằng, Ngũ Hành Sơn, Đà Nẵng, Việt Nam', 'Mixed', 90, 1, 0, N'https://www.google.com/maps?cid=10893993350059459299');

    DECLARE @HoursSeed TABLE
    (
        name NVARCHAR(200) COLLATE Latin1_General_100_CI_AI NOT NULL,
        latitude DECIMAL(9, 6) NOT NULL,
        longitude DECIMAL(9, 6) NOT NULL,
        day_of_week TINYINT NOT NULL,
        open_time TIME NULL,
        close_time TIME NULL,
        is_closed BIT NOT NULL
    );

    INSERT @HoursSeed VALUES
        (N'Đỉnh Bàn Cờ', 16.118937, 108.271983, 0, '07:30', '17:30', 0),
        (N'Đỉnh Bàn Cờ', 16.118937, 108.271983, 1, '07:30', '17:30', 0),
        (N'Đỉnh Bàn Cờ', 16.118937, 108.271983, 2, '07:30', '17:30', 0),
        (N'Đỉnh Bàn Cờ', 16.118937, 108.271983, 3, '07:30', '17:30', 0),
        (N'Đỉnh Bàn Cờ', 16.118937, 108.271983, 4, '07:30', '17:30', 0),
        (N'Đỉnh Bàn Cờ', 16.118937, 108.271983, 5, '07:30', '17:30', 0),
        (N'Đỉnh Bàn Cờ', 16.118937, 108.271983, 6, '07:30', '17:30', 0),
        (N'Bãi tắm Liên Chiểu', 16.088756, 108.154841, 0, '05:00', '19:00', 0),
        (N'Bãi tắm Liên Chiểu', 16.088756, 108.154841, 1, '05:00', '19:00', 0),
        (N'Bãi tắm Liên Chiểu', 16.088756, 108.154841, 2, '05:00', '19:00', 0),
        (N'Bãi tắm Liên Chiểu', 16.088756, 108.154841, 3, '05:00', '19:00', 0),
        (N'Bãi tắm Liên Chiểu', 16.088756, 108.154841, 4, '05:00', '19:00', 0),
        (N'Bãi tắm Liên Chiểu', 16.088756, 108.154841, 5, '05:00', '19:00', 0),
        (N'Bãi tắm Liên Chiểu', 16.088756, 108.154841, 6, '05:00', '19:00', 0),
        (N'Sơn Trà Tịnh Viên', 16.114732, 108.257247, 0, '06:30', '17:00', 0),
        (N'Sơn Trà Tịnh Viên', 16.114732, 108.257247, 1, '06:30', '17:00', 0),
        (N'Sơn Trà Tịnh Viên', 16.114732, 108.257247, 2, '06:30', '17:00', 0),
        (N'Sơn Trà Tịnh Viên', 16.114732, 108.257247, 3, '06:30', '17:00', 0),
        (N'Sơn Trà Tịnh Viên', 16.114732, 108.257247, 4, '06:30', '17:00', 0),
        (N'Sơn Trà Tịnh Viên', 16.114732, 108.257247, 5, '06:30', '17:00', 0),
        (N'Sơn Trà Tịnh Viên', 16.114732, 108.257247, 6, '06:30', '17:00', 0),
        (N'Bảo tàng Hồ Chí Minh', 16.048681, 108.217581, 0, '08:00', '16:30', 0),
        (N'Bảo tàng Hồ Chí Minh', 16.048681, 108.217581, 1, NULL, NULL, 1),
        (N'Bảo tàng Hồ Chí Minh', 16.048681, 108.217581, 2, '08:00', '16:30', 0),
        (N'Bảo tàng Hồ Chí Minh', 16.048681, 108.217581, 3, '08:00', '16:30', 0),
        (N'Bảo tàng Hồ Chí Minh', 16.048681, 108.217581, 4, '08:00', '16:30', 0),
        (N'Bảo tàng Hồ Chí Minh', 16.048681, 108.217581, 5, NULL, NULL, 1),
        (N'Bảo tàng Hồ Chí Minh', 16.048681, 108.217581, 6, '08:00', '16:30', 0),
        (N'Bảo tàng 3D Art In Paradise', 16.095428, 108.243109, 0, '08:30', '18:00', 0),
        (N'Bảo tàng 3D Art In Paradise', 16.095428, 108.243109, 1, '08:30', '18:00', 0),
        (N'Bảo tàng 3D Art In Paradise', 16.095428, 108.243109, 2, '08:30', '18:00', 0),
        (N'Bảo tàng 3D Art In Paradise', 16.095428, 108.243109, 3, '08:30', '18:00', 0),
        (N'Bảo tàng 3D Art In Paradise', 16.095428, 108.243109, 4, '08:30', '18:00', 0),
        (N'Bảo tàng 3D Art In Paradise', 16.095428, 108.243109, 5, '08:30', '18:00', 0),
        (N'Bảo tàng 3D Art In Paradise', 16.095428, 108.243109, 6, '08:30', '18:00', 0),
        (N'Chợ Hàn', 16.068084, 108.224069, 0, '06:00', '19:00', 0),
        (N'Chợ Hàn', 16.068084, 108.224069, 1, '06:00', '19:00', 0),
        (N'Chợ Hàn', 16.068084, 108.224069, 2, '06:00', '19:00', 0),
        (N'Chợ Hàn', 16.068084, 108.224069, 3, '06:00', '19:00', 0),
        (N'Chợ Hàn', 16.068084, 108.224069, 4, '06:00', '19:00', 0),
        (N'Chợ Hàn', 16.068084, 108.224069, 5, '06:00', '19:00', 0),
        (N'Chợ Hàn', 16.068084, 108.224069, 6, '06:00', '19:00', 0),
        (N'Chợ Đêm An Thượng', 16.050270, 108.247707, 0, '18:00', '23:00', 0),
        (N'Chợ Đêm An Thượng', 16.050270, 108.247707, 1, '18:00', '23:00', 0),
        (N'Chợ Đêm An Thượng', 16.050270, 108.247707, 2, '18:00', '23:00', 0),
        (N'Chợ Đêm An Thượng', 16.050270, 108.247707, 3, '18:00', '23:00', 0),
        (N'Chợ Đêm An Thượng', 16.050270, 108.247707, 4, '18:00', '23:00', 0),
        (N'Chợ Đêm An Thượng', 16.050270, 108.247707, 5, '18:00', '23:00', 0),
        (N'Chợ Đêm An Thượng', 16.050270, 108.247707, 6, '18:00', '23:00', 0);

    INSERT catalog.POICategories (name)
    SELECT DISTINCT seed.category_name
    FROM @PoiSeed AS seed
    WHERE NOT EXISTS (SELECT 1 FROM catalog.POICategories AS category WHERE category.name = seed.category_name);

    MERGE catalog.POIs AS target
    USING
    (
        SELECT category.category_id, seed.*
        FROM @PoiSeed AS seed
        INNER JOIN catalog.POICategories AS category ON category.name = seed.category_name
    ) AS source
    ON target.name = source.name
       AND target.latitude = source.latitude
       AND target.longitude = source.longitude
    WHEN MATCHED THEN
        UPDATE SET
            category_id = source.category_id,
            address = source.address,
            indoor_outdoor = source.indoor_outdoor,
            indoor_outdoor_reviewed_at_utc = SYSUTCDATETIME(),
            avg_visit_duration_minutes = source.average_visit_duration_minutes,
            has_shelter = source.has_shelter,
            status = 'Active',
            estimated_visit_cost = source.estimated_visit_cost,
            source_url = source.source_url,
            verified_at = @VerifiedAtUtc,
            updated_at = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (category_id, name, latitude, longitude, address, indoor_outdoor, indoor_outdoor_reviewed_at_utc,
                avg_visit_duration_minutes, has_shelter, status, estimated_visit_cost,
                source_url, verified_at, created_at, updated_at)
        VALUES (source.category_id, source.name, source.latitude, source.longitude,
                source.address, source.indoor_outdoor, SYSUTCDATETIME(), source.average_visit_duration_minutes,
                source.has_shelter, 'Active', source.estimated_visit_cost, source.source_url,
                @VerifiedAtUtc, SYSUTCDATETIME(), SYSUTCDATETIME());

    MERGE catalog.POIOpeningHours AS target
    USING
    (
        SELECT poi.poi_id, hours.day_of_week, hours.open_time, hours.close_time, hours.is_closed
        FROM @HoursSeed AS hours
        INNER JOIN catalog.POIs AS poi
            ON poi.name = hours.name AND poi.latitude = hours.latitude AND poi.longitude = hours.longitude
    ) AS source
    ON target.poi_id = source.poi_id AND target.day_of_week = source.day_of_week
    WHEN MATCHED THEN
        UPDATE SET open_time = source.open_time, close_time = source.close_time, is_closed = source.is_closed
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (poi_id, day_of_week, open_time, close_time, is_closed)
        VALUES (source.poi_id, source.day_of_week, source.open_time, source.close_time, source.is_closed);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
