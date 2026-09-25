/*
   Curated planning-ready Da Nang POIs for UC-10.

   Coordinates/categories: OpenStreetMap-derived reference data. This seed does
   not import photos, reviews, or Google Maps/Places content.
   Opening hours and reference admission costs: Da Nang Tourism Promotion Center,
   "Plan Your Da Nang Journey 2026 – Updated Admission Prices for Attractions
   and Tourist Sites", published 2026-01-22:
   https://danangfantasticity.com/en/news/plan-your-da-nang-journey-2026-updated-admission-prices-for-attractions-and-tourist-sites

   Average visit durations are TripMate planning estimates. They are deliberately
   separate from the verified opening-hour/cost facts and are shown to Travelers
   as estimates by the generation flow.
*/
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @VerifiedAtUtc DATETIME2 = '2026-09-14T00:00:00';
    DECLARE @SourceUrl NVARCHAR(500) =
        N'https://danangfantasticity.com/en/news/plan-your-da-nang-journey-2026-updated-admission-prices-for-attractions-and-tourist-sites';

    MERGE catalog.POICategories AS target
    USING (VALUES
        (N'Museum', N'Curated museums suitable for self-guided visits.'),
        (N'Natural attraction', N'Curated natural and heritage attractions.'),
        (N'Theme park', N'Curated full-day entertainment attractions.'),
        (N'Water park', N'Curated water-park attractions.')) AS source(name, description)
    ON target.name = source.name
    WHEN MATCHED THEN
        UPDATE SET description = source.description
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (name, description) VALUES (source.name, source.description);

    DECLARE @PoiSeed TABLE
    (
        name NVARCHAR(200) NOT NULL,
        category_name NVARCHAR(100) NOT NULL,
        latitude DECIMAL(9, 6) NOT NULL,
        longitude DECIMAL(9, 6) NOT NULL,
        address NVARCHAR(400) NULL,
        indoor_outdoor VARCHAR(10) NOT NULL,
        average_visit_duration_minutes INT NOT NULL,
        has_shelter BIT NOT NULL,
        estimated_visit_cost DECIMAL(12, 2) NULL,
        open_time TIME NOT NULL,
        close_time TIME NOT NULL
    );

    INSERT @PoiSeed
    (
        name,
        category_name,
        latitude,
        longitude,
        address,
        indoor_outdoor,
        average_visit_duration_minutes,
        has_shelter,
        estimated_visit_cost,
        open_time,
        close_time
    )
    VALUES
        (N'Da Nang Museum of Cham Sculpture', N'Museum', 16.040393, 108.223058,
            N'02 September 2 Street, Hai Chau, Da Nang', 'Indoor', 90, 1, 60000, '07:30', '17:00'),
        (N'Da Nang Museum', N'Museum', 16.071218, 108.225226,
            N'42 Bach Dang Street, Hai Chau, Da Nang', 'Indoor', 90, 1, 50000, '08:00', '17:00'),
        (N'Da Nang Fine Arts Museum', N'Museum', 16.073538, 108.219823,
            N'78 Le Duan Street, Hai Chau, Da Nang', 'Indoor', 75, 1, 20000, '08:00', '17:00'),
        (N'Military Zone 5 Museum', N'Museum', 16.032273, 108.216323,
            N'01 Duy Tan Street, Hai Chau, Da Nang', 'Indoor', 90, 1, 60000, '08:00', '16:30'),
        (N'Marble Mountains - Thuy Son Peak', N'Natural attraction', 16.003759, 108.263876,
            N'Huyen Tran Cong Chua Street, Ngu Hanh Son, Da Nang', 'Outdoor', 120, 0, 40000, '07:00', '17:00'),
        (N'Am Phu Cave', N'Natural attraction', 16.004488, 108.264164,
            N'Marble Mountains, Ngu Hanh Son, Da Nang', 'Mixed', 45, 1, 20000, '07:00', '17:00'),
        (N'Sun World Ba Na Hills', N'Theme park', 15.997596, 107.988215,
            N'An Son Village, Hoa Vang, Da Nang', 'Mixed', 300, 1, 1000000, '08:00', '22:00'),
        (N'Than Tai Hot Spring Park', N'Natural attraction', 15.987264, 107.986255,
            N'Hoa Phu Commune, Hoa Vang, Da Nang', 'Mixed', 240, 1, 490000, '08:30', '17:30'),
        (N'Mikazuki 365 Water Park', N'Water park', 16.125205, 108.120618,
            N'Nguyen Tat Thanh Street, Lien Chieu, Da Nang', 'Indoor', 240, 1, 350000, '09:00', '19:00'),
        (N'Hoa Phu Thanh Tourist Area', N'Natural attraction', 15.973945, 108.090233,
            N'Hoa Phu Commune, Hoa Vang, Da Nang', 'Outdoor', 180, 1, 150000, '08:00', '16:30'),
        (N'Suoi Luong Ecotourism Area', N'Natural attraction', 16.135173, 108.102806,
            N'Hai Van Ward, Da Nang', 'Outdoor', 180, 1, 80000, '08:00', '17:00'),
        (N'Dong Dinh Museum', N'Museum', 16.112646, 108.278505,
            N'Hoang Sa Street, Son Tra, Da Nang', 'Mixed', 90, 1, 35000, '07:00', '17:00');

    MERGE catalog.POIs AS target
    USING
    (
        SELECT
            category.category_id,
            seed.name,
            seed.latitude,
            seed.longitude,
            seed.address,
            seed.indoor_outdoor,
            seed.average_visit_duration_minutes,
            seed.has_shelter,
            seed.estimated_visit_cost,
            seed.open_time,
            seed.close_time
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
            source_url = @SourceUrl,
            verified_at = @VerifiedAtUtc,
            updated_at = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT
        (
            category_id,
            name,
            latitude,
            longitude,
            address,
            indoor_outdoor,
            avg_visit_duration_minutes,
            has_shelter,
            status,
            estimated_visit_cost,
            source_url,
            verified_at,
            created_at,
            updated_at
        )
        VALUES
        (
            source.category_id,
            source.name,
            source.latitude,
            source.longitude,
            source.address,
            source.indoor_outdoor,
            source.average_visit_duration_minutes,
            source.has_shelter,
            'Active',
            source.estimated_visit_cost,
            @SourceUrl,
            @VerifiedAtUtc,
            SYSUTCDATETIME(),
            SYSUTCDATETIME()
        );

    ;WITH SeededHours AS
    (
        SELECT
            poi.poi_id,
            day_of_week.day_of_week,
            seed.open_time,
            seed.close_time
        FROM @PoiSeed AS seed
        INNER JOIN catalog.POIs AS poi
            ON poi.name = seed.name
           AND poi.latitude = seed.latitude
           AND poi.longitude = seed.longitude
        CROSS JOIN (VALUES (0), (1), (2), (3), (4), (5), (6)) AS day_of_week(day_of_week)
    )
    MERGE catalog.POIOpeningHours AS target
    USING SeededHours AS source
    ON target.poi_id = source.poi_id
       AND target.day_of_week = source.day_of_week
    WHEN MATCHED THEN
        UPDATE SET
            open_time = source.open_time,
            close_time = source.close_time,
            is_closed = 0
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (poi_id, day_of_week, open_time, close_time, is_closed)
        VALUES (source.poi_id, source.day_of_week, source.open_time, source.close_time, 0);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
