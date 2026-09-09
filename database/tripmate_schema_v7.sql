/* =====================================================================
   TripMate — Smart Travel Planner & Travel Services Platform
   Physical Database Design v7 (SQL Server / T-SQL)

   v7 changes vs v6:

     1. dbo.Messages.message_type CHECK constraint widened to add 'Alert'
        and 'PushNotification' — the real SRS §5.3 Application Messages
        List uses "Alert pop-up" and "Push notification" as distinct
        message types alongside the five already supported
        (InLine/ToastMessage/RedUnderTextbox/Modal/BottomSheet). "Alert
        pop-up" and "Push notification" map to the new 'Alert' and
        'PushNotification' values; "Confirmation pop-up" and "Modal
        dialog" both map to the existing 'Modal' value (no new value
        needed for those two).
     2. dbo.Messages seed data replaced: the v6 seed rows were 5
        illustrative placeholder rows copied from the SRS template before
        §5.3 was authored for TripMate. Replaced with the full, real
        130-row Application Messages List (MSG01–MSG130) from the
        authored SRS §5.3.

   v6 changes vs v5:

     1. REMOVED dbo.BusinessRules, dbo.Screens, dbo.ScreenMessageTriggers —
        this mapping (which screen shows which message, enforcing which
        business rule) is documentation, not a runtime query the app makes,
        so it moved to database/screen_message_business_rule_mapping.md
        instead. dbo.Messages is KEPT — message_code -> content_template
        lookup is a real runtime need.
     2. RE-ADDED dbo.LandingPageContents (removed in v4) — UC-66 "Update
        Landing Page Content" needs somewhere to write to; hard-coding the
        landing page in the frontend means every copy change needs a
        redeploy, which defeats having an Admin "update landing page"
        function at all.

   v5 changes vs v4:

     1. NEW dbo.BusinessRules, dbo.Screens, dbo.Messages,
        dbo.ScreenMessageTriggers (section 10) — a message catalog that
        ties a UI message (SRS §5.3 Application Messages List) to the
        screen that shows it and, where applicable, the business rule
        it enforces (SRS §5.1 Business Rules). ScreenMessageTriggers is
        the actual "trigger" table: one row = "on Screen X, Event Y
        fires Message Z (enforcing Rule R, if any)".

        IMPORTANT: SRS §5.1 and §5.3 are still template placeholder
        content (generic "cafeteria/asset" example rows, not authored
        for TripMate). Seed data below is illustrative only — copies
        the literal placeholder text from the spec so the table
        structure is demonstrable, NOT real TripMate copy. Replace
        with actual rule/message text once §5.1/§5.3 are written for
        TripMate, before relying on this table in the app.

   v4 changes vs v3 (design review pass):

     1. REMOVED dbo.OtpCodes — OTP lives a few minutes and is deleted on
        successful verification, so it belongs in a cache (Redis) with a
        TTL, not a relational table. No FK referenced this table.
     2. REMOVED dbo.LandingPageContents — not core to the system and
        deployed once. NOTE: this means UC-66 "Update Landing Page
        Content" can no longer be fulfilled through the database; landing
        page content becomes hard-coded in the frontend and requires a
        redeploy to change. Flagging this trade-off explicitly.
     3. NEW catalog.FavoritePOIs — lets a Traveler bookmark a POI.
     4. social.Reviews — added scenic_rating/photo_rating (only when
        target_type = 'POI'). catalog.POIs.scenic_score/photo_rating are
        Admin-seeded on POI creation (UC-52) as a cold-start value, then
        intended to be refreshed periodically as an average of these
        per-review ratings once enough reviews exist (batch job, not yet
        implemented — application-layer concern).
     5. commerce.BookingParticipants — added id_document_type,
        id_document_number, nationality for premium/international tours
        that require real identity verification. Contains PII — per
        Business Rule BR-33 (SRS §5.1), must be encrypted in transit and
        at rest.
     6. commerce.TourSchedules — added meeting_point (a specific dated
        departure's gathering point can differ from the tour's own POI
        stops).
     7. planning.ItineraryItems — added estimated_cost (per-stop CSP
        budget tracking) and recommendation_reason (explains why the CSP
        engine chose this stop).
     8. planning.SchedulingRequests — added search_radius_km ("explore
        around this area" requests).
     9. planning.Itineraries — added share_token + is_public, backing the
        "Shared Itinerary View" screen (public read-only itinerary link).

   v3 changes vs v2:

     1. rental.* (VehicleRentalProviders, Vehicles, VehicleBookings)
        replaced by commercial.* (ServiceProviders, Services,
        ServiceBookings, ServiceClickLogs) — one generic pattern shared
        by vehicle rental, hotel and restaurant partners instead of a
        separate 3-table set per vertical. See section 8 below.
     2. commercial.ServiceBookings.commission_amount +
        commercial.ServiceClickLogs — models TripMate's affiliate/
        commission revenue from 3rd-party service providers, which is
        a different revenue path from the Tour Operator commission
        already modeled via OperatorProfiles.commission_rate + Payouts.
     3. Documentation-only clarifications (no column changes):
          - trip.WeatherEvents is a short-retention cache/audit table,
            not a permanent archive — pair it with a purge job, not a
            schema change.
          - trip.OfflineSyncBatches/Items only reconcile data the
            mobile device COLLECTED while offline (GPS/state/check-in).
            Downloading POI/itinerary data FOR offline use (UC-16) is
            pure client-side caching and needs no backend table at all.

   v2 merged tripmate_schema.sql (schema-per-domain, semantic PKs,
   unified Itinerary, Vehicle Rental) with fixes ported from
   tripmate_schema_trial.dbml — see database/schema_comparison.md
   for that rationale. v2 additions carried into v3 unchanged:

     - commerce.TourSchedules            — recurring tour departures
     - commerce.BookingParticipants,
       commerce.Tickets, commerce.TicketQrCodes, commerce.CheckInLogs
                                          — per-person check-in (UC-43) +
                                            QR/signature table (Non-Screen
                                            Function: Dynamic QR Code
                                            Generation & Crypto Signer)
     - commerce.TourSearchRequests,
       commerce.TourRecommendationLogs   — UC-25 Tour Matching Algorithm
     - trip.OfflineSyncBatches/Items     — generic offline sync (UC-72)
     - dbo.RefreshTokens                 — JWT session management
     - social.GroupLocationSharing       — storage for location-sharing flag
     - trip.TripStateHistory             — FSM transition audit trail
     - commerce.Vouchers                 — max_discount_amount, usage_limit_per_user

   Everything else (schema-per-domain layout, BIGINT IDENTITY keys,
   Itinerary.source_type unification) is carried over unchanged.
   ===================================================================== */

-- CREATE DATABASE TripMateDb COLLATE Vietnamese_100_CI_AS;
-- GO
-- USE TripMateDb;
-- GO

CREATE SCHEMA catalog AUTHORIZATION dbo;
GO
CREATE SCHEMA commerce AUTHORIZATION dbo;
GO
CREATE SCHEMA planning AUTHORIZATION dbo;
GO
CREATE SCHEMA trip AUTHORIZATION dbo;
GO
CREATE SCHEMA payment AUTHORIZATION dbo;
GO
CREATE SCHEMA commercial AUTHORIZATION dbo;
GO
CREATE SCHEMA social AUTHORIZATION dbo;
GO

/* =====================================================================
   1. IDENTITY & ACCOUNTS  (dbo)
   Covers UC-01..UC-09, UC-34, UC-47..UC-51
   ===================================================================== */

CREATE TABLE dbo.Users (
    user_id             BIGINT IDENTITY(1,1) PRIMARY KEY,
    role                VARCHAR(20)   NOT NULL
        CHECK (role IN ('Traveler','TourOperator','Administrator')),
    email               NVARCHAR(256) NULL,
    phone_number        NVARCHAR(20)  NULL,
    password_hash       NVARCHAR(256) NULL,          -- NULL allowed: social-login-only accounts
    full_name           NVARCHAR(150) NOT NULL,
    avatar_url          NVARCHAR(500) NULL,
    status              VARCHAR(24)   NOT NULL DEFAULT 'Active'
        CHECK (status IN ('PendingEmailVerification','Active','Locked',
                           'PendingApproval','Rejected','Inactive')),
    email_verified_at   DATETIME2     NULL,
    phone_verified_at   DATETIME2     NULL,
    created_at          DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at          DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    last_login_at       DATETIME2     NULL,
    CONSTRAINT CK_Users_HasIdentifier CHECK (email IS NOT NULL OR phone_number IS NOT NULL)
);
GO
CREATE UNIQUE INDEX UX_Users_Email ON dbo.Users(email) WHERE email IS NOT NULL;
CREATE UNIQUE INDEX UX_Users_Phone ON dbo.Users(phone_number) WHERE phone_number IS NOT NULL;
CREATE INDEX IX_Users_Role_Status ON dbo.Users(role, status);
GO

CREATE TABLE dbo.AuthProviders (
    auth_provider_id    BIGINT IDENTITY(1,1) PRIMARY KEY,
    user_id             BIGINT NOT NULL REFERENCES dbo.Users(user_id) ON DELETE CASCADE,
    provider            VARCHAR(20) NOT NULL CHECK (provider IN ('Google')),
    provider_user_id    NVARCHAR(255) NOT NULL,
    linked_at           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_AuthProviders_Provider UNIQUE (provider, provider_user_id)
);
GO

