/* Weather Readiness: meeting coordinates and weather-critical itinerary items.
   Idempotent migration for pre-trip weather readiness monitoring. */
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'commerce.TourSchedules', N'U') IS NULL
        THROW 51000, 'Weather readiness requires commerce.TourSchedules.', 1;

    IF OBJECT_ID(N'commerce.TourItineraryItems', N'U') IS NULL
        THROW 51000, 'Weather readiness requires commerce.TourItineraryItems.', 1;

    IF COL_LENGTH(N'commerce.TourSchedules', N'meeting_latitude') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commerce.TourSchedules ADD meeting_latitude DECIMAL(9,6) NULL;';

    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns AS column_info
        INNER JOIN sys.types AS type_info ON type_info.user_type_id = column_info.user_type_id
        WHERE column_info.object_id = OBJECT_ID(N'commerce.TourSchedules')
          AND column_info.name = N'meeting_latitude'
          AND type_info.name = N'decimal'
          AND column_info.precision = 9
          AND column_info.scale = 6
          AND column_info.is_nullable = 1)
        THROW 51000, 'Weather readiness meeting_latitude column has an incompatible shape.', 1;

    IF COL_LENGTH(N'commerce.TourSchedules', N'meeting_longitude') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commerce.TourSchedules ADD meeting_longitude DECIMAL(9,6) NULL;';

    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns AS column_info
        INNER JOIN sys.types AS type_info ON type_info.user_type_id = column_info.user_type_id
        WHERE column_info.object_id = OBJECT_ID(N'commerce.TourSchedules')
          AND column_info.name = N'meeting_longitude'
          AND type_info.name = N'decimal'
          AND column_info.precision = 9
          AND column_info.scale = 6
          AND column_info.is_nullable = 1)
        THROW 51000, 'Weather readiness meeting_longitude column has an incompatible shape.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_TourSchedules_WeatherMeetingCoordinates'
          AND parent_object_id = OBJECT_ID(N'commerce.TourSchedules'))
    BEGIN
        EXEC sys.sp_executesql N'
            ALTER TABLE commerce.TourSchedules WITH CHECK
            ADD CONSTRAINT CK_TourSchedules_WeatherMeetingCoordinates CHECK (
                (meeting_latitude IS NULL AND meeting_longitude IS NULL)
                OR (meeting_latitude IS NOT NULL AND meeting_longitude IS NOT NULL
                    AND meeting_latitude BETWEEN -90 AND 90
                    AND meeting_longitude BETWEEN -180 AND 180));';
    END;

    ALTER TABLE commerce.TourSchedules WITH CHECK
        CHECK CONSTRAINT CK_TourSchedules_WeatherMeetingCoordinates;

    IF COL_LENGTH(N'commerce.TourItineraryItems', N'is_required_for_weather') IS NULL
        EXEC sys.sp_executesql N'ALTER TABLE commerce.TourItineraryItems ADD is_required_for_weather BIT NULL;';

    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns AS column_info
        INNER JOIN sys.types AS type_info ON type_info.user_type_id = column_info.user_type_id
        WHERE column_info.object_id = OBJECT_ID(N'commerce.TourItineraryItems')
          AND column_info.name = N'is_required_for_weather'
          AND type_info.name = N'bit'
          AND column_info.is_nullable = 1)
        THROW 51000, 'Weather readiness is_required_for_weather column has an incompatible shape.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
