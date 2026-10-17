/* Checks 20261015_seed_danang_city_real_pois.sql and 20261016_seed_danang_restaurants.sql.
   Fails (THROW) on the first expectation that does not hold; run with sqlcmd -b. */
SET NOCOUNT ON;

DECLARE @Actual INT;
DECLARE @Message NVARCHAR(400);

-- 11 active, planning-ready restaurants.
SELECT @Actual = COUNT(*)
FROM catalog.POIs AS poi
INNER JOIN catalog.POICategories AS category ON category.category_id = poi.category_id
WHERE category.name = N'Food & Drink'
  AND poi.status = 'Active'
  AND poi.source_url IS NOT NULL
  AND poi.verified_at IS NOT NULL;
IF @Actual <> 11
BEGIN
    SET @Message = CONCAT(N'Expected 11 planning-ready restaurants, found ', @Actual, N'.');
    THROW 50001, @Message, 1;
END;

-- Every restaurant has opening hours for all 7 days.
SELECT @Actual = COUNT(*)
FROM
(
    SELECT poi.poi_id
    FROM catalog.POIs AS poi
    INNER JOIN catalog.POICategories AS category ON category.category_id = poi.category_id
    LEFT JOIN catalog.POIOpeningHours AS hours ON hours.poi_id = poi.poi_id
    WHERE category.name = N'Food & Drink'
    GROUP BY poi.poi_id
    HAVING COUNT(hours.poi_id) <> 7
) AS incomplete;
IF @Actual <> 0
BEGIN
    SET @Message = CONCAT(@Actual, N' restaurant(s) without opening hours for all 7 days.');
    THROW 50002, @Message, 1;
END;

-- 11 Tripadvisor links: 7 restaurants and 4 demo attractions.
SELECT @Actual = COUNT(*) FROM catalog.POIs WHERE tripadvisor_location_id IS NOT NULL;
IF @Actual <> 11
BEGIN
    SET @Message = CONCAT(N'Expected 11 Tripadvisor links, found ', @Actual, N'.');
    THROW 50003, @Message, 1;
END;

-- The city POIs the demo needs exist (they are only inserted when their category exists).
SELECT @Actual = COUNT(*)
FROM catalog.POIs
WHERE name IN (N'Cầu Rồng (Dragon Bridge)', N'Bãi biển Mỹ Khê') AND status = 'Active';
IF @Actual <> 2
BEGIN
    SET @Message = CONCAT(N'Expected Cầu Rồng and Bãi biển Mỹ Khê to be active, found ', @Actual, N'.');
    THROW 50004, @Message, 1;
END;

-- Beaches open at 04:30 for sunrise.
SELECT @Actual = COUNT(*)
FROM catalog.POIs AS poi
INNER JOIN catalog.POICategories AS category ON category.category_id = poi.category_id
INNER JOIN catalog.POIOpeningHours AS hours ON hours.poi_id = poi.poi_id
WHERE category.name = N'Beach' AND hours.is_closed = 0 AND hours.open_time > '04:30';
IF @Actual <> 0
BEGIN
    SET @Message = CONCAT(@Actual, N' beach opening-hour row(s) open after 04:30.');
    THROW 50005, @Message, 1;
END;

-- The sunrise tags on Bãi biển Mỹ Khê.
SELECT @Actual = COUNT(*)
FROM catalog.POITagMap AS map
INNER JOIN catalog.POIs AS poi ON poi.poi_id = map.poi_id
INNER JOIN catalog.Tags AS tag ON tag.tag_id = map.tag_id
WHERE poi.name = N'Bãi biển Mỹ Khê' AND tag.name IN (N'Ngắm bình minh', N'Chèo SUP');
IF @Actual <> 2
BEGIN
    SET @Message = CONCAT(N'Expected 2 sunrise tags on Bãi biển Mỹ Khê, found ', @Actual, N'.');
    THROW 50006, @Message, 1;
END;

PRINT N'Review 2 seed data verified.';
