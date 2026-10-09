-- Local development fixture for UC-59. Safe to rerun: each itinerary title is unique to this seed.
-- No password or usable login account is created. Do not include in the production db-init path.
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @now DATETIME2 = SYSUTCDATETIME();
DECLARE @category INT;
SELECT @category = category_id FROM catalog.POICategories WHERE name = N'UC59 Demo Places';
IF @category IS NULL
BEGIN
    INSERT catalog.POICategories(name, description)
    VALUES (N'UC59 Demo Places', N'Local active-trip monitoring fixtures');
    SET @category = CONVERT(INT, SCOPE_IDENTITY());
END;

IF NOT EXISTS (SELECT 1 FROM planning.Itineraries WHERE title = N'[UC59 DEMO] Hoi An Group Journey')
BEGIN
    DECLARE @host BIGINT, @companion BIGINT, @itineraryGroup BIGINT, @sessionGroup BIGINT;
    DECLARE @poiOldTown BIGINT, @poiRiver BIGINT, @incidentGroup BIGINT;

    INSERT dbo.Users(role, email, full_name, status)
    VALUES ('Traveler', N'uc59-group-host@example.invalid', N'UC59 Group Host', 'Active');
    SET @host = CONVERT(BIGINT, SCOPE_IDENTITY());
    INSERT dbo.Users(role, email, full_name, status)
    VALUES ('Traveler', N'uc59-group-member@example.invalid', N'UC59 Group Member', 'Active');
    SET @companion = CONVERT(BIGINT, SCOPE_IDENTITY());

    INSERT catalog.POIs(category_id, name, description, latitude, longitude)
    VALUES (@category, N'UC59 Hoi An Old Town', N'Local demo stop', 15.880100, 108.338000);
    SET @poiOldTown = CONVERT(BIGINT, SCOPE_IDENTITY());
    INSERT catalog.POIs(category_id, name, description, latitude, longitude)
    VALUES (@category, N'UC59 Hoai River Walk', N'Local demo stop', 15.878600, 108.329200);
    SET @poiRiver = CONVERT(BIGINT, SCOPE_IDENTITY());

    INSERT planning.Itineraries(traveler_user_id, source_type, title, status)
    VALUES (@host, 'Manual', N'[UC59 DEMO] Hoi An Group Journey', 'Active');
    SET @itineraryGroup = CONVERT(BIGINT, SCOPE_IDENTITY());
    INSERT planning.ItineraryItems(itinerary_id, sequence_no, poi_id, planned_arrival, planned_departure, stay_duration_minutes, item_kind, status)
    VALUES
        (@itineraryGroup, 1, @poiOldTown, DATEADD(HOUR, -4, @now), DATEADD(HOUR, -3, @now), 60, 'Visit', 'Visited'),
        (@itineraryGroup, 2, NULL, DATEADD(HOUR, -3, @now), DATEADD(HOUR, -2, @now), 60, 'Rest', 'Visited'),
        (@itineraryGroup, 3, @poiRiver, DATEADD(HOUR, 1, @now), DATEADD(HOUR, 2, @now), 60, 'Visit', 'Planned');

    INSERT trip.TripSessions(itinerary_id, requested_itinerary_id, traveler_user_id, start_idempotency_key, fsm_state, current_latitude, current_longitude, started_at, last_synced_at)
    VALUES (@itineraryGroup, @itineraryGroup, @host, NEWID(), 'Navigating', 15.879000, 108.335000, DATEADD(HOUR, -5, @now), DATEADD(MINUTE, -2, @now));
    SET @sessionGroup = CONVERT(BIGINT, SCOPE_IDENTITY());
    INSERT social.TravelGroups(itinerary_id, host_user_id, name)
    VALUES (@itineraryGroup, @host, N'UC59 Hoi An Walking Group');
    DECLARE @group BIGINT = CONVERT(BIGINT, SCOPE_IDENTITY());
    INSERT social.GroupMembers(group_id, user_id, location_sharing_enabled, status, joined_at)
    VALUES
        (@group, @host, 1, 'Active', DATEADD(DAY, -2, @now)),
        (@group, @companion, 0, 'Active', DATEADD(DAY, -1, @now));

    INSERT trip.TripStateHistory(session_id, from_state, to_state, reason, triggered_by, changed_at)
    VALUES
        (@sessionGroup, NULL, 'Navigating', N'Trip started', 'Traveler', DATEADD(HOUR, -5, @now)),
        (@sessionGroup, 'Navigating', 'Interrupted', N'Temporary route deviation', 'System', DATEADD(HOUR, -2, @now)),
        (@sessionGroup, 'Interrupted', 'Navigating', N'Alternate route accepted', 'Traveler', DATEADD(HOUR, -1, @now));

    INSERT trip.WeatherEvents(region_name, event_type, severity, description, valid_from, valid_to, source)
    VALUES (N'Hoi An', 'HeavyRain', 'Severe', N'Passing heavy rain during the walk', DATEADD(HOUR, -3, @now), DATEADD(HOUR, 1, @now), 'WeatherAPI');
    DECLARE @groupWeather BIGINT = CONVERT(BIGINT, SCOPE_IDENTITY());
    INSERT trip.Incidents(session_id, incident_type, weather_event_id, description, detected_at, resolved_at)
    VALUES (@sessionGroup, 'SevereWeather', @groupWeather, N'Rain cleared after a short stop', DATEADD(HOUR, -3, @now), DATEADD(HOUR, -2, @now));
    INSERT trip.Incidents(session_id, incident_type, description, detected_at, resolved_at)
    VALUES (@sessionGroup, 'RouteDeviation', N'Bridge detour, alternate walking route accepted', DATEADD(HOUR, -2, @now), DATEADD(HOUR, -1, @now));
    SET @incidentGroup = CONVERT(BIGINT, SCOPE_IDENTITY());
    INSERT trip.ReroutingEvents(incident_id, session_id, proposed_itinerary_snapshot, status, proposed_at, decided_at)
    VALUES (@incidentGroup, @sessionGroup, N'{"demo":true,"route":"bridge detour"}', 'Accepted', DATEADD(HOUR, -2, @now), DATEADD(HOUR, -1, @now));

    DECLARE @point INT = 0;
    WHILE @point < 8
    BEGIN
        INSERT trip.TripLocationLogs(session_id, latitude, longitude, recorded_at, synced_at, is_offline_captured)
        VALUES (@sessionGroup, 15.880100 - @point * 0.000150, 108.338000 - @point * 0.000400,
            DATEADD(MINUTE, -20 + @point * 2, @now), DATEADD(MINUTE, -19 + @point * 2, @now),
            CASE WHEN @point = 2 THEN 1 ELSE 0 END);
        SET @point += 1;
    END;
