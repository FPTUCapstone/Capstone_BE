SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'commercial.StayListings', N'U') IS NULL
BEGIN
    CREATE TABLE commercial.StayListings (
        poi_id BIGINT NOT NULL PRIMARY KEY REFERENCES catalog.POIs(poi_id),
        description NVARCHAR(2000) NULL,
        published BIT NOT NULL,
        accepting_requests BIT NOT NULL,
        CONSTRAINT CK_StayListings_Publication CHECK (published = 1 OR accepting_requests = 0)
    );
END;

IF OBJECT_ID(N'commercial.StayInquiries', N'U') IS NULL
BEGIN
    CREATE TABLE commercial.StayInquiries (
        inquiry_id BIGINT IDENTITY(1,1) PRIMARY KEY,
        poi_id BIGINT NOT NULL REFERENCES catalog.POIs(poi_id),
        traveler_id BIGINT NOT NULL REFERENCES dbo.Users(user_id),
        itinerary_id BIGINT NULL REFERENCES planning.Itineraries(itinerary_id),
        arrival DATE NOT NULL,
        departure DATE NOT NULL,
        guests INT NOT NULL,
        contact_name NVARCHAR(150) NOT NULL,
        contact NVARCHAR(254) NOT NULL,
        note NVARCHAR(1000) NULL,
        submit_key UNIQUEIDENTIFIER NOT NULL,
        submit_hash VARCHAR(64) NOT NULL,
        created_at DATETIME2 NOT NULL,
        status VARCHAR(20) NOT NULL,
        reply NVARCHAR(1000) NULL,
        CONSTRAINT CK_StayInquiries_Dates CHECK (departure > arrival),
        CONSTRAINT CK_StayInquiries_Guests CHECK (guests > 0),
        CONSTRAINT CK_StayInquiries_Status CHECK (status IN ('Sent','Replied','Declined'))
    );
    CREATE UNIQUE INDEX UX_StayInquiries_SubmitKey ON commercial.StayInquiries(traveler_id, submit_key);
    CREATE INDEX IX_StayInquiries_Poi ON commercial.StayInquiries(poi_id, inquiry_id);
END;

IF OBJECT_ID(N'commercial.StayOwnershipClaims', N'U') IS NULL
BEGIN
CREATE TABLE commercial.StayOwnershipClaims (
        claim_id BIGINT IDENTITY(1,1) PRIMARY KEY,
        poi_id BIGINT NOT NULL REFERENCES catalog.POIs(poi_id),
        applicant_id BIGINT NOT NULL REFERENCES dbo.Users(user_id),
        evidence NVARCHAR(1000) NOT NULL,
        status VARCHAR(20) NOT NULL,
        submitted_at DATETIME2 NOT NULL,
        reviewer_id BIGINT NULL REFERENCES dbo.Users(user_id),
        review_note NVARCHAR(1000) NULL,
        CONSTRAINT CK_StayOwnershipClaims_Status CHECK (status IN ('Pending','Approved','Rejected'))
    );
    CREATE UNIQUE INDEX UX_StayOwnershipClaims_Pending ON commercial.StayOwnershipClaims(poi_id, applicant_id) WHERE status = 'Pending';
END;

COMMIT TRANSACTION;
GO