-- NEW in v2 — UC-04/UC-05: JWT session issuance/revocation was previously untracked
CREATE TABLE dbo.RefreshTokens (
    refresh_token_id    BIGINT IDENTITY(1,1) PRIMARY KEY,
    user_id             BIGINT NOT NULL REFERENCES dbo.Users(user_id) ON DELETE CASCADE,
    token_hash          NVARCHAR(500) NOT NULL,
    expires_at          DATETIME2 NOT NULL,
    revoked_at          DATETIME2 NULL,
    device_info         NVARCHAR(300) NULL,
    created_at          DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_RefreshTokens_User ON dbo.RefreshTokens(user_id);
GO

-- v4: OtpCodes removed — OTP is short-lived (a few minutes) and deleted on
-- successful verification, so it belongs in a cache (Redis: key = user_id +
-- purpose, value = hashed code, TTL = expiry) rather than a SQL table.

CREATE TABLE dbo.TravelerProfiles (
    user_id                     BIGINT PRIMARY KEY
        REFERENCES dbo.Users(user_id) ON DELETE CASCADE,
    preferred_transport_mode   VARCHAR(20) NULL
        CHECK (preferred_transport_mode IN ('Walking','Motorbike','Car','PublicTransit')),
    travel_pace                VARCHAR(20) NULL CHECK (travel_pace IN ('Relaxed','Moderate','Fast')),
    risk_tolerance              VARCHAR(20) NULL CHECK (risk_tolerance IN ('Low','Medium','High')),
    food_preferences_json       NVARCHAR(1000) NULL,   -- JSON array, e.g. ["vegetarian","seafood"]
    interest_tags_json          NVARCHAR(1000) NULL,   -- JSON array of catalog.Tags.name
    default_budget              DECIMAL(12,2) NULL,
    updated_at                  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE dbo.OperatorProfiles (
    user_id             BIGINT PRIMARY KEY
        REFERENCES dbo.Users(user_id) ON DELETE CASCADE,
    company_name        NVARCHAR(200) NOT NULL,
    tax_code             NVARCHAR(50)  NOT NULL,
    business_license_no NVARCHAR(100) NOT NULL,
    contact_phone        NVARCHAR(20)  NULL,
    contact_address      NVARCHAR(300) NULL,
    commission_rate      DECIMAL(5,2)  NOT NULL DEFAULT 10.00
        CHECK (commission_rate BETWEEN 0 AND 100),
    approval_status      VARCHAR(20)   NOT NULL DEFAULT 'PendingApproval'
        CHECK (approval_status IN ('PendingApproval','Approved','Rejected')),
    rejection_reason     NVARCHAR(500) NULL,
    reviewed_by          BIGINT NULL REFERENCES dbo.Users(user_id),
    reviewed_at          DATETIME2 NULL,
    created_at           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_OperatorProfiles_TaxCode UNIQUE (tax_code)
);
GO

CREATE TABLE dbo.OperatorDocuments (
    document_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
    operator_user_id   BIGINT NOT NULL REFERENCES dbo.OperatorProfiles(user_id) ON DELETE CASCADE,
    document_type      VARCHAR(30) NOT NULL
        CHECK (document_type IN ('BusinessLicense','TaxCertificate','Other')),
    file_url           NVARCHAR(500) NOT NULL,
    status             VARCHAR(20) NOT NULL DEFAULT 'Submitted'
        CHECK (status IN ('Submitted','Approved','Rejected')),
    uploaded_at        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_OperatorDocuments_Operator ON dbo.OperatorDocuments(operator_user_id);
GO

CREATE TABLE dbo.SystemConfigs (
    config_key      VARCHAR(100) PRIMARY KEY,
    config_value    NVARCHAR(500) NOT NULL,
    description     NVARCHAR(500) NULL,
    updated_by      BIGINT NULL REFERENCES dbo.Users(user_id),
    updated_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- v6: RE-ADDED (removed in v4, restored here) — UC-66 "Update Landing Page
-- Content" needs somewhere to write to; hard-coding content in the frontend
-- means every copy change requires a redeploy, which defeats the point of
-- having an Admin-facing "update landing page" function at all.
CREATE TABLE dbo.LandingPageContents (
    content_id      BIGINT IDENTITY(1,1) PRIMARY KEY,
    section_key     VARCHAR(100) NOT NULL UNIQUE,   -- which landing-page block this row fills, e.g. 'Hero', 'FeaturedTours', 'AboutUs'
    title           NVARCHAR(200) NULL,
    body            NVARCHAR(MAX) NULL,
    media_url       NVARCHAR(500) NULL,
    is_published    BIT NOT NULL DEFAULT 1,
    updated_by      BIGINT NULL REFERENCES dbo.Users(user_id),
    updated_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE dbo.Notifications (
    notification_id     BIGINT IDENTITY(1,1) PRIMARY KEY,
    user_id              BIGINT NOT NULL REFERENCES dbo.Users(user_id) ON DELETE CASCADE,
    channel              VARCHAR(10) NOT NULL CHECK (channel IN ('Push','Email','SMS')),
    type                 VARCHAR(40) NOT NULL,
    title                NVARCHAR(200) NULL,
    body                 NVARCHAR(1000) NULL,
    related_entity_type  VARCHAR(40) NULL,
    related_entity_id    BIGINT NULL,
    status               VARCHAR(10) NOT NULL DEFAULT 'Pending'
        CHECK (status IN ('Pending','Sent','Failed','Read')),
    created_at           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    sent_at              DATETIME2 NULL
);
GO
CREATE INDEX IX_Notifications_User_Status ON dbo.Notifications(user_id, status);
GO

CREATE TABLE dbo.AuditLogs (
    audit_log_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
    actor_user_id        BIGINT NULL REFERENCES dbo.Users(user_id),
    action_type          VARCHAR(50) NOT NULL,
    affected_entity       VARCHAR(80) NOT NULL,
    affected_entity_id   BIGINT NULL,
    before_data           NVARCHAR(MAX) NULL,
    after_data            NVARCHAR(MAX) NULL,
    ip_address            VARCHAR(45) NULL,
    created_at            DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_AuditLogs_Entity ON dbo.AuditLogs(affected_entity, affected_entity_id);
CREATE INDEX IX_AuditLogs_Actor_Date ON dbo.AuditLogs(actor_user_id, created_at DESC);
GO

/* =====================================================================
   2. CATALOG — POI & ROUTING  (catalog)
   Covers UC-12, UC-52..UC-56
   ===================================================================== */

CREATE TABLE catalog.POICategories (
    category_id     INT IDENTITY(1,1) PRIMARY KEY,
    name            NVARCHAR(100) NOT NULL UNIQUE,
    description     NVARCHAR(300) NULL
);
GO

CREATE TABLE catalog.POIs (
    poi_id                      BIGINT IDENTITY(1,1) PRIMARY KEY,
    category_id                 INT NOT NULL REFERENCES catalog.POICategories(category_id),
    name                        NVARCHAR(200) NOT NULL,
    description                 NVARCHAR(2000) NULL,
    latitude                    DECIMAL(9,6) NOT NULL,
    longitude                   DECIMAL(9,6) NOT NULL,
    address                     NVARCHAR(400) NULL,
    indoor_outdoor               VARCHAR(10) NOT NULL DEFAULT 'Outdoor'
        CHECK (indoor_outdoor IN ('Indoor','Outdoor','Mixed')),
    -- TM-98 leaves these nullable cold-start scores unset on creation.
    -- A later approved aggregation flow may populate them from
    -- social.Reviews.scenic_rating / photo_rating (target_type = 'POI').
    scenic_score                 DECIMAL(3,1) NULL CHECK (scenic_score BETWEEN 0 AND 10),
    photo_rating                 DECIMAL(3,1) NULL CHECK (photo_rating BETWEEN 0 AND 10),
    avg_visit_duration_minutes  INT NOT NULL DEFAULT 60 CHECK (avg_visit_duration_minutes > 0),
    has_shelter                  BIT NOT NULL DEFAULT 0,
    status                       VARCHAR(10) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Inactive')),
    created_by                   BIGINT NULL REFERENCES dbo.Users(user_id),
    created_at                   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at                   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_POIs_Category ON catalog.POIs(category_id);
CREATE INDEX IX_POIs_LatLng ON catalog.POIs(latitude, longitude);
GO

CREATE TABLE catalog.POIOpeningHours (
    poi_id          BIGINT NOT NULL REFERENCES catalog.POIs(poi_id) ON DELETE CASCADE,
    day_of_week     TINYINT NOT NULL CHECK (day_of_week BETWEEN 0 AND 6),
    open_time       TIME NULL,
    close_time      TIME NULL,
    is_closed       BIT NOT NULL DEFAULT 0,
    CONSTRAINT PK_POIOpeningHours PRIMARY KEY (poi_id, day_of_week)
);
GO

CREATE TABLE catalog.POIPhotos (
    photo_id     BIGINT IDENTITY(1,1) PRIMARY KEY,
    poi_id       BIGINT NOT NULL REFERENCES catalog.POIs(poi_id) ON DELETE CASCADE,
    url          NVARCHAR(500) NOT NULL,
    caption      NVARCHAR(200) NULL,
    sort_order   INT NOT NULL DEFAULT 0
);
GO
CREATE INDEX IX_POIPhotos_POI ON catalog.POIPhotos(poi_id);
GO

CREATE TABLE catalog.Tags (
    tag_id   INT IDENTITY(1,1) PRIMARY KEY,
    name     NVARCHAR(60) NOT NULL UNIQUE
);
GO

CREATE TABLE catalog.POITagMap (
    poi_id  BIGINT NOT NULL REFERENCES catalog.POIs(poi_id) ON DELETE CASCADE,
    tag_id  INT NOT NULL REFERENCES catalog.Tags(tag_id) ON DELETE CASCADE,
    CONSTRAINT PK_POITagMap PRIMARY KEY (poi_id, tag_id)
);
GO

-- NEW in v4 — lets a Traveler bookmark a POI
CREATE TABLE catalog.FavoritePOIs (
    user_id      BIGINT NOT NULL REFERENCES dbo.Users(user_id) ON DELETE CASCADE,
    poi_id       BIGINT NOT NULL REFERENCES catalog.POIs(poi_id) ON DELETE CASCADE,
    created_at   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_FavoritePOIs PRIMARY KEY (user_id, poi_id)
);
GO

CREATE TABLE catalog.RouteSegments (
    segment_id             BIGINT IDENTITY(1,1) PRIMARY KEY,
    from_poi_id            BIGINT NOT NULL REFERENCES catalog.POIs(poi_id),
    to_poi_id              BIGINT NOT NULL REFERENCES catalog.POIs(poi_id),
    transport_mode         VARCHAR(20) NOT NULL
        CHECK (transport_mode IN ('Walking','Motorbike','Car','PublicTransit')),
    distance_km             DECIMAL(6,2) NOT NULL CHECK (distance_km > 0),
    est_duration_minutes   INT NOT NULL CHECK (est_duration_minutes > 0),
    difficulty              VARCHAR(10) NULL CHECK (difficulty IN ('Easy','Moderate','Hard')),
    slope_percent            DECIMAL(4,1) NULL,
    safety_score             DECIMAL(3,1) NULL CHECK (safety_score BETWEEN 0 AND 10),
    scenic_score              DECIMAL(3,1) NULL CHECK (scenic_score BETWEEN 0 AND 10),
    created_at                DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_RouteSegments_NoSelfLoop CHECK (from_poi_id <> to_poi_id),
    CONSTRAINT UQ_RouteSegments UNIQUE (from_poi_id, to_poi_id, transport_mode)
);
GO
CREATE INDEX IX_RouteSegments_From ON catalog.RouteSegments(from_poi_id);
CREATE INDEX IX_RouteSegments_To ON catalog.RouteSegments(to_poi_id);
GO

/* =====================================================================
   3. COMMERCE — TOURS & SCHEDULES  (commerce)
   Covers UC-35..UC-37, UC-60, UC-61
   ===================================================================== */

CREATE TABLE commerce.Tours (
    tour_id             BIGINT IDENTITY(1,1) PRIMARY KEY,
    operator_user_id    BIGINT NOT NULL REFERENCES dbo.OperatorProfiles(user_id),
    title                NVARCHAR(200) NOT NULL,
    description           NVARCHAR(MAX) NULL,
    base_price            DECIMAL(12,2) NOT NULL CHECK (base_price >= 0),
    duration_days           INT NOT NULL DEFAULT 1 CHECK (duration_days > 0),
    status                  VARCHAR(12) NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft','Pending','Approved','Rejected','Inactive')),
    rejection_reason         NVARCHAR(500) NULL,
    reviewed_by               BIGINT NULL REFERENCES dbo.Users(user_id),
    reviewed_at                DATETIME2 NULL,
    published_at                DATETIME2 NULL,
    created_at                  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at                  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    -- v2: slot_capacity/slots_booked moved to commerce.TourSchedules —
    -- a Tour is now a reusable template that can run on multiple dates.
);
GO
CREATE INDEX IX_Tours_Operator ON commerce.Tours(operator_user_id);
CREATE INDEX IX_Tours_Status ON commerce.Tours(status);
GO

CREATE TABLE commerce.TourItineraryItems (
    tour_item_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
    tour_id                  BIGINT NOT NULL REFERENCES commerce.Tours(tour_id) ON DELETE CASCADE,
    day_no                    INT NOT NULL DEFAULT 1 CHECK (day_no > 0),
    sequence_no                INT NOT NULL CHECK (sequence_no > 0),
    poi_id                      BIGINT NOT NULL REFERENCES catalog.POIs(poi_id),
    planned_time                 TIME NULL,
    stay_duration_minutes       INT NOT NULL DEFAULT 60 CHECK (stay_duration_minutes > 0),
    notes                         NVARCHAR(500) NULL,
    CONSTRAINT UQ_TourItineraryItems UNIQUE (tour_id, day_no, sequence_no)
);
GO

-- NEW in v2 — one Tour template can run on many dates, each with its own capacity
CREATE TABLE commerce.TourSchedules (
    schedule_id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    tour_id                BIGINT NOT NULL REFERENCES commerce.Tours(tour_id),
    start_datetime            DATETIME2 NOT NULL,
    end_datetime                DATETIME2 NOT NULL,
    meeting_point                 NVARCHAR(500) NULL,   -- NEW in v4: this dated departure's gathering point (can differ from the tour's own POI stops)
    total_capacity                 INT NOT NULL CHECK (total_capacity > 0),
    reserved_capacity                 INT NOT NULL DEFAULT 0 CHECK (reserved_capacity >= 0),
    status                               VARCHAR(12) NOT NULL DEFAULT 'Scheduled'
        CHECK (status IN ('Scheduled','Cancelled','Completed')),
    created_at                              DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_TourSchedules_CapacityWithinLimit CHECK (reserved_capacity <= total_capacity),
    CONSTRAINT CK_TourSchedules_DateOrder CHECK (end_datetime > start_datetime)
);
GO
CREATE INDEX IX_TourSchedules_Tour ON commerce.TourSchedules(tour_id, start_datetime);
GO

/* =====================================================================
   4. PLANNING — CSP SCHEDULING & ITINERARIES  (planning)
   Covers UC-10, UC-11
   ===================================================================== */

CREATE TABLE planning.SchedulingRequests (
    request_id                 BIGINT IDENTITY(1,1) PRIMARY KEY,
    traveler_user_id            BIGINT NOT NULL REFERENCES dbo.Users(user_id),
    start_latitude                DECIMAL(9,6) NOT NULL,
    start_longitude               DECIMAL(9,6) NOT NULL,
    destination_latitude          DECIMAL(9,6) NULL,
    destination_longitude         DECIMAL(9,6) NULL,
    available_minutes             INT NOT NULL CHECK (available_minutes > 0),
    search_radius_km               DECIMAL(6,2) NULL,   -- NEW in v4: bounds "explore around this area" requests
    budget                         DECIMAL(12,2) NULL,
    mandatory_poi_ids_json          NVARCHAR(500) NULL,
    preferences_snapshot_json        NVARCHAR(MAX) NULL,
    status                            VARCHAR(12) NOT NULL DEFAULT 'Pending'
        CHECK (status IN ('Pending','Processing','Completed','Failed')),
    requested_at                       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    completed_at                        DATETIME2 NULL
);
GO
CREATE INDEX IX_SchedulingRequests_Traveler ON planning.SchedulingRequests(traveler_user_id);
GO

CREATE TABLE planning.Itineraries (
    itinerary_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
    traveler_user_id         BIGINT NOT NULL REFERENCES dbo.Users(user_id),
    source_type               VARCHAR(14) NOT NULL
        CHECK (source_type IN ('CSPGenerated','BookedTour','Manual')),
    scheduling_request_id      BIGINT NULL REFERENCES planning.SchedulingRequests(request_id),
    source_tour_id              BIGINT NULL REFERENCES commerce.Tours(tour_id),
    title                         NVARCHAR(200) NULL,
    status                         VARCHAR(10) NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft','Active','Completed','Cancelled')),
    valid_from                     DATETIME2 NULL,
    valid_to                        DATETIME2 NULL,
    version                          INT NOT NULL DEFAULT 1 CHECK (version > 0),
    share_token                       NVARCHAR(64) NULL,   -- NEW in v4: token used in the public "Shared Itinerary View" link
    is_public                           BIT NOT NULL DEFAULT 0,   -- NEW in v4: whether this itinerary is viewable via share_token
    created_at                        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at                        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Itineraries_SourceConsistency CHECK (
        (source_type = 'CSPGenerated' AND scheduling_request_id IS NOT NULL) OR
        (source_type = 'BookedTour'   AND source_tour_id IS NOT NULL) OR
        (source_type = 'Manual')
    )
);
GO
CREATE INDEX IX_Itineraries_Traveler ON planning.Itineraries(traveler_user_id, status);
CREATE UNIQUE INDEX UX_Itineraries_ShareToken ON planning.Itineraries(share_token) WHERE share_token IS NOT NULL;
GO

CREATE TABLE planning.ItineraryItems (
    item_id                       BIGINT IDENTITY(1,1) PRIMARY KEY,
    itinerary_id                   BIGINT NOT NULL REFERENCES planning.Itineraries(itinerary_id) ON DELETE CASCADE,
    sequence_no                     INT NOT NULL CHECK (sequence_no > 0),
    poi_id                            BIGINT NOT NULL REFERENCES catalog.POIs(poi_id),
    planned_arrival                    DATETIME2 NULL,
    planned_departure                   DATETIME2 NULL,
    stay_duration_minutes                INT NOT NULL DEFAULT 60 CHECK (stay_duration_minutes > 0),
    is_mandatory                          BIT NOT NULL DEFAULT 0,
    estimated_cost                          DECIMAL(12,2) NULL,   -- NEW in v4: per-stop cost, lets the CSP engine track budget as it builds the itinerary
    recommendation_reason                     NVARCHAR(500) NULL,   -- NEW in v4: explains why the CSP engine chose this stop
    transport_mode_to_next                 VARCHAR(20) NULL
        CHECK (transport_mode_to_next IN ('Walking','Motorbike','Car','PublicTransit')),
    travel_duration_to_next_minutes         INT NULL,
    status                                   VARCHAR(10) NOT NULL DEFAULT 'Planned'
        CHECK (status IN ('Planned','Visited','Skipped')),
    CONSTRAINT UQ_ItineraryItems UNIQUE (itinerary_id, sequence_no)
);
GO
CREATE INDEX IX_ItineraryItems_POI ON planning.ItineraryItems(poi_id);
GO

/* =====================================================================
   5. TRIP — LIVE EXECUTION (FSM), INCIDENTS, REROUTING & OFFLINE SYNC (trip)
   Covers UC-13..UC-16, UC-58, UC-59, UC-70, UC-72
   ===================================================================== */

CREATE TABLE trip.TripSessions (
    session_id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    itinerary_id           BIGINT NOT NULL REFERENCES planning.Itineraries(itinerary_id),
    traveler_user_id        BIGINT NOT NULL REFERENCES dbo.Users(user_id),
    fsm_state                 VARCHAR(12) NOT NULL DEFAULT 'Planning'
        CHECK (fsm_state IN ('Planning','Navigating','Exploring','Interrupted','Completed')),
    current_latitude           DECIMAL(9,6) NULL,
    current_longitude           DECIMAL(9,6) NULL,
    started_at                    DATETIME2 NULL,
    ended_at                       DATETIME2 NULL,
    last_synced_at                  DATETIME2 NULL
);
GO
CREATE INDEX IX_TripSessions_Traveler ON trip.TripSessions(traveler_user_id, fsm_state);
CREATE INDEX IX_TripSessions_Itinerary ON trip.TripSessions(itinerary_id);
GO

-- NEW in v2 — FSM transition audit trail; TripSessions.fsm_state only ever held the current value
CREATE TABLE trip.TripStateHistory (
    history_id       BIGINT IDENTITY(1,1) PRIMARY KEY,
    session_id          BIGINT NOT NULL REFERENCES trip.TripSessions(session_id) ON DELETE CASCADE,
    from_state             VARCHAR(12) NULL,
    to_state                  VARCHAR(12) NOT NULL,
    reason                       NVARCHAR(500) NULL,
    triggered_by                    VARCHAR(20) NULL
        CHECK (triggered_by IN ('System','Traveler','Administrator')),
    changed_at                          DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_TripStateHistory_Session ON trip.TripStateHistory(session_id, changed_at);
GO

CREATE TABLE trip.TripLocationLogs (
    log_id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    session_id                BIGINT NOT NULL REFERENCES trip.TripSessions(session_id) ON DELETE CASCADE,
    latitude                    DECIMAL(9,6) NOT NULL,
    longitude                    DECIMAL(9,6) NOT NULL,
    recorded_at                    DATETIME2 NOT NULL,
    synced_at                       DATETIME2 NULL,
    is_offline_captured               BIT NOT NULL DEFAULT 0
);
GO
CREATE INDEX IX_TripLocationLogs_Session ON trip.TripLocationLogs(session_id, recorded_at);
GO

-- Short-retention cache/audit table, NOT a permanent weather archive.
-- Needed even though data comes from an external API because
-- trip.Incidents.weather_event_id must point at a stable row (so the
-- reroute reason stays explainable later) and to avoid re-calling the
-- external API for the same area/time window. Pair with a periodic
-- purge job (e.g. delete rows older than 90 days with no Incidents
-- referencing them) instead of growing this table forever.
CREATE TABLE trip.WeatherEvents (
    weather_event_id       BIGINT IDENTITY(1,1) PRIMARY KEY,
    region_name               NVARCHAR(150) NULL,
    latitude                     DECIMAL(9,6) NULL,
    longitude                     DECIMAL(9,6) NULL,
    event_type                     VARCHAR(40) NOT NULL,
    severity                         VARCHAR(10) NOT NULL
        CHECK (severity IN ('Low','Moderate','Severe','Extreme')),
    description                       NVARCHAR(1000) NULL,
    valid_from                          DATETIME2 NOT NULL,
    valid_to                             DATETIME2 NULL,
    source                                 VARCHAR(50) NOT NULL DEFAULT 'WeatherAPI',
    ingested_at                             DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_WeatherEvents_Window ON trip.WeatherEvents(valid_from, valid_to);
GO

CREATE TABLE trip.Incidents (
    incident_id       BIGINT IDENTITY(1,1) PRIMARY KEY,
    session_id           BIGINT NOT NULL REFERENCES trip.TripSessions(session_id) ON DELETE CASCADE,
    incident_type          VARCHAR(20) NOT NULL
        CHECK (incident_type IN ('SevereWeather','ScheduleDelay','RouteDeviation','POIClosure')),
    weather_event_id          BIGINT NULL REFERENCES trip.WeatherEvents(weather_event_id),
    description                 NVARCHAR(500) NULL,
    detected_at                   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    resolved_at                     DATETIME2 NULL
);
GO
CREATE INDEX IX_Incidents_Session ON trip.Incidents(session_id);
GO

CREATE TABLE trip.ReroutingEvents (
    rerouting_id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    incident_id                     BIGINT NOT NULL REFERENCES trip.Incidents(incident_id) ON DELETE CASCADE,
    session_id                        BIGINT NOT NULL REFERENCES trip.TripSessions(session_id),
    proposed_itinerary_snapshot          NVARCHAR(MAX) NOT NULL,
    status                                  VARCHAR(10) NOT NULL DEFAULT 'Proposed'
        CHECK (status IN ('Proposed','Accepted','Rejected','Expired')),
    proposed_at                              DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    decided_at                                 DATETIME2 NULL
);
GO
CREATE INDEX IX_ReroutingEvents_Session ON trip.ReroutingEvents(session_id);
GO

-- Generic offline sync inbox — UPLOAD direction only (device -> server).
-- Covers UC-72: GPS trail + trip-state changes + check-in attempts collected
-- while the phone had no connectivity. Items are staged here, then reconciled
-- into TripLocationLogs / TripStateHistory / CheckInLogs by data_type once the
-- backend processes the batch.
--
-- This is NOT where "offline map/itinerary" data lives (UC-16, DOWNLOAD
-- direction). Downloading POIs/itinerary for offline use on the phone is a
-- pure mobile-client concern (e.g. a local SQLite/Drift cache in the Flutter
-- app) — the backend already holds that data in catalog.POIs /
-- planning.Itineraries, so no backend table is needed for the download side.
CREATE TABLE trip.OfflineSyncBatches (
    batch_id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    user_id               BIGINT NOT NULL REFERENCES dbo.Users(user_id),
    device_id                NVARCHAR(150) NOT NULL,
    batch_uuid                  NVARCHAR(100) NOT NULL UNIQUE,
    total_items                    INT NOT NULL DEFAULT 0,
    success_items                     INT NOT NULL DEFAULT 0,
    failed_items                         INT NOT NULL DEFAULT 0,
    status                                   VARCHAR(10) NOT NULL DEFAULT 'Pending'
        CHECK (status IN ('Pending','Processing','Completed','Failed')),
    created_at                                  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    synced_at                                      DATETIME2 NULL
);
GO
CREATE INDEX IX_OfflineSyncBatches_User ON trip.OfflineSyncBatches(user_id, status);
GO

CREATE TABLE trip.OfflineSyncItems (
    item_id                BIGINT IDENTITY(1,1) PRIMARY KEY,
    batch_id                  BIGINT NOT NULL REFERENCES trip.OfflineSyncBatches(batch_id) ON DELETE CASCADE,
    data_type                    VARCHAR(20) NOT NULL
        CHECK (data_type IN ('GpsLog','TripStateChange','CheckIn','Other')),
    client_record_id                NVARCHAR(100) NULL,
    payload                            NVARCHAR(MAX) NOT NULL,
    client_timestamp                      DATETIME2 NULL,
    sync_status                              VARCHAR(10) NOT NULL DEFAULT 'Pending'
        CHECK (sync_status IN ('Pending','Applied','Failed')),
    error_message                                NVARCHAR(1000) NULL,
    synced_at                                       DATETIME2 NULL
);
GO
CREATE INDEX IX_OfflineSyncItems_Batch ON trip.OfflineSyncItems(batch_id, sync_status);
GO

/* =====================================================================
   6. COMMERCE — TOUR MATCHING, VOUCHERS & BOOKINGS  (commerce)
   Covers UC-24..UC-29, UC-38..UC-43
   ===================================================================== */

-- NEW in v2 — UC-25 Tour Matching Algorithm was previously unmodeled
CREATE TABLE commerce.TourSearchRequests (
    search_request_id    BIGINT IDENTITY(1,1) PRIMARY KEY,
    traveler_user_id        BIGINT NOT NULL REFERENCES dbo.Users(user_id),
    destination_text           NVARCHAR(300) NULL,
    start_date                     DATE NULL,
    end_date                          DATE NULL,
    min_budget                            DECIMAL(12,2) NULL,
    max_budget                               DECIMAL(12,2) NULL,
    interest_tags_json                          NVARCHAR(1000) NULL,
    requested_at                                   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE commerce.TourRecommendationLogs (
    recommendation_id      BIGINT IDENTITY(1,1) PRIMARY KEY,
    search_request_id         BIGINT NOT NULL REFERENCES commerce.TourSearchRequests(search_request_id) ON DELETE CASCADE,
    tour_id                      BIGINT NOT NULL REFERENCES commerce.Tours(tour_id),
    similarity_score                DECIMAL(5,2) NOT NULL CHECK (similarity_score BETWEEN 0 AND 100),
    is_recommended                     BIT NOT NULL,   -- true when similarity_score > the configured 80% threshold
    recommended_at                        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_TourRecommendationLogs_Search ON commerce.TourRecommendationLogs(search_request_id);
GO

CREATE TABLE commerce.Vouchers (
    voucher_id                BIGINT IDENTITY(1,1) PRIMARY KEY,
    owner_operator_user_id      BIGINT NULL REFERENCES dbo.OperatorProfiles(user_id),
    code                          VARCHAR(30) NOT NULL UNIQUE,
    discount_type                   VARCHAR(10) NOT NULL CHECK (discount_type IN ('Percentage','Flat')),
    discount_value                    DECIMAL(12,2) NOT NULL CHECK (discount_value > 0),
    max_discount_amount                 DECIMAL(12,2) NULL,   -- NEW in v2: caps a Percentage discount's absolute value
    min_order_amount                      DECIMAL(12,2) NOT NULL DEFAULT 0,
    usage_limit                             INT NULL,
    usage_limit_per_user                      INT NULL,   -- NEW in v2: per-traveler cap, independent of the global usage_limit
    used_count                                  INT NOT NULL DEFAULT 0,
    valid_from                                    DATETIME2 NOT NULL,
    valid_to                                        DATETIME2 NOT NULL,
    status                                            VARCHAR(10) NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active','Expired','Disabled')),
    created_at                                          DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Vouchers_ValidWindow CHECK (valid_to > valid_from),
    CONSTRAINT CK_Vouchers_UsageWithinLimit CHECK (usage_limit IS NULL OR used_count <= usage_limit)
);
GO

CREATE TABLE commerce.VoucherApplicableTours (
    voucher_id   BIGINT NOT NULL REFERENCES commerce.Vouchers(voucher_id) ON DELETE CASCADE,
    tour_id       BIGINT NOT NULL REFERENCES commerce.Tours(tour_id) ON DELETE CASCADE,
    CONSTRAINT PK_VoucherApplicableTours PRIMARY KEY (voucher_id, tour_id)
);
GO

-- v2: booking now points at a specific TourSchedule (a dated departure) instead of
-- the Tour template directly; qr_code_token/checked_in_at/checked_in_by moved out
-- to commerce.Tickets + commerce.TicketQrCodes since check-in is now per-person.
CREATE TABLE commerce.Bookings (
    booking_id               BIGINT IDENTITY(1,1) PRIMARY KEY,
    booking_code                VARCHAR(30) NOT NULL UNIQUE,   -- human-readable reference shown to the traveler
    traveler_user_id              BIGINT NOT NULL REFERENCES dbo.Users(user_id),
    tour_schedule_id                BIGINT NULL REFERENCES commerce.TourSchedules(schedule_id),
    itinerary_id                       BIGINT NULL REFERENCES planning.Itineraries(itinerary_id),
    voucher_id                            BIGINT NULL REFERENCES commerce.Vouchers(voucher_id),
    quantity                                 INT NOT NULL DEFAULT 1 CHECK (quantity > 0),
    unit_price                                 DECIMAL(12,2) NOT NULL CHECK (unit_price >= 0),
    discount_amount                               DECIMAL(12,2) NOT NULL DEFAULT 0 CHECK (discount_amount >= 0),
    total_amount                                    DECIMAL(12,2) NOT NULL CHECK (total_amount >= 0),
    status                                             VARCHAR(14) NOT NULL DEFAULT 'PendingPayment'
        CHECK (status IN ('PendingPayment','Confirmed','Cancelled','Completed','Expired')),
    payment_status                                        VARCHAR(16) NOT NULL DEFAULT 'Unpaid'
        CHECK (payment_status IN ('Unpaid','Paid','Refunded','PartiallyRefunded')),
    booked_at                                                DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    payment_expires_at                                          DATETIME2 NULL,
    cancelled_at                                                   DATETIME2 NULL,
    cancel_reason                                                     NVARCHAR(300) NULL
);
GO
CREATE INDEX IX_Bookings_Traveler ON commerce.Bookings(traveler_user_id, status);
CREATE INDEX IX_Bookings_TourSchedule ON commerce.Bookings(tour_schedule_id, status);
CREATE INDEX IX_Bookings_ExpiryScan ON commerce.Bookings(status, payment_expires_at)
    WHERE status = 'PendingPayment';
GO

-- NEW in v2 — named ticket-holders per booking, required to issue individual Tickets
-- v4: added identity-verification fields for premium/international tours.
-- id_document_number holds PII — per Business Rule BR-33 (SRS §5.1), must be
-- encrypted in transit and at rest (encrypt at the application layer; do not
-- rely on column-level plaintext storage).
CREATE TABLE commerce.BookingParticipants (
    participant_id     BIGINT IDENTITY(1,1) PRIMARY KEY,
    booking_id            BIGINT NOT NULL REFERENCES commerce.Bookings(booking_id) ON DELETE CASCADE,
    full_name               NVARCHAR(150) NOT NULL,
    phone_number               NVARCHAR(20) NULL,
    email                          NVARCHAR(256) NULL,
    participant_type                  VARCHAR(10) NULL CHECK (participant_type IN ('Adult','Child','Infant')),
    id_document_type                     VARCHAR(20) NULL CHECK (id_document_type IN ('CCCD','Passport','Other')),
    id_document_number                      NVARCHAR(50) NULL,   -- PII — see note above
    nationality                                CHAR(3) NULL   -- ISO 3166-1 alpha-3 country code
);
GO
CREATE INDEX IX_BookingParticipants_Booking ON commerce.BookingParticipants(booking_id);
GO

-- NEW in v2 — one ticket per seat/person (fixes: v1's single qr_code_token per
-- Booking couldn't check quantity > 1 in independently, breaking UC-43 for group bookings)
CREATE TABLE commerce.Tickets (
    ticket_id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    booking_id             BIGINT NOT NULL REFERENCES commerce.Bookings(booking_id) ON DELETE CASCADE,
    participant_id            BIGINT NULL REFERENCES commerce.BookingParticipants(participant_id),
    ticket_code                  VARCHAR(30) NOT NULL UNIQUE,
    status                          VARCHAR(10) NOT NULL DEFAULT 'Valid'
        CHECK (status IN ('Valid','Used','Cancelled','Expired')),
    issued_at                          DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    expires_at                            DATETIME2 NULL,
    checked_in_at                            DATETIME2 NULL
);
GO
CREATE INDEX IX_Tickets_Booking ON commerce.Tickets(booking_id);
GO

-- NEW in v2 — the actual QR payload/signature, split out from Tickets so a QR can be
-- rotated (revoked + re-issued) without touching ticket status. Maps directly to the
-- SRS Non-Screen Function "Dynamic QR Code Generation & Cryptographic Signer".
CREATE TABLE commerce.TicketQrCodes (
    qr_code_id         BIGINT IDENTITY(1,1) PRIMARY KEY,
    ticket_id             BIGINT NOT NULL REFERENCES commerce.Tickets(ticket_id) ON DELETE CASCADE,
    qr_token                NVARCHAR(500) NOT NULL UNIQUE,   -- encrypted payload encoded into the QR image
    signature                  NVARCHAR(500) NOT NULL,          -- cryptographic signature, prevents ticket forgery
    status                        VARCHAR(10) NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active','Consumed','Revoked','Expired')),
    issued_at                       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    expires_at                        DATETIME2 NULL,
    consumed_at                          DATETIME2 NULL
);
GO
CREATE UNIQUE INDEX UX_TicketQrCodes_OneActivePerTicket
    ON commerce.TicketQrCodes(ticket_id) WHERE status = 'Active';
GO

-- NEW in v2 — append-only scan audit trail (supports UC-43's "invalid/previously used
-- tickets are handled according to check-in rules" — failed scans are recorded too)
CREATE TABLE commerce.CheckInLogs (
    checkin_log_id       BIGINT IDENTITY(1,1) PRIMARY KEY,
    ticket_id               BIGINT NOT NULL REFERENCES commerce.Tickets(ticket_id),
    qr_code_id                 BIGINT NULL REFERENCES commerce.TicketQrCodes(qr_code_id),
    checked_in_by                 BIGINT NULL REFERENCES dbo.Users(user_id),   -- operator who scanned
    scan_result                      VARCHAR(12) NOT NULL
        CHECK (scan_result IN ('Success','AlreadyUsed','Invalid','Expired')),
    latitude                            DECIMAL(9,6) NULL,
    longitude                             DECIMAL(9,6) NULL,
    checked_in_at                           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_CheckInLogs_Ticket ON commerce.CheckInLogs(ticket_id, checked_in_at);
GO

CREATE TABLE commerce.VoucherRedemptions (
    redemption_id     BIGINT IDENTITY(1,1) PRIMARY KEY,
    voucher_id           BIGINT NOT NULL REFERENCES commerce.Vouchers(voucher_id),
    booking_id             BIGINT NOT NULL UNIQUE REFERENCES commerce.Bookings(booking_id),
    discount_amount           DECIMAL(12,2) NOT NULL,
    redeemed_at                 DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

/* =====================================================================
   7. PAYMENT — TRANSACTIONS, REFUNDS, PAYOUTS  (payment)
   Covers UC-28, UC-42, UC-46, UC-62..UC-65, UC-71
   Unchanged from v1.
   ===================================================================== */

CREATE TABLE payment.PaymentTransactions (
    transaction_id               BIGINT IDENTITY(1,1) PRIMARY KEY,
    booking_id                     BIGINT NOT NULL REFERENCES commerce.Bookings(booking_id),
    gateway                          VARCHAR(20) NOT NULL CHECK (gateway IN ('VNPAY','MoMo','PayOS','Stripe')),
    gateway_transaction_ref            NVARCHAR(150) NULL,
    amount                                DECIMAL(12,2) NOT NULL CHECK (amount > 0),
    currency                                CHAR(3) NOT NULL DEFAULT 'VND',
    transaction_type                          VARCHAR(10) NOT NULL CHECK (transaction_type IN ('Payment','Refund')),
    status                                       VARCHAR(10) NOT NULL DEFAULT 'Pending'
        CHECK (status IN ('Pending','Success','Failed')),
    raw_response                                   NVARCHAR(MAX) NULL,
    created_at                                       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_PaymentTransactions_Booking ON payment.PaymentTransactions(booking_id);
GO

CREATE TABLE payment.Refunds (
    refund_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
    booking_id              BIGINT NOT NULL REFERENCES commerce.Bookings(booking_id),
    transaction_id             BIGINT NULL REFERENCES payment.PaymentTransactions(transaction_id),
    reason                        NVARCHAR(500) NULL,
    amount                          DECIMAL(12,2) NOT NULL CHECK (amount > 0),
    initiated_by                      VARCHAR(14) NOT NULL
        CHECK (initiated_by IN ('System','TourOperator','Administrator')),
    status                              VARCHAR(10) NOT NULL DEFAULT 'Pending'
        CHECK (status IN ('Pending','Processed','Failed')),
    requested_at                          DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    processed_at                            DATETIME2 NULL
);
GO
CREATE INDEX IX_Refunds_Booking ON payment.Refunds(booking_id);
GO

CREATE TABLE payment.Payouts (
    payout_id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    operator_user_id          BIGINT NOT NULL REFERENCES dbo.OperatorProfiles(user_id),
    period_start                DATE NOT NULL,
    period_end                    DATE NOT NULL,
    gross_revenue                   DECIMAL(14,2) NOT NULL CHECK (gross_revenue >= 0),
    commission_amount                 DECIMAL(14,2) NOT NULL CHECK (commission_amount >= 0),
    net_amount                          DECIMAL(14,2) NOT NULL CHECK (net_amount >= 0),
    status                                 VARCHAR(12) NOT NULL DEFAULT 'Pending'
        CHECK (status IN ('Pending','Requested','Confirmed','Paid','Rejected')),
    requested_at                             DATETIME2 NULL,
    confirmed_by                               BIGINT NULL REFERENCES dbo.Users(user_id),
    confirmed_at                                 DATETIME2 NULL,
    CONSTRAINT CK_Payouts_PeriodOrder CHECK (period_end >= period_start),
    CONSTRAINT CK_Payouts_NetAmount CHECK (net_amount = gross_revenue - commission_amount)
);
GO
CREATE INDEX IX_Payouts_Operator ON payment.Payouts(operator_user_id, status);
GO

CREATE TABLE payment.PayoutItems (
    payout_item_id     BIGINT IDENTITY(1,1) PRIMARY KEY,
    payout_id             BIGINT NOT NULL REFERENCES payment.Payouts(payout_id) ON DELETE CASCADE,
    booking_id               BIGINT NOT NULL REFERENCES commerce.Bookings(booking_id),
    amount                     DECIMAL(12,2) NOT NULL CHECK (amount >= 0),
    CONSTRAINT UQ_PayoutItems UNIQUE (payout_id, booking_id)
);
GO

/* =====================================================================
   8. COMMERCIAL SERVICES — VEHICLE RENTAL / HOTEL / RESTAURANT  (commercial)
   Covers UC-30, UC-31, and generalizes the same pattern to hotel and
   restaurant partners so future verticals don't each need their own
   3-table set.

   One ServiceProvider (an external partner business) offers one or more
   Services (a bookable item: a specific vehicle, a room type, a table/menu
   slot). A traveler books a Service via ServiceBookings. Because these
   providers are NOT platform accounts (no OperatorProfiles/commission_rate,
   unlike Tour Operators), TripMate's revenue from them is recorded directly
   as commission_amount per booking rather than run through payment.Payouts
   — Payouts pays money OUT to Tour Operators, this is money TripMate earns
   IN from external partners, a different direction.
   ===================================================================== */

CREATE TABLE commercial.ServiceProviders (
    provider_id         BIGINT IDENTITY(1,1) PRIMARY KEY,
    name                   NVARCHAR(150) NOT NULL,
    service_category          VARCHAR(20) NOT NULL
        CHECK (service_category IN ('Vehicle','Hotel','Restaurant')),
    contact_email                NVARCHAR(200) NULL,
    contact_phone                   NVARCHAR(20) NULL,
    api_endpoint                       NVARCHAR(300) NULL,   -- set when the provider is integrated via API rather than manual admin entry
    commission_rate                       DECIMAL(5,2) NULL
        CHECK (commission_rate IS NULL OR commission_rate BETWEEN 0 AND 100),   -- negotiated affiliate rate, if any
    status                                    VARCHAR(10) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Inactive'))
);
GO
CREATE INDEX IX_ServiceProviders_Category ON commercial.ServiceProviders(service_category);
GO

-- The bookable item/listing: a specific vehicle, room type, or restaurant table/menu slot
CREATE TABLE commercial.Services (
    service_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
    provider_id              BIGINT NOT NULL REFERENCES commercial.ServiceProviders(provider_id),
    service_category            VARCHAR(20) NOT NULL
        CHECK (service_category IN ('Vehicle','Hotel','Restaurant')),
    name                            NVARCHAR(200) NOT NULL,   -- e.g. "Honda Wave", "Deluxe Room", "Set Menu for 2"
    description                        NVARCHAR(2000) NULL,
    poi_id                                 BIGINT NULL REFERENCES catalog.POIs(poi_id),   -- physical venue on the map, if any (Hotel/Restaurant; usually NULL for a Vehicle fleet)
    price_amount                              DECIMAL(12,2) NOT NULL CHECK (price_amount >= 0),
    price_unit                                    VARCHAR(12) NOT NULL
        CHECK (price_unit IN ('PerDay','PerNight','PerItem','PerPerson')),
    capacity                                          INT NULL,   -- seats/rooms/vehicles available, generic across categories
    attributes_json                                       NVARCHAR(1000) NULL,   -- category-specific fields, e.g. Vehicle:{seats,transmission}, Hotel:{room_type,beds}, Restaurant:{cuisine,table_size}
    availability_status                                       VARCHAR(14) NOT NULL DEFAULT 'Available'
        CHECK (availability_status IN ('Available','Unavailable')),
    created_at                                                    DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_Services_Provider ON commercial.Services(provider_id);
CREATE INDEX IX_Services_Category ON commercial.Services(service_category, availability_status);
GO

CREATE TABLE commercial.ServiceBookings (
    service_booking_id       BIGINT IDENTITY(1,1) PRIMARY KEY,
    traveler_user_id            BIGINT NOT NULL REFERENCES dbo.Users(user_id),
    service_id                     BIGINT NOT NULL REFERENCES commercial.Services(service_id),
    start_datetime                    DATETIME2 NOT NULL,   -- rental pickup / hotel check-in / restaurant reservation time
    end_datetime                          DATETIME2 NULL,        -- rental return / hotel check-out; NULL for a point-in-time restaurant reservation
    quantity                                  INT NOT NULL DEFAULT 1 CHECK (quantity > 0),   -- vehicles / rooms / seats
    total_price                                  DECIMAL(12,2) NOT NULL CHECK (total_price >= 0),
    commission_amount                                DECIMAL(12,2) NULL CHECK (commission_amount IS NULL OR commission_amount >= 0),   -- revenue TripMate recognizes on this booking
    status                                               VARCHAR(10) NOT NULL DEFAULT 'Pending'
        CHECK (status IN ('Pending','Confirmed','Cancelled','Completed')),
    provider_reference                                       NVARCHAR(150) NULL,   -- booking reference returned by the external provider's system
    created_at                                                   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_ServiceBookings_DateOrder CHECK (end_datetime IS NULL OR end_datetime >= start_datetime)
);
GO
CREATE INDEX IX_ServiceBookings_Traveler ON commercial.ServiceBookings(traveler_user_id);
CREATE INDEX IX_ServiceBookings_Service ON commercial.ServiceBookings(service_id, status);
GO

-- Ad-link / click-through revenue tracking — a click doesn't require a booking
-- to have value (impression/click-based affiliate deals), so this is logged
-- independently of ServiceBookings; resulted_in_booking_id is filled in later
-- if the click did convert, to measure click-to-booking conversion rate.
CREATE TABLE commercial.ServiceClickLogs (
    click_id                 BIGINT IDENTITY(1,1) PRIMARY KEY,
    traveler_user_id             BIGINT NULL REFERENCES dbo.Users(user_id),   -- NULL when a guest (not signed in) clicks
    service_id                       BIGINT NOT NULL REFERENCES commercial.Services(service_id),
    clicked_at                           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    resulted_in_booking_id                   BIGINT NULL REFERENCES commercial.ServiceBookings(service_booking_id)
);
GO
CREATE INDEX IX_ServiceClickLogs_Service ON commercial.ServiceClickLogs(service_id, clicked_at);
GO

/* =====================================================================
   9. SOCIAL — GROUP TRAVEL & REVIEWS  (social)
   Covers UC-17..UC-23, UC-33
   ===================================================================== */

CREATE TABLE social.TravelGroups (
    group_id         BIGINT IDENTITY(1,1) PRIMARY KEY,
    itinerary_id        BIGINT NOT NULL REFERENCES planning.Itineraries(itinerary_id),
    host_user_id           BIGINT NOT NULL REFERENCES dbo.Users(user_id),
    name                      NVARCHAR(150) NULL,
    created_at                  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE social.GroupMembers (
    group_id                     BIGINT NOT NULL REFERENCES social.TravelGroups(group_id) ON DELETE CASCADE,
    user_id                         BIGINT NOT NULL REFERENCES dbo.Users(user_id),
    location_sharing_enabled          BIT NOT NULL DEFAULT 0,
    status                               VARCHAR(10) NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active','Removed','Left')),
    joined_at                              DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    left_at                                  DATETIME2 NULL,
    CONSTRAINT PK_GroupMembers PRIMARY KEY (group_id, user_id)
);
GO

CREATE TABLE social.GroupInvitations (
    invitation_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
    group_id                 BIGINT NOT NULL REFERENCES social.TravelGroups(group_id) ON DELETE CASCADE,
    invite_code                  VARCHAR(20) NOT NULL UNIQUE,
    created_by                     BIGINT NOT NULL REFERENCES dbo.Users(user_id),
    expires_at                        DATETIME2 NOT NULL,
    max_uses                            INT NOT NULL DEFAULT 1 CHECK (max_uses > 0),
    used_count                            INT NOT NULL DEFAULT 0,
    created_at                              DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_GroupInvitations_UsageWithinLimit CHECK (used_count <= max_uses)
);
GO

-- NEW in v2 — actual storage for members' shared location; GroupMembers.location_sharing_enabled
-- was previously just a flag with nowhere for the location itself to land
CREATE TABLE social.GroupLocationSharing (
    location_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
    group_id              BIGINT NOT NULL REFERENCES social.TravelGroups(group_id) ON DELETE CASCADE,
    user_id                  BIGINT NOT NULL REFERENCES dbo.Users(user_id),
    latitude                    DECIMAL(9,6) NOT NULL,
    longitude                     DECIMAL(9,6) NOT NULL,
    recorded_at                      DATETIME2 NOT NULL
);
GO
CREATE INDEX IX_GroupLocationSharing_Group ON social.GroupLocationSharing(group_id, recorded_at DESC);
GO

CREATE TABLE social.Reviews (
    review_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
    traveler_user_id         BIGINT NOT NULL REFERENCES dbo.Users(user_id),
    target_type                 VARCHAR(14) NOT NULL CHECK (target_type IN ('Tour','POI','RouteSegment','Operator')),
    target_id                     BIGINT NOT NULL,
    booking_id                       BIGINT NULL REFERENCES commerce.Bookings(booking_id),
    rating                              TINYINT NOT NULL CHECK (rating BETWEEN 1 AND 5),
    scenic_rating                         TINYINT NULL CHECK (scenic_rating BETWEEN 1 AND 5),   -- NEW in v4: POI-only, feeds catalog.POIs.scenic_score aggregation
    photo_rating                            TINYINT NULL CHECK (photo_rating BETWEEN 1 AND 5),   -- NEW in v4: POI-only, feeds catalog.POIs.photo_rating aggregation
    comment                               NVARCHAR(1000) NULL,
    created_at                              DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Reviews_PoiOnlyRatings CHECK (
        target_type = 'POI' OR (scenic_rating IS NULL AND photo_rating IS NULL)
    )
);
GO
CREATE INDEX IX_Reviews_Target ON social.Reviews(target_type, target_id);
GO

/* =====================================================================
   10. REFERENCE — UI MESSAGE CATALOG  (dbo)
   v6: BusinessRules/Screens/ScreenMessageTriggers removed — that mapping
   (which screen shows which message, enforcing which business rule) turned
   out to be documentation, not something the app queries at runtime, so it
   moved to database/screen_message_business_rule_mapping.md instead. This
   table stays because message_code -> content_template lookup IS a real
   runtime need (frontend/backend resolve a code to display text, e.g. for
   i18n), unlike the screen/rule mapping.
   ===================================================================== */

CREATE TABLE dbo.Messages (
    message_code      VARCHAR(10) PRIMARY KEY,   -- e.g. 'MSG01', matches SRS §5.3 Message code column
    message_type      VARCHAR(30) NOT NULL
        CHECK (message_type IN ('InLine','ToastMessage','RedUnderTextbox','Modal','BottomSheet',
                                 'Alert','PushNotification')),
    content_template  NVARCHAR(500) NOT NULL,   -- may contain {placeholder} tokens, e.g. {email_address}, {max_length}
    created_at        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

/* =====================================================================
   Seed data
   ===================================================================== */

INSERT INTO dbo.SystemConfigs (config_key, config_value, description) VALUES
    ('CSP.BufferTimeMinutes', '15', 'Buffer time inserted between consecutive itinerary stops'),
    ('CSP.DefaultTravelSpeedKmh', '30', 'Default motorbike travel speed used when a RouteSegment has no measured duration'),
    ('Rerouting.SearchRadiusKm', '5', 'Radius used by the Rerouting Engine to look for alternative POIs'),
    ('Weather.AlertThresholdSeverity', 'Severe', 'Minimum WeatherEvents.severity that triggers an Incident'),
    ('Booking.PaymentExpiryMinutes', '15', 'Minutes a PendingPayment booking is held before auto-expiration'),
    ('Tour.RecommendationSimilarityThreshold', '80', 'Minimum similarity_score (%) for a TourRecommendationLogs row to be marked is_recommended'),
    ('Ticket.QrTokenExpiryHours', '24', 'Hours a generated TicketQrCodes.qr_token remains valid before the client must request a fresh one'),
    ('CommercialService.DefaultCommissionRate', '8', 'Fallback affiliate commission (%) applied to a ServiceBooking when the provider has no negotiated commission_rate');
GO

-- The real, authored SRS §5.3 Application Messages List (MSG01–MSG130).
-- See database/screen_message_business_rule_mapping.md for which screen
-- shows each message and which business rule (if any) it enforces.
--
-- message_type mapping from the SRS "Message Type" column: "In red, under
-- text" -> RedUnderTextbox; "In line" -> InLine; "Toast message" ->
-- ToastMessage; "Confirmation pop-up" and "Modal dialog" -> Modal (one DB
-- value covers both — a confirmation dialog is a modal); "Alert pop-up" ->
-- Alert; "Push notification" -> PushNotification. MSG127 was listed as
-- "Alert / In line" (contextual); stored as Alert, its more prominent form.

INSERT INTO dbo.Messages (message_code, message_type, content_template) VALUES
    ('MSG01', 'RedUnderTextbox', 'This field is required.'),
    ('MSG02', 'RedUnderTextbox', 'Invalid email format. Please enter a valid email address (e.g., user@example.com).'),
    ('MSG03', 'InLine', 'An account with this email already exists. Please sign in or use another email.'),
    ('MSG04', 'RedUnderTextbox', 'Invalid phone number. Phone number must be 10 digits starting with 0.'),
    ('MSG05', 'RedUnderTextbox', 'Password must be at least 8 characters, containing uppercase, lowercase, number, and special character.'),
    ('MSG06', 'RedUnderTextbox', 'Passwords do not match. Please re-enter.'),
    ('MSG07', 'ToastMessage', 'Account registered successfully! Please verify your email/OTP to activate your account.'),
    ('MSG08', 'ToastMessage', 'Your business profile has been submitted for verification. Admin review takes 1-2 business days.'),
    ('MSG09', 'InLine', 'Incorrect email or password. Please try again.'),
    ('MSG10', 'InLine', 'Your Tour Operator account is pending verification. You will be notified once approved.'),
    ('MSG11', 'InLine', 'Your account has been locked due to policy violations. Please contact support@tripmate.com.'),
    ('MSG12', 'ToastMessage', 'Welcome back to TripMate! Signed in successfully.'),
    ('MSG13', 'ToastMessage', 'Signed out successfully.'),
    ('MSG14', 'InLine', 'Invalid or expired verification code. Please request a new OTP.'),
    ('MSG15', 'ToastMessage', 'A new 6-digit verification code has been sent to your email/phone.'),
    ('MSG16', 'ToastMessage', 'Your password has been reset successfully. Please sign in with your new password.'),
    ('MSG17', 'RedUnderTextbox', 'Current password does not match our records.'),
    ('MSG18', 'ToastMessage', 'Password updated successfully.'),
    ('MSG19', 'ToastMessage', 'Profile updated successfully.'),
    ('MSG20', 'RedUnderTextbox', 'Avatar must be JPG, PNG or WEBP format and under 5MB.'),
    ('MSG21', 'ToastMessage', 'Your travel preferences have been saved. TripMate will personalize your recommendations!'),
    ('MSG22', 'InLine', 'Please select at least one preferred travel style and budget level.'),
    ('MSG23', 'Modal', 'You have unsaved changes. Are you sure you want to leave without saving?'),
    ('MSG24', 'InLine', 'No points of interest (POIs) found matching your criteria.'),
    ('MSG25', 'ToastMessage', 'POI added to the master catalog successfully.'),
    ('MSG26', 'ToastMessage', 'POI details updated successfully.'),
    ('MSG27', 'Modal', 'Are you sure you want to deactivate POI "{POI_Name}"? It will be hidden from new itineraries.'),
    ('MSG28', 'InLine', 'Cannot delete POI: It is referenced in active tours or user itineraries. Soft deactivation applied.'),
    ('MSG29', 'RedUnderTextbox', 'Invalid latitude (-90 to 90) or longitude (-180 to 180).'),
    ('MSG30', 'ToastMessage', 'Category created successfully.'),
    ('MSG31', 'RedUnderTextbox', 'End date must be on or after the start date.'),
    ('MSG32', 'RedUnderTextbox', 'Trip budget must be at least 100,000 VND.'),
    ('MSG33', 'InLine', 'Unable to fit all selected mandatory locations into the given timeframe. Please adjust schedule or remove some locations.'),
    ('MSG34', 'ToastMessage', 'Smart itinerary generated successfully! Optimized based on your preferences.'),
    ('MSG35', 'Alert', 'Generating optimized route using fast heuristic mode due to high complexity.'),
    ('MSG36', 'ToastMessage', 'Itinerary saved to My Trips.'),
    ('MSG37', 'Modal', 'Are you sure you want to delete itinerary "{Itinerary_Title}"? This action cannot be undone.'),
    ('MSG38', 'ToastMessage', 'Itinerary deleted successfully.'),
    ('MSG39', 'ToastMessage', 'Trip started! Real-time GPS navigation and tracking is active.'),
    ('MSG40', 'Alert', 'You have deviated from the planned route by over 500m. Would you like to recalculate the route?'),
    ('MSG41', 'PushNotification', 'Welcome to {POI_Name}! Enjoy your visit. Estimated stay duration: {Duration} mins.'),
    ('MSG42', 'ToastMessage', 'Checked in at {POI_Name} successfully.'),
    ('MSG43', 'Alert', 'You are running {Delay_Minutes} minutes behind schedule. Upcoming locations may close soon.'),
    ('MSG44', 'Modal', 'Are you sure you want to complete this trip? Tracking will stop and trip summary will be saved.'),
    ('MSG45', 'ToastMessage', 'Congratulations on completing your trip! View your trip summary and photos.'),
    ('MSG46', 'InLine', 'Location permission is required for real-time navigation and trip tracking. Please enable it in Settings.'),
    ('MSG47', 'Alert', 'Severe weather warning: Heavy rainfall ({Rainfall_Rate} mm/h) detected along your route. Rerouting recommended.'),
    ('MSG48', 'Modal', 'Bad weather ahead! TripMate recommends rerouting to suitable indoor locations within {Rerouting_Radius} km. Accept the new route?'),
    ('MSG49', 'ToastMessage', 'Itinerary updated with weather-safe route and indoor shelters.'),
    ('MSG50', 'ToastMessage', 'Reroute declined. Continuing on current itinerary. Please stay safe!'),
    ('MSG51', 'PushNotification', 'Notice: Tour "{Tour_Title}" on {Date} has been cancelled due to severe weather alerts. Refund processing has been initiated according to the applicable refund policy.'),
    ('MSG52', 'InLine', '{POI_Name} is currently closed. Opening hours: {Open_Time} - {Close_Time}.'),
    ('MSG53', 'ToastMessage', 'Navigating to nearest indoor shelter: {Shelter_Name}.'),
    ('MSG54', 'ToastMessage', 'Travel group created! You are the Group Host. Share the invite code to add members.'),
    ('MSG55', 'ToastMessage', 'Invite code "{Invite_Code}" copied to clipboard.'),
    ('MSG56', 'InLine', 'This invitation is invalid, expired, or no longer available. Please check the invitation and try again.'),
    ('MSG57', 'InLine', 'You are already a member of this travel group.'),
    ('MSG58', 'ToastMessage', 'You have joined "{Group_Name}"!'),
    ('MSG59', 'Modal', 'Are you sure you want to remove {Member_Name} from this group?'),
    ('MSG60', 'ToastMessage', '{Member_Name} has been removed from the group.'),
    ('MSG61', 'Modal', 'You are the Group Host. Leaving will transfer Host privileges to {Next_Member_Name}. Confirm leave?'),
    ('MSG62', 'ToastMessage', 'Live location sharing is now active with group members.'),
    ('MSG63', 'ToastMessage', 'Live location sharing disabled.'),
    ('MSG64', 'InLine', 'No tour packages found matching your destination and dates.'),
    ('MSG65', 'InLine', 'This tour package is currently unavailable for booking.'),
    ('MSG66', 'ToastMessage', 'Tour added to your Saved Tours.'),
    ('MSG67', 'ToastMessage', 'Tour removed from Saved Tours.'),
    ('MSG68', 'InLine', 'Matching score: {Score}% based on your travel style and preferences.'),
    ('MSG69', 'ToastMessage', 'Tour draft saved successfully.'),
    ('MSG70', 'InLine', 'Please complete itinerary timeline, pricing, and slot capacity before submitting for approval.'),
    ('MSG71', 'ToastMessage', 'Tour package submitted for approval! Status changed to Pending.'),
    ('MSG72', 'InLine', 'Tour rejected: "{Rejection_Reason}". Please edit required information and resubmit.'),
    ('MSG73', 'ToastMessage', 'Congratulations! Your tour "{Tour_Title}" is now live and bookable.'),
    ('MSG74', 'Modal', 'Are you sure you want to pause this tour? It will be hidden from search results.'),
    ('MSG75', 'ToastMessage', 'Tour is now paused and hidden from public search.'),
    ('MSG76', 'RedUnderTextbox', 'Departure date must be in the future.'),
    ('MSG77', 'InLine', 'Only {Remaining_Slots} seats left for this tour. Please reduce passenger count.'),
    ('MSG78', 'RedUnderTextbox', 'Passenger full name and valid ID/Passport number are required.'),
    ('MSG79', 'ToastMessage', 'Booking created! Please complete payment within 15 minutes to secure your seats.'),
    ('MSG80', 'Alert', 'Your booking reservation has expired due to non-payment within 15 minutes. Seats released.'),
    ('MSG81', 'Modal', 'Are you sure you want to cancel booking #{Booking_ID}? Refund will follow cancellation policy.'),
    ('MSG82', 'ToastMessage', 'Booking #{Booking_ID} cancelled successfully.'),
    ('MSG83', 'InLine', 'This booking can no longer be cancelled under the applicable cancellation policy.'),
    ('MSG84', 'InLine', 'Access denied. You can only view and manage bookings for your own tours.'),
    ('MSG85', 'ToastMessage', 'Redirecting to secure VNPay payment gateway...'),
    ('MSG86', 'ToastMessage', 'Payment successful! Your booking is confirmed and e-tickets have been issued.'),
    ('MSG87', 'Alert', 'Payment was cancelled or failed. Please try again or choose another payment method.'),
    ('MSG88', 'InLine', 'This booking has already been paid. Duplicate payment blocked.'),
    ('MSG89', 'Alert', 'Payment gateway is taking longer than usual. Please check your banking app before retrying.'),
    ('MSG90', 'ToastMessage', 'Refund request of {Refund_Amount} VND submitted successfully. Processing time: 3-5 days.'),
    ('MSG91', 'ToastMessage', 'Refund of {Refund_Amount} VND successfully returned to original payment account.'),
    ('MSG92', 'InLine', 'Transaction rejected due to invalid payment amount verification.'),
    ('MSG93', 'ToastMessage', 'Your encrypted QR ticket is ready. Present this code at the tour gate.'),
    ('MSG94', 'ToastMessage', 'Check-in successful! Ticket verified for passenger: {Passenger_Name}.'),
    ('MSG95', 'Alert', 'Check-in failed: This QR ticket was already used at {Checkin_Time}.'),
    ('MSG96', 'Alert', 'Invalid QR code. Signature verification failed. Ticket is not genuine.'),
    ('MSG97', 'Alert', 'Check-in failed: This ticket belongs to a cancelled booking #{Booking_ID}.'),
    ('MSG98', 'InLine', 'This ticket is valid only on departure date: {Departure_Date}.'),
    ('MSG99', 'ToastMessage', 'Promotion voucher "{Voucher_Code}" created successfully.'),
    ('MSG100', 'ToastMessage', 'Voucher "{Voucher_Code}" applied! You saved {Discount_Amount} VND.'),
    ('MSG101', 'InLine', 'Invalid or expired voucher code. Please check code or minimum order condition.'),
    ('MSG102', 'InLine', 'Minimum order spend of {Min_Amount} VND required to use this voucher.'),
    ('MSG103', 'ToastMessage', 'Voucher removed from current order.'),
    ('MSG104', 'ToastMessage', 'Downloading offline trip package (itinerary, POIs, map tiles)...'),
    ('MSG105', 'ToastMessage', 'Trip package downloaded! Itinerary is now available offline without internet.'),
    ('MSG106', 'Alert', 'Download interrupted due to connection loss. Partial download discarded. Please retry.'),
    ('MSG107', 'Alert', 'Insufficient storage space. At least 150MB free space required for offline map data.'),
    ('MSG108', 'ToastMessage', 'Internet connection restored. {Count} offline check-ins and GPS logs synced to server.'),
    ('MSG109', 'ToastMessage', 'Tour completed! {Net_Amount} VND (after {Rate}% platform fee) released to Withdrawable Balance.'),
    ('MSG110', 'ToastMessage', 'Withdrawal request of {Payout_Amount} VND submitted. Admin processing in 24 hours.'),
    ('MSG111', 'InLine', 'Requested amount exceeds your available balance of {Available_Balance} VND.'),
    ('MSG112', 'ToastMessage', 'Payout of {Payout_Amount} VND has been transferred to bank account: {Bank_Account}.'),
    ('MSG113', 'InLine', 'Please configure your verified bank account details before requesting payout.'),
    ('MSG114', 'ToastMessage', 'Tour Operator "{Company_Name}" approved. Account activated.'),
    ('MSG115', 'Modal', 'Please enter specific rejection reason to send to applicant:'),
    ('MSG116', 'ToastMessage', 'Application rejected. Notification sent to operator.'),
    ('MSG117', 'ToastMessage', 'Algorithm parameters (buffer time, default travel speed, rerouting search radius, and weather thresholds) updated successfully.'),
    ('MSG118', 'RedUnderTextbox', 'Parameter value out of allowed range (e.g., buffer time must be 5-60 mins).'),
    ('MSG119', 'ToastMessage', 'System configuration change recorded in Audit Log.'),
    ('MSG120', 'ToastMessage', 'Thank you for your feedback! Your review has been published.'),
    ('MSG121', 'InLine', 'You can only review trips that have been completed.'),
    ('MSG122', 'InLine', 'You have already reviewed this trip.'),
    ('MSG123', 'RedUnderTextbox', 'Review comment cannot exceed 500 characters.'),
    ('MSG124', 'ToastMessage', 'Review removed successfully.'),
    ('MSG125', 'InLine', 'Your session has expired. Please sign in again to continue.'),
    ('MSG126', 'InLine', 'You do not have permission to access this function.'),
    ('MSG127', 'Alert', 'TripMate is temporarily unable to process your request. Please check your connection and try again.'),
    ('MSG128', 'InLine', 'No records found matching your criteria.'),
    ('MSG129', 'ToastMessage', 'Operation completed successfully.'),
    ('MSG130', 'Modal', 'Are you sure you want to continue? This action may not be reversible.');
GO
