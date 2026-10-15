/*
POI name search ignores Vietnamese diacritics.

Travelers often type without accents ("Hoi An", "da nang", "my khe"). With the
default SQL_Latin1_General_CP1_CI_AS collation, LIKE on catalog.POIs.name only
matches when the accents are typed exactly ("Hội An"). Latin1_General_100_CI_AI is
case- and accent-insensitive and also folds Đ/đ to D/d, so both spellings match.

Rerunnable: the column is altered only while it still has another collation.
Auto-created column statistics block ALTER COLUMN, so they are dropped first
(SQL Server recreates them on demand). Any index or user statistic on the column
fails the migration atomically instead of being dropped silently.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'catalog.POIs', N'U') IS NULL
        THROW 51000, 'POI name collation migration requires catalog.POIs.', 1;

    IF EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'catalog.POIs')
          AND name = N'name'
          AND collation_name <> N'Latin1_General_100_CI_AI')
    BEGIN
        IF EXISTS (
            SELECT 1
            FROM sys.index_columns AS index_column
            INNER JOIN sys.columns AS column_info
                ON column_info.object_id = index_column.object_id
               AND column_info.column_id = index_column.column_id
            WHERE index_column.object_id = OBJECT_ID(N'catalog.POIs')
              AND column_info.name = N'name')
            THROW 51001, 'catalog.POIs.name is indexed; drop or rebuild the index before changing its collation.', 1;

        IF EXISTS (
            SELECT 1
            FROM sys.stats AS stat
            INNER JOIN sys.stats_columns AS stat_column
                ON stat_column.object_id = stat.object_id
               AND stat_column.stats_id = stat.stats_id
            INNER JOIN sys.columns AS column_info
                ON column_info.object_id = stat_column.object_id
               AND column_info.column_id = stat_column.column_id
            WHERE stat.object_id = OBJECT_ID(N'catalog.POIs')
              AND column_info.name = N'name'
              AND stat.auto_created = 0
              AND stat.user_created = 1)
            THROW 51002, 'catalog.POIs.name has user-created statistics; drop them before changing its collation.', 1;

        DECLARE @dropStatistics NVARCHAR(MAX) = N'';
        SELECT @dropStatistics = @dropStatistics
            + N'DROP STATISTICS catalog.POIs.' + QUOTENAME(stat.name) + N';'
        FROM sys.stats AS stat
        INNER JOIN sys.stats_columns AS stat_column
            ON stat_column.object_id = stat.object_id
           AND stat_column.stats_id = stat.stats_id
        INNER JOIN sys.columns AS column_info
            ON column_info.object_id = stat_column.object_id
           AND column_info.column_id = stat_column.column_id
        WHERE stat.object_id = OBJECT_ID(N'catalog.POIs')
          AND column_info.name = N'name'
          AND stat.auto_created = 1;

        IF LEN(@dropStatistics) > 0
            EXEC sys.sp_executesql @dropStatistics;

        ALTER TABLE catalog.POIs
            ALTER COLUMN name NVARCHAR(200) COLLATE Latin1_General_100_CI_AI NOT NULL;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
