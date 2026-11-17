/* UC-10 traveler feedback (decisions D12/D17/D19): two small per-traveler state tables the ranking reads through
   BehaviorProfileBuilder.
     - social.PoiFavorites     the heart "Yeu thich" on the place detail page; one row per traveler and place,
                               deleted when the heart is turned off. Read as an explicit like.
     - social.PoiVisitedMarks  a place removed from an itinerary as "Da di roi"; one row per traveler and place
                               with the latest date. No like and no dislike: the place only rests for a while.
   Additive, no existing row is touched, safe to rerun. Until it is applied the favorite endpoints fail, an
   "already visited" removal is not remembered (derived_visited_mark_failed is logged), and itinerary generation
   keeps working without both tables (behavior_table_unavailable is logged). */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
    THROW 51121, 'The UC-10 traveler feedback migration requires dbo.Users.', 1;
IF OBJECT_ID(N'catalog.POIs', N'U') IS NULL
    THROW 51122, 'The UC-10 traveler feedback migration requires catalog.POIs.', 1;

IF OBJECT_ID(N'social.PoiFavorites', N'U') IS NULL
BEGIN
    CREATE TABLE social.PoiFavorites (
        favorite_id BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_PoiFavorites PRIMARY KEY,
        traveler_user_id BIGINT NOT NULL,
        poi_id BIGINT NOT NULL,
        created_at_utc DATETIME2 NOT NULL
            CONSTRAINT DF_PoiFavorites_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_PoiFavorites_Traveler_POI
            UNIQUE (traveler_user_id, poi_id),
        CONSTRAINT FK_PoiFavorites_Traveler
            FOREIGN KEY (traveler_user_id) REFERENCES dbo.Users(user_id) ON DELETE NO ACTION,
        CONSTRAINT FK_PoiFavorites_POI
            FOREIGN KEY (poi_id) REFERENCES catalog.POIs(poi_id) ON DELETE NO ACTION
    );
END;

IF OBJECT_ID(N'social.PoiVisitedMarks', N'U') IS NULL
BEGIN
    CREATE TABLE social.PoiVisitedMarks (
        visited_mark_id BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_PoiVisitedMarks PRIMARY KEY,
        traveler_user_id BIGINT NOT NULL,
        poi_id BIGINT NOT NULL,
        marked_at_utc DATETIME2 NOT NULL
            CONSTRAINT DF_PoiVisitedMarks_MarkedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_PoiVisitedMarks_Traveler_POI
            UNIQUE (traveler_user_id, poi_id),
        CONSTRAINT FK_PoiVisitedMarks_Traveler
            FOREIGN KEY (traveler_user_id) REFERENCES dbo.Users(user_id) ON DELETE NO ACTION,
        CONSTRAINT FK_PoiVisitedMarks_POI
            FOREIGN KEY (poi_id) REFERENCES catalog.POIs(poi_id) ON DELETE NO ACTION
    );
END;

COMMIT TRANSACTION;
