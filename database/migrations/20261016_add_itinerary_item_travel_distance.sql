/*
REVIEW2 (D3): road distance of each itinerary leg, in metres.

planning.ItineraryItems already stores the minutes to the next stop
(travel_duration_to_next_minutes) and the transport mode of that leg
(transport_mode_to_next). This adds the matching distance so clients can show
"9,4 km · 22 phút". Existing itineraries keep NULL and clients hide the distance.

Rerunnable: the column is added only when it is missing.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'planning.ItineraryItems', N'U') IS NULL
    THROW 51000, 'REVIEW2 travel distance requires planning.ItineraryItems.', 1;

IF COL_LENGTH(N'planning.ItineraryItems', N'travel_distance_to_next_meters') IS NULL
    ALTER TABLE planning.ItineraryItems ADD travel_distance_to_next_meters INT NULL
        CONSTRAINT CK_ItineraryItems_TravelDistance CHECK (travel_distance_to_next_meters >= 0);

COMMIT TRANSACTION;
