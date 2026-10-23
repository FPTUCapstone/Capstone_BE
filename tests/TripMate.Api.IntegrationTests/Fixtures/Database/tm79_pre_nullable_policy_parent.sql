-- Frozen Task 3a parent shape before the no-AI delivery decision (fed9ca9).
-- Applied only over tm79_pre_migration_schema.sql in an isolated test database.
CREATE TABLE social.TripReviews (
    trip_review_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TripReviews PRIMARY KEY,
    booking_id BIGINT NOT NULL CONSTRAINT FK_TripReviews_Booking REFERENCES commerce.Bookings(booking_id),
    traveler_user_id BIGINT NOT NULL CONSTRAINT FK_TripReviews_Traveler REFERENCES dbo.Users(user_id),
    tour_id BIGINT NULL CONSTRAINT FK_TripReviews_Tour REFERENCES commerce.Tours(tour_id),
    itinerary_id BIGINT NULL CONSTRAINT FK_TripReviews_Itinerary REFERENCES planning.Itineraries(itinerary_id),
    overall_rating TINYINT NOT NULL,
    title NVARCHAR(200) COLLATE Vietnamese_100_CI_AS NOT NULL,
    content NVARCHAR(1000) COLLATE Vietnamese_100_CI_AS NOT NULL,
    route_pacing VARCHAR(9) NULL,
    csp_rating TINYINT NULL,
    publish_display_name BIT NOT NULL,
    public_display_name NVARCHAR(MAX) COLLATE Vietnamese_100_CI_AS NOT NULL,
    publication_status VARCHAR(9) NOT NULL,
    policy_version NVARCHAR(MAX) COLLATE Vietnamese_100_CI_AS NOT NULL,
    created_at DATETIME2(7) NOT NULL,
    edit_deadline DATETIME2(7) NOT NULL,
    updated_at DATETIME2(7) NOT NULL,
    version ROWVERSION NOT NULL,
    CONSTRAINT CK_TripReviews_Overall CHECK (overall_rating BETWEEN 1 AND 5),
    CONSTRAINT CK_TripReviews_Csp CHECK (csp_rating BETWEEN 1 AND 5),
    CONSTRAINT CK_TripReviews_Pacing CHECK (route_pacing IN ('tooTight','wellPaced','tooLoose')),
    CONSTRAINT CK_TripReviews_Subject CHECK (
        (tour_id IS NOT NULL AND itinerary_id IS NULL) OR
        (tour_id IS NULL AND itinerary_id IS NOT NULL)),
    CONSTRAINT CK_TripReviews_Publication CHECK (publication_status = 'Published'),
    CONSTRAINT CK_TripReviews_Title CHECK (LEN(title) > 0),
    CONSTRAINT CK_TripReviews_Content CHECK (LEN(content) > 0),
    CONSTRAINT CK_TripReviews_Policy CHECK (LEN(policy_version) > 0),
    CONSTRAINT CK_TripReviews_Display CHECK (LEN(public_display_name) > 0),
    CONSTRAINT CK_TripReviews_Deadline CHECK (edit_deadline = DATEADD(day, 7, created_at))
);
CREATE UNIQUE INDEX UX_TripReviews_Booking ON social.TripReviews(booking_id);