END;

IF NOT EXISTS (SELECT 1 FROM planning.Itineraries WHERE title = N'[UC59 DEMO] Da Nang Coastal Tour')
BEGIN
    DECLARE @traveler BIGINT, @operator BIGINT, @tour BIGINT, @destination BIGINT;
    DECLARE @poiBeach BIGINT, @poiMarble BIGINT, @itineraryTour BIGINT, @sessionTour BIGINT;

    INSERT dbo.Users(role, email, full_name, status)
    VALUES ('Traveler', N'uc59-tour-traveler@example.invalid', N'UC59 Tour Traveler', 'Active');
    SET @traveler = CONVERT(BIGINT, SCOPE_IDENTITY());
    INSERT dbo.Users(role, email, full_name, status)
    VALUES ('TourOperator', N'uc59-tour-operator@example.invalid', N'UC59 Tour Operator', 'Active');
    SET @operator = CONVERT(BIGINT, SCOPE_IDENTITY());
    INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no, approval_status)
    VALUES (@operator, N'UC59 Demo Tours', N'UC59-DEMO-TAX-20261001', N'UC59-DEMO-LICENSE', 'Approved');
    INSERT commerce.Tours(operator_user_id, title, base_price, duration_days, status)
    VALUES (@operator, N'UC59 Da Nang Coastal Tour', 500000, 2, 'Approved');
    SET @tour = CONVERT(BIGINT, SCOPE_IDENTITY());
    SELECT @destination = destination_id FROM catalog.Destinations WHERE name = N'Da Nang';
    IF @destination IS NULL
    BEGIN
        INSERT catalog.Destinations(name) VALUES (N'Da Nang');
        SET @destination = CONVERT(BIGINT, SCOPE_IDENTITY());
    END;
    INSERT commerce.TourDestinations(tour_id, destination_id, sequence_no)
    VALUES (@tour, @destination, 1);

    INSERT catalog.POIs(category_id, name, description, latitude, longitude)
    VALUES (@category, N'UC59 My Khe Beach', N'Local demo stop', 16.061200, 108.227700);
    SET @poiBeach = CONVERT(BIGINT, SCOPE_IDENTITY());
    INSERT catalog.POIs(category_id, name, description, latitude, longitude)
    VALUES (@category, N'UC59 Marble Mountains', N'Local demo stop', 16.003700, 108.263100);
    SET @poiMarble = CONVERT(BIGINT, SCOPE_IDENTITY());

    INSERT planning.Itineraries(traveler_user_id, source_type, source_tour_id, title, status)
    VALUES (@traveler, 'BookedTour', @tour, N'[UC59 DEMO] Da Nang Coastal Tour', 'Active');
    SET @itineraryTour = CONVERT(BIGINT, SCOPE_IDENTITY());
    INSERT planning.ItineraryItems(itinerary_id, sequence_no, poi_id, planned_arrival, planned_departure, stay_duration_minutes, item_kind, status)
    VALUES
        (@itineraryTour, 1, @poiBeach, DATEADD(HOUR, -6, @now), DATEADD(HOUR, -4, @now), 120, 'Visit', 'Visited'),
        (@itineraryTour, 2, NULL, DATEADD(HOUR, -4, @now), DATEADD(HOUR, -3, @now), 60, 'Rest', 'Skipped'),
        (@itineraryTour, 3, @poiMarble, DATEADD(HOUR, 2, @now), DATEADD(HOUR, 4, @now), 120, 'Visit', 'Planned');

    INSERT trip.TripSessions(itinerary_id, requested_itinerary_id, traveler_user_id, start_idempotency_key, fsm_state, current_latitude, current_longitude, started_at, last_synced_at)
    VALUES (@itineraryTour, @itineraryTour, @traveler, NEWID(), 'Exploring', 16.061200, 108.227700, DATEADD(HOUR, -7, @now), DATEADD(MINUTE, -3, @now));
    SET @sessionTour = CONVERT(BIGINT, SCOPE_IDENTITY());
    INSERT trip.TripStateHistory(session_id, from_state, to_state, reason, triggered_by, changed_at)
    VALUES
        (@sessionTour, NULL, 'Navigating', N'Tour started', 'Traveler', DATEADD(HOUR, -7, @now)),
        (@sessionTour, 'Navigating', 'Exploring', N'Arrived at My Khe Beach', 'System', DATEADD(HOUR, -6, @now));

    INSERT trip.WeatherEvents(region_name, event_type, severity, description, valid_from, valid_to, source)
    VALUES (N'Da Nang', 'HeavyRain', 'Severe', N'Coastal heavy rain alert', DATEADD(HOUR, -1, @now), DATEADD(HOUR, 3, @now), 'WeatherAPI');
    DECLARE @tourWeather BIGINT = CONVERT(BIGINT, SCOPE_IDENTITY());
    INSERT trip.Incidents(session_id, incident_type, weather_event_id, description, detected_at)
    VALUES (@sessionTour, 'SevereWeather', @tourWeather, N'Heavy rain affecting the next stop', DATEADD(MINUTE, -35, @now));
    DECLARE @incidentTour BIGINT = CONVERT(BIGINT, SCOPE_IDENTITY());
    INSERT trip.ReroutingEvents(incident_id, session_id, proposed_itinerary_snapshot, status, proposed_at)
    VALUES (@incidentTour, @sessionTour, N'{"demo":true,"proposal":"indoor alternative"}', 'Proposed', DATEADD(MINUTE, -30, @now));

    DECLARE @tourPoint INT = 0;
    WHILE @tourPoint < 8
    BEGIN
        INSERT trip.TripLocationLogs(session_id, latitude, longitude, recorded_at, synced_at, is_offline_captured)
        VALUES (@sessionTour, 16.058000 + @tourPoint * 0.000450, 108.226000 + @tourPoint * 0.000240,
            DATEADD(MINUTE, -24 + @tourPoint * 3, @now), DATEADD(MINUTE, -23 + @tourPoint * 3, @now),
            CASE WHEN @tourPoint = 3 THEN 1 ELSE 0 END);
        SET @tourPoint += 1;
    END;
END;

COMMIT TRANSACTION;

SELECT s.session_id, s.fsm_state, i.title
FROM trip.TripSessions AS s
JOIN planning.Itineraries AS i ON i.itinerary_id = s.itinerary_id
WHERE i.title IN (N'[UC59 DEMO] Hoi An Group Journey', N'[UC59 DEMO] Da Nang Coastal Tour')
ORDER BY s.session_id;
