/*
    TM-71 (UC-25 Tour Recommendations) follow-up: operator-declared pace/transport/diet
    attributes on commerce.Tours, so tour-recommendation scoring can compare a tour against
    the traveler's TravelerProfiles.travel_pace, preferred_transport_mode and
    food_preferences_json instead of ignoring them. Three new nullable columns; existing tours
    get NULL and are unaffected (scoring treats each dimension as inactive when absent).

    Idempotent: guarded, so a re-run on an upgraded database is a no-op.
*/
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF COL_LENGTH(N'commerce.Tours', N'pace_level') IS NULL
    ALTER TABLE commerce.Tours ADD pace_level VARCHAR(20) NULL;
GO

IF COL_LENGTH(N'commerce.Tours', N'supported_transport_modes_json') IS NULL
    ALTER TABLE commerce.Tours ADD supported_transport_modes_json NVARCHAR(1000) NULL;
GO

IF COL_LENGTH(N'commerce.Tours', N'dietary_accommodations_json') IS NULL
    ALTER TABLE commerce.Tours ADD dietary_accommodations_json NVARCHAR(1000) NULL;
GO
