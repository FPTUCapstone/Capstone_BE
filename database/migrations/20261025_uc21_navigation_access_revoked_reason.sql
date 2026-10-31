/* UC-21: allow navigation to close when group-only itinerary access is revoked. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'trip.TripSessions', N'U') IS NULL
    THROW 51026, 'UC-21 navigation migration requires trip.TripSessions.', 1;

IF EXISTS (
    SELECT 1 FROM trip.TripSessions
    WHERE completion_reason IS NOT NULL
      AND completion_reason NOT IN
          ('RouteFinished', 'TravelerStopped', 'Expired', 'GroupAccessRevoked'))
    THROW 51027, 'UC-21 found an unknown navigation completion reason.', 1;

DECLARE @reasonDefinition nvarchar(max);
SELECT @reasonDefinition = definition
FROM sys.check_constraints
WHERE parent_object_id = OBJECT_ID(N'trip.TripSessions')
  AND name = N'CK_TripSessions_CompletionReason';

IF @reasonDefinition IS NOT NULL
BEGIN
    IF @reasonDefinition NOT LIKE N'%RouteFinished%'
        OR @reasonDefinition NOT LIKE N'%TravelerStopped%'
        OR @reasonDefinition NOT LIKE N'%Expired%'
        THROW 51028, 'UC-21 found an unexpected navigation reason constraint.', 1;

    IF @reasonDefinition NOT LIKE N'%GroupAccessRevoked%'
        ALTER TABLE trip.TripSessions DROP CONSTRAINT CK_TripSessions_CompletionReason;
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'trip.TripSessions')
      AND name = N'CK_TripSessions_CompletionReason')
BEGIN
    ALTER TABLE trip.TripSessions WITH CHECK
        ADD CONSTRAINT CK_TripSessions_CompletionReason CHECK (
            completion_reason IS NULL
            OR completion_reason IN
                ('RouteFinished', 'TravelerStopped', 'Expired', 'GroupAccessRevoked'));
END;

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'trip.TripSessions')
      AND name = N'CK_TripSessions_CompletionReason'
      AND (is_disabled = 1 OR is_not_trusted = 1
           OR definition NOT LIKE N'%GroupAccessRevoked%'))
    THROW 51029, 'UC-21 navigation reason constraint is not enabled and trusted.', 1;

COMMIT TRANSACTION;
