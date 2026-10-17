/*
   Real Da Nang / Hoi An POIs for UC-13 navigation testing (city-centre + Ngu Hanh Son + Hoi An).

   Coordinates: Mapbox Search Box retrieve (2026-10-15), checked against the
   returned ward. No photos or reviews are imported.

   Opening hours / admission:
   - Kim Bong Carpentry Village, My Son Sanctuary, VinWonders Nam Hoi An,
     Non Nuoc Stone Carving Village (free): Da Nang Tourism Promotion Center,
     "Plan Your Da Nang Journey 2026", published 2026-01-22.
   - Linh Ung Pagoda (Son Tra): Vietnam Airlines travel guide (free, 06:00-21:00).
   - Con Market: Vinpearl guide (07:00-19:30).
   - Public spaces (Dragon Bridge, Han River Bridge, Love Bridge, APEC Park,
     My Khe Beach) are free with unrestricted access; 05:00-23:00 is a
     TripMate planning window, not a published opening time.
   - Non Nuoc Stone Carving Village has no published hours; 08:00-17:30 is a
     planning estimate.
   Average visit durations are TripMate planning estimates.
*/
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @VerifiedAtUtc DATETIME2 = '2026-10-15T00:00:00';
    DECLARE @Official NVARCHAR(500) =
        N'https://danangfantasticity.com/en/news/plan-your-da-nang-journey-2026-updated-admission-prices-for-attractions-and-tourist-sites';

    DECLARE @PoiSeed TABLE
    (
        -- Same collation as catalog.POIs.name (20261015_poi_name_accent_insensitive.sql).
        name NVARCHAR(200) COLLATE Latin1_General_100_CI_AI NOT NULL,
        category_name NVARCHAR(100) NOT NULL,
        latitude DECIMAL(9, 6) NOT NULL,
        longitude DECIMAL(9, 6) NOT NULL,
        address NVARCHAR(400) NULL,
        indoor_outdoor VARCHAR(10) NOT NULL,
        average_visit_duration_minutes INT NOT NULL,
        has_shelter BIT NOT NULL,
        estimated_visit_cost DECIMAL(12, 2) NULL,
        open_time TIME NOT NULL,
        close_time TIME NOT NULL,
        source_url NVARCHAR(500) NOT NULL,
        tags NVARCHAR(200) NOT NULL
    );

    INSERT @PoiSeed VALUES
        (N'Cầu Rồng (Dragon Bridge)', N'Culture', 16.061158, 108.230008,
            N'Cầu Rồng, An Hải Tây, Đà Nẵng', 'Outdoor', 30, 0, 0, '05:00', '23:00',
            N'https://www.openstreetmap.org/?mlat=16.061158&mlon=108.230008', N'architecture,photography'),
        (N'Cầu Sông Hàn (Han River Bridge)', N'Culture', 16.072159, 108.226790,
            N'Cầu Sông Hàn, Hải Châu, Đà Nẵng', 'Outdoor', 30, 0, 0, '05:00', '23:00',
            N'https://www.openstreetmap.org/?mlat=16.072159&mlon=108.226790', N'architecture,photography'),
        (N'Cầu Tình Yêu & Tượng Cá Chép Hóa Rồng', N'Culture', 16.062936, 108.229821,
            N'Đường Trần Hưng Đạo, An Hải, Đà Nẵng', 'Outdoor', 30, 0, 0, '05:00', '23:00',
            N'https://www.openstreetmap.org/?mlat=16.062936&mlon=108.229821', N'photography,relaxation'),
        (N'Công viên APEC', N'Nature', 16.058101, 108.223250,
            N'Đường 2 Tháng 9, Bình Hiên, Đà Nẵng', 'Outdoor', 45, 0, 0, '05:00', '23:00',
            N'https://www.openstreetmap.org/?mlat=16.058101&mlon=108.223250', N'architecture,relaxation,photography'),
        (N'Bãi biển Mỹ Khê', N'Beach', 16.069967, 108.246066,
            N'Công viên Biển Đông, Phước Mỹ, Đà Nẵng', 'Outdoor', 120, 0, 0, '05:00', '23:00',
            N'https://www.openstreetmap.org/?mlat=16.069967&mlon=108.246066', N'beach,relaxation,nature'),
        (N'Chợ Cồn', N'Culture', 16.068420, 108.214113,
            N'339 Ông Ích Khiêm, Hải Châu II, Đà Nẵng', 'Indoor', 60, 1, 0, '07:00', '19:30',
            N'https://vinpearl.com/vi/luon-lo-cho-han-da-nang-tat-tan-tat-nhung-dieu-ban-nen-biet', N'culinary,shopping,culture'),
        (N'Chùa Linh Ứng Bãi Bụt (Sơn Trà)', N'Culture', 16.099685, 108.277042,
            N'Hoàng Sa, Thọ Quang, Sơn Trà, Đà Nẵng', 'Mixed', 60, 1, 0, '06:00', '21:00',
            N'https://www.vietnamairlines.com/vn/en/plan-book/travel/travel-guide/attractions-in-da-nang-vietnam', N'heritage,culture,nature,photography'),
        (N'Làng đá mỹ nghệ Non Nước', N'Culture', 16.002829, 108.261263,
            N'45 Huyền Trân Công Chúa, Hòa Hải, Đà Nẵng', 'Mixed', 45, 1, 0, '08:00', '17:30',
            @Official, N'heritage,culture,shopping'),
        (N'Làng mộc Kim Bồng', N'Culture', 15.865159, 108.324237,
            N'Cẩm Kim, Hội An, Đà Nẵng', 'Outdoor', 60, 0, 35000, '08:00', '17:30',
            @Official, N'heritage,culture'),
        (N'Thánh địa Mỹ Sơn', N'Culture', 15.768789, 108.119316,
            N'Duy Phú, Duy Xuyên, Đà Nẵng', 'Outdoor', 150, 0, 100000, '06:00', '18:00',
            @Official, N'heritage,history,architecture'),
        (N'VinWonders Nam Hội An', N'Theme park', 15.786494, 108.408451,
            N'Võ Chí Công, Bình Minh, Thăng Bình, Đà Nẵng', 'Mixed', 300, 1, 650000, '09:00', '19:00',
            @Official, N'relaxation');

    -- A fresh database only has the categories of the 20260914 seed; without these rows the MERGE below
    -- would silently skip every POI of a missing category.
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
            avg_visit_duration_minutes = source.average_visit_duration_minutes,
            has_shelter = source.has_shelter,
            status = 'Active',
            estimated_visit_cost = source.estimated_visit_cost,
            source_url = source.source_url,
            verified_at = @VerifiedAtUtc,
            updated_at = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (category_id, name, latitude, longitude, address, indoor_outdoor,
                avg_visit_duration_minutes, has_shelter, status, estimated_visit_cost,
                source_url, verified_at, created_at, updated_at)
        VALUES (source.category_id, source.name, source.latitude, source.longitude,
                source.address, source.indoor_outdoor, source.average_visit_duration_minutes,
                source.has_shelter, 'Active', source.estimated_visit_cost, source.source_url,
                @VerifiedAtUtc, SYSUTCDATETIME(), SYSUTCDATETIME());

    ;WITH SeededHours AS
    (
        SELECT poi.poi_id, day_of_week.day_of_week, seed.open_time, seed.close_time
        FROM @PoiSeed AS seed
        INNER JOIN catalog.POIs AS poi
            ON poi.name = seed.name AND poi.latitude = seed.latitude AND poi.longitude = seed.longitude
        CROSS JOIN (VALUES (0), (1), (2), (3), (4), (5), (6)) AS day_of_week(day_of_week)
    )
    MERGE catalog.POIOpeningHours AS target
    USING SeededHours AS source
    ON target.poi_id = source.poi_id AND target.day_of_week = source.day_of_week
    WHEN MATCHED THEN
        UPDATE SET open_time = source.open_time, close_time = source.close_time, is_closed = 0
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (poi_id, day_of_week, open_time, close_time, is_closed)
        VALUES (source.poi_id, source.day_of_week, source.open_time, source.close_time, 0);

    ;WITH SeededTags AS
    (
        SELECT DISTINCT poi.poi_id, tag.tag_id
        FROM @PoiSeed AS seed
        INNER JOIN catalog.POIs AS poi
            ON poi.name = seed.name AND poi.latitude = seed.latitude AND poi.longitude = seed.longitude
        CROSS APPLY STRING_SPLIT(seed.tags, ',') AS split
        INNER JOIN catalog.Tags AS tag ON tag.name = LTRIM(RTRIM(split.value))
    )
    MERGE catalog.POITagMap AS target
    USING SeededTags AS source
    ON target.poi_id = source.poi_id AND target.tag_id = source.tag_id
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (poi_id, tag_id) VALUES (source.poi_id, source.tag_id);

    -- Retire the hand-made TM-213 test POIs so they no longer enter itineraries.
    UPDATE catalog.POIs
    SET status = 'Inactive', updated_at = SYSUTCDATETIME()
    WHERE name IN (N'TM-213 Manual Culture POI', N'TM-213 Manual Nature POI');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
