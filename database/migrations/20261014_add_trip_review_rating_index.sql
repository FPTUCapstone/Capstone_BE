/*
TM-70 V2 Phase 1.3: cover confirmed Published Tour rating reads on the canonical
social.TripReviews table. This migration changes no Review lifecycle state and
does not classify or publish legacy social.Reviews rows.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'social.TripReviews', N'U') IS NULL
        THROW 51000, 'TM-70 rating index requires social.TripReviews.', 1;

    IF COL_LENGTH(N'social.TripReviews', N'tour_id') IS NULL
       OR COL_LENGTH(N'social.TripReviews', N'publication_status') IS NULL
       OR COL_LENGTH(N'social.TripReviews', N'overall_rating') IS NULL
       OR COL_LENGTH(N'social.TripReviews', N'booking_id') IS NULL
        THROW 51000, 'TM-70 rating index requires the canonical TripReview columns.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'social.TripReviews')
          AND name = N'IX_TripReviews_TourPublicationRating')
    BEGIN
        CREATE INDEX IX_TripReviews_TourPublicationRating
            ON social.TripReviews(tour_id, publication_status)
            INCLUDE (overall_rating, booking_id)
            WHERE tour_id IS NOT NULL;
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes AS i
        WHERE i.object_id = OBJECT_ID(N'social.TripReviews')
          AND i.name = N'IX_TripReviews_TourPublicationRating'
          AND i.is_unique = 0
          AND i.is_disabled = 0
          AND i.has_filter = 1
          AND REPLACE(REPLACE(i.filter_definition, N'[', N''), N']', N'') = N'(tour_id IS NOT NULL)'
          AND (
              SELECT COUNT(*)
              FROM sys.index_columns AS ic
              WHERE ic.object_id = i.object_id
                AND ic.index_id = i.index_id) = 4
          AND EXISTS (
              SELECT 1
              FROM sys.index_columns AS ic
              INNER JOIN sys.columns AS c
                  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                AND ic.key_ordinal = 1 AND ic.is_descending_key = 0
                AND c.name = N'tour_id')
          AND EXISTS (
              SELECT 1
              FROM sys.index_columns AS ic
              INNER JOIN sys.columns AS c
                  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                AND ic.key_ordinal = 2 AND ic.is_descending_key = 0
                AND c.name = N'publication_status')
          AND EXISTS (
              SELECT 1
              FROM sys.index_columns AS ic
              INNER JOIN sys.columns AS c
                  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                AND ic.key_ordinal = 0 AND ic.is_included_column = 1
                AND c.name = N'overall_rating')
          AND EXISTS (
              SELECT 1
              FROM sys.index_columns AS ic
              INNER JOIN sys.columns AS c
                  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                AND ic.key_ordinal = 0 AND ic.is_included_column = 1
                AND c.name = N'booking_id'))
        THROW 51000, 'TM-70 rating index exists with an incompatible shape.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
