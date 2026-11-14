/* UC-10 item 6 (AI behavior learning, D7): the backend now derives behavior signals from what travelers do and
   appends them to social.RecommendationBehaviorEvents with three new capture sources:
     - ItineraryEdit      Dislike (stop removed) / Like (stop added) when an itinerary is adjusted
     - Navigation         Like (stop reached) / Skip (stop skipped) during live navigation
     - SchedulingRequest  Like for each POI marked mandatory when the request is created (no itinerary yet)
   Only two CHECK constraints change. No rows are touched; safe to rerun (apply-schema.sh reruns every script,
   and 20260929_add_recommendation_behavior_events.sql accepts the shape left here). Run before deploying the BE
   branch: until then the inserts fail the CHECK, are logged (derived_behavior_signal_failed) and the
   traveler's action still succeeds. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'social.RecommendationBehaviorEvents', N'U') IS NULL
    THROW 51111, 'social.RecommendationBehaviorEvents must exist before applying the UC-10 behavior source migration.', 1;

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')
      AND name = N'CK_RecommendationBehaviorEvents_SourceContext'
      AND definition NOT LIKE N'%SchedulingRequest%')
    ALTER TABLE social.RecommendationBehaviorEvents
        DROP CONSTRAINT CK_RecommendationBehaviorEvents_SourceContext;

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')
      AND name = N'CK_RecommendationBehaviorEvents_TypeSource'
      AND definition NOT LIKE N'%Navigation%')
    ALTER TABLE social.RecommendationBehaviorEvents
        DROP CONSTRAINT CK_RecommendationBehaviorEvents_TypeSource;

IF OBJECT_ID(N'social.CK_RecommendationBehaviorEvents_SourceContext', N'C') IS NULL
    ALTER TABLE social.RecommendationBehaviorEvents WITH CHECK
        ADD CONSTRAINT CK_RecommendationBehaviorEvents_SourceContext CHECK (
            (source IN ('Itinerary','ItineraryEdit','Navigation')
                AND itinerary_id IS NOT NULL AND was_mandatory IS NOT NULL)
            OR (source IN ('Explore','PoiDetail')
                AND itinerary_id IS NULL AND was_mandatory IS NULL)
            OR (source = 'SchedulingRequest'
                AND itinerary_id IS NULL AND was_mandatory = 1));

IF OBJECT_ID(N'social.CK_RecommendationBehaviorEvents_TypeSource', N'C') IS NULL
    ALTER TABLE social.RecommendationBehaviorEvents WITH CHECK
        ADD CONSTRAINT CK_RecommendationBehaviorEvents_TypeSource CHECK (
            event_type IN ('Like','Dislike')
            OR (event_type = 'Skip' AND source IN ('Itinerary','Navigation') AND itinerary_id IS NOT NULL)
            OR (event_type = 'Reorder' AND source = 'Itinerary' AND itinerary_id IS NOT NULL));

IF (SELECT COUNT(*) FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')
      AND name IN (N'CK_RecommendationBehaviorEvents_SourceContext', N'CK_RecommendationBehaviorEvents_TypeSource')
      AND is_disabled = 0 AND is_not_trusted = 0) <> 2
    THROW 51112, 'UC-10 behavior source constraints are not enabled and trusted.', 1;

COMMIT TRANSACTION;
