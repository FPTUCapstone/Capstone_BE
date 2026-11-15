SET XACT_ABORT ON;
SET NOCOUNT ON;
GO
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'trip.Incidents', N'U') IS NULL
       OR OBJECT_ID(N'catalog.POIs', N'U') IS NULL
        THROW 51014, 'UC-14 Tomorrow migration prerequisites are missing.', 1;

    IF COL_LENGTH(N'trip.Incidents', N'weather_basis') IS NULL
        ALTER TABLE trip.Incidents ADD weather_basis VARCHAR(30) NULL;
    IF COL_LENGTH(N'trip.Incidents', N'weather_at_utc') IS NULL
        ALTER TABLE trip.Incidents ADD weather_at_utc DATETIME2 NULL;
    IF COL_LENGTH(N'trip.Incidents', N'rain_intensity_mm_per_hour') IS NULL
        ALTER TABLE trip.Incidents ADD rain_intensity_mm_per_hour FLOAT NULL;
    IF COL_LENGTH(N'catalog.POIs', N'indoor_outdoor_reviewed_at_utc') IS NULL
        ALTER TABLE catalog.POIs ADD indoor_outdoor_reviewed_at_utc DATETIME2 NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE parent_object_id = OBJECT_ID(N'trip.Incidents')
                     AND name = N'CK_Incidents_WeatherAlertType')
    BEGIN
        DECLARE @oldCheck SYSNAME;
        SELECT @oldCheck = name FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'trip.Incidents')
          AND definition LIKE N'%incident_type%';
        IF @oldCheck IS NOT NULL
        BEGIN
            DECLARE @dropSql NVARCHAR(MAX) =
                N'ALTER TABLE trip.Incidents DROP CONSTRAINT ' + QUOTENAME(@oldCheck);
            EXEC(@dropSql);
        END;
        ALTER TABLE trip.Incidents WITH CHECK ADD CONSTRAINT CK_Incidents_WeatherAlertType
            CHECK (incident_type IN ('SevereWeather', 'WeatherAdvisory',
                'ScheduleDelay', 'RouteDeviation', 'POIClosure'));
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
