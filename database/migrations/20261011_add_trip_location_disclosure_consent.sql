/*
  UC-58/59 privacy remediation
  - Legacy trip coordinates have no attributable subject and remain undisclosable.
  - Group sharing is intentionally not migrated into OperationsStaff consent.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
GO

IF COL_LENGTH(N'trip.TripLocationLogs', N'subject_user_id') IS NULL
BEGIN
    ALTER TABLE trip.TripLocationLogs ADD subject_user_id BIGINT NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_TripLocationLogs_SubjectUser')
BEGIN
    ALTER TABLE trip.TripLocationLogs WITH CHECK
        ADD CONSTRAINT FK_TripLocationLogs_SubjectUser
        FOREIGN KEY (subject_user_id) REFERENCES dbo.Users(user_id);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TripLocationLogs_SessionSubjectRecorded'
    AND object_id = OBJECT_ID(N'trip.TripLocationLogs'))
BEGIN
    CREATE INDEX IX_TripLocationLogs_SessionSubjectRecorded
        ON trip.TripLocationLogs(session_id, subject_user_id, recorded_at);
END;
GO

IF COL_LENGTH(N'trip.TripSessions', N'current_location_subject_user_id') IS NULL
BEGIN
    ALTER TABLE trip.TripSessions ADD current_location_subject_user_id BIGINT NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_TripSessions_CurrentLocationSubjectUser')
BEGIN
    ALTER TABLE trip.TripSessions WITH CHECK
        ADD CONSTRAINT FK_TripSessions_CurrentLocationSubjectUser
        FOREIGN KEY (current_location_subject_user_id) REFERENCES dbo.Users(user_id);
END;
GO

IF OBJECT_ID(N'trip.TripLocationDisclosureConsents', N'U') IS NULL
BEGIN
    CREATE TABLE trip.TripLocationDisclosureConsents (
        consent_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TripLocationDisclosureConsents PRIMARY KEY,
        session_id BIGINT NOT NULL CONSTRAINT FK_TripLocationDisclosureConsents_Session
            REFERENCES trip.TripSessions(session_id) ON DELETE CASCADE,
        user_id BIGINT NOT NULL CONSTRAINT FK_TripLocationDisclosureConsents_User
            REFERENCES dbo.Users(user_id),
        audience VARCHAR(32) NOT NULL CONSTRAINT CK_TripLocationDisclosureConsents_Audience
            CHECK (audience IN ('OperationsStaff')),
        granted_at DATETIME2 NOT NULL,
        revoked_at DATETIME2 NULL,
        CONSTRAINT CK_TripLocationDisclosureConsents_Interval
            CHECK (revoked_at IS NULL OR revoked_at >= granted_at)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TripLocationDisclosureConsents_Active'
    AND object_id = OBJECT_ID(N'trip.TripLocationDisclosureConsents'))
BEGIN
    CREATE UNIQUE INDEX UX_TripLocationDisclosureConsents_Active
        ON trip.TripLocationDisclosureConsents(session_id, user_id, audience)
        WHERE revoked_at IS NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TripLocationDisclosureConsents_ActiveLookup'
    AND object_id = OBJECT_ID(N'trip.TripLocationDisclosureConsents'))
BEGIN
    CREATE INDEX IX_TripLocationDisclosureConsents_ActiveLookup
        ON trip.TripLocationDisclosureConsents(session_id, user_id, audience, revoked_at);
END;
GO

COMMIT TRANSACTION;
GO
