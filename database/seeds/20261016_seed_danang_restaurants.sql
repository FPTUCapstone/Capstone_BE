/*
   REVIEW2 (D2, D6, D10): real Da Nang / Hội An restaurants, Tripadvisor links and sunrise data.

   - Category "Food & Drink" (shown as "Ẩm thực"). Itinerary generation never schedules it on its
     own; travelers add a restaurant to an itinerary themselves.
   - estimated_visit_cost is the average spend per person, not a ticket price.
   - tripadvisor_location_id: matched once on 2026-10-16 with the Tripadvisor Terra location search
     (name + address + coordinates checked). Only the id is stored; ratings and reviews are read live.
   - Coordinates: Mapbox Search Box (2026-10-16), checked against street and ward.

   Opening hours / spend sources (opened 2026-10-16):
   - Michelin Guide lists on the Da Nang tourism portal (danangfantasticity.com), 2024 and 2025 editions.
   - Madame Lân: madamelan.vn. VinWonders Wonderpedia guides (2025). VnExpress food tour (2026-06).
   - Conservative choices where sources disagree:
     * Mì Quảng Bà Mua: published hours vary (06:30-21:30 vs 07:00-20:00) -> 07:00-20:00.
     * Cơm gà Bà Buội: one guide reports an afternoon break -> lunch window 10:30-14:00 only.
     * Hải sản Bé Ni 2: only per-dish prices are published -> spend left NULL ("Chưa có thông tin").
   Average visit minutes and indoor/outdoor are TripMate planning estimates.

   Also (D10): beaches are planned from 04:30 (public beaches have no published hours; sunrise is
   about 05:30 in October) and Bãi biển Mỹ Khê gets the tags "Ngắm bình minh" and "Chèo SUP".
*/
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @VerifiedAtUtc DATETIME2 = '2026-10-16T00:00:00';

    MERGE catalog.POICategories AS target
    USING (VALUES
        (N'Food & Drink', N'Restaurants and cafés travelers add to their itinerary themselves.'))
        AS source(name, description)
    ON target.name = source.name
    WHEN MATCHED THEN
        UPDATE SET description = source.description
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (name, description) VALUES (source.name, source.description);

    MERGE catalog.Tags AS target
    USING (VALUES (N'Ngắm bình minh'), (N'Chèo SUP')) AS source(name)
    ON target.name = source.name
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (name) VALUES (source.name);

    DECLARE @PoiSeed TABLE
    (
        -- Same collation as catalog.POIs.name (20261015_poi_name_accent_insensitive.sql).
        name NVARCHAR(200) COLLATE Latin1_General_100_CI_AI NOT NULL,
        latitude DECIMAL(9, 6) NOT NULL,
        longitude DECIMAL(9, 6) NOT NULL,
        address NVARCHAR(400) NOT NULL,
        indoor_outdoor VARCHAR(10) NOT NULL,
        average_visit_duration_minutes INT NOT NULL,
        estimated_visit_cost DECIMAL(12, 2) NULL,
        open_time TIME NOT NULL,
        close_time TIME NOT NULL,
        source_url NVARCHAR(500) NOT NULL,
        tripadvisor_location_id BIGINT NULL
    );

    INSERT @PoiSeed VALUES
        -- Hải Châu
        (N'Nhà hàng Madame Lân', 16.081455, 108.223324, N'04 Bạch Đằng, phường Hải Châu, Đà Nẵng',
            'Mixed', 75, 152500, '06:30', '21:30', N'https://madamelan.vn/', 2724207),
        (N'Mỳ Quảng Quê Xưa', 16.046888, 108.221160, N'165-167 Núi Thành, Hải Châu, Đà Nẵng',
            'Indoor', 45, 57500, '06:00', '22:30',
            N'https://danangfantasticity.com/discovery/list-of-michelin-guide-dining-establishments-in-da-nang-2025', 9739777),
        (N'Bánh xèo Bà Dưỡng', 16.058865, 108.216435, N'K280/23 Hoàng Diệu, phường Hải Châu, Đà Nẵng',
            'Mixed', 60, 50000, '09:30', '21:30',
            N'https://vinwonders.com/vi/wonderpedia/news/15-quan-banh-xeo-da-nang/', 6961132),
        (N'Mì Quảng Bà Mua', 16.066019, 108.219144, N'19-21 Trần Bình Trọng, phường Hải Châu, Đà Nẵng',
            'Indoor', 45, 40000, '07:00', '20:00',
            N'https://vnexpress.net/food-tour-da-nang-200k-5085110.html', 8064453),
        (N'Bún chả cá 109', 16.074418, 108.220757, N'109 Nguyễn Chí Thanh, phường Hải Châu, Đà Nẵng',
            'Indoor', 45, 50000, '06:30', '22:30',
            N'https://vinwonders.com/vi/wonderpedia/news/an-la-ghien-voi-10-quan-bun-cha-ca-da-nang-sieu-hut-khach/', 17158035),
        -- Sơn Trà
        (N'Hải sản Mộc Quán', 16.064016, 108.241554, N'26 Tô Hiến Thành, Sơn Trà, Đà Nẵng',
            'Mixed', 75, 185000, '10:30', '22:30',
            N'https://danangfantasticity.com/discovery/list-of-michelin-guide-dining-establishments-in-da-nang-2025', NULL),
        (N'Hải sản Bé Ni 2', 16.066800, 108.245089, N'228 Võ Nguyên Giáp, Sơn Trà, Đà Nẵng',
            'Mixed', 75, NULL, '00:00', '23:59',
            N'https://sunparadiseland.com/en/tin-tuc/top-7-michelin-recommended-restaurants-in-da-nang-attracting-food-connoisseurs-in-2025-6212', NULL),
        (N'Hải sản Năm Đảnh', 16.102249, 108.253336, N'K139/H59/38 Trần Quang Khải, Thọ Quang, Sơn Trà, Đà Nẵng',
            'Mixed', 75, 80000, '10:00', '22:00',
            N'https://danangfantasticity.com/en/event-highlights/da-nang-newly-covered-in-this-second-edition-contributes-36-entries-to-the-the-2024-michelin-guide-including-1-one-michelin-star', NULL),
        -- Ngũ Hành Sơn
        (N'Bánh Xèo 76', 16.041647, 108.240083, N'85A Lê Văn Hưu, Ngũ Hành Sơn, Đà Nẵng',
            'Mixed', 45, 30000, '10:00', '21:00',
            N'https://danangfantasticity.com/discovery/list-of-michelin-guide-dining-establishments-in-da-nang-2025', NULL),
        -- Hội An
        (N'Cơm gà Bà Buội', 15.878479, 108.330403, N'22 Phan Châu Trinh, phường Hội An, Đà Nẵng',
            'Indoor', 45, 55000, '10:30', '14:00',
            N'https://vinwonders.com/vi/wonderpedia/news/com-ga-hoi-an-top-10-quan-an-pho-hoi/', 28755940),
        (N'Morning Glory Signature', 15.875959, 108.326191, N'41 Nguyễn Phúc Chu (tầng 2), phường Hội An, Đà Nẵng',
            'Mixed', 75, 200000, '10:30', '22:30',
            N'https://hoiandaytrip.com/best-restaurants-in-hoi-an-old-town-with-a-view/', 13355496);

    MERGE catalog.POIs AS target
    USING
    (
        SELECT category.category_id, seed.*
        FROM @PoiSeed AS seed
        INNER JOIN catalog.POICategories AS category ON category.name = N'Food & Drink'
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
            has_shelter = 1,
            status = 'Active',
            estimated_visit_cost = source.estimated_visit_cost,
            source_url = source.source_url,
            tripadvisor_location_id = source.tripadvisor_location_id,
            verified_at = @VerifiedAtUtc,
            updated_at = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (category_id, name, latitude, longitude, address, indoor_outdoor,
                avg_visit_duration_minutes, has_shelter, status, estimated_visit_cost,
                source_url, tripadvisor_location_id, verified_at, created_at, updated_at)
        VALUES (source.category_id, source.name, source.latitude, source.longitude,
                source.address, source.indoor_outdoor, source.average_visit_duration_minutes,
                1, 'Active', source.estimated_visit_cost, source.source_url,
                source.tripadvisor_location_id, @VerifiedAtUtc, SYSUTCDATETIME(), SYSUTCDATETIME());

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

    -- Tripadvisor ids of the attractions used in the Review 2 demo (matched the same way).
    UPDATE poi
    SET tripadvisor_location_id = link.location_id, updated_at = SYSUTCDATETIME()
    FROM catalog.POIs AS poi
    INNER JOIN (VALUES
        (N'Marble Mountains - Thuy Son Peak', CAST(454980 AS BIGINT)),
        (N'Da Nang Museum of Cham Sculpture', CAST(454979 AS BIGINT)),
        (N'Cầu Rồng (Dragon Bridge)', CAST(6612108 AS BIGINT)),
        (N'Bãi biển Mỹ Khê', CAST(7687457 AS BIGINT))) AS link(name, location_id)
        ON poi.name = link.name
    WHERE poi.tripadvisor_location_id IS NULL OR poi.tripadvisor_location_id <> link.location_id;

    -- Sunrise: beaches are planned from 04:30 instead of the 05:00 planning window.
    UPDATE hours
    SET open_time = '04:30'
    FROM catalog.POIOpeningHours AS hours
    INNER JOIN catalog.POIs AS poi ON poi.poi_id = hours.poi_id
    INNER JOIN catalog.POICategories AS category ON category.category_id = poi.category_id
    WHERE category.name = N'Beach' AND hours.is_closed = 0 AND hours.open_time = '05:00';

    MERGE catalog.POITagMap AS target
    USING
    (
        SELECT poi.poi_id, tag.tag_id
        FROM catalog.POIs AS poi
        CROSS JOIN catalog.Tags AS tag
        WHERE poi.name = N'Bãi biển Mỹ Khê' AND tag.name IN (N'Ngắm bình minh', N'Chèo SUP')
    ) AS source
    ON target.poi_id = source.poi_id AND target.tag_id = source.tag_id
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (poi_id, tag_id) VALUES (source.poi_id, source.tag_id);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
