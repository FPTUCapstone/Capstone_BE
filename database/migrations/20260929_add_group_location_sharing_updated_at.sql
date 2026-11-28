-- UC-22: retain when an active member last changed their explicit opt-in.
IF OBJECT_ID(N'social.GroupMembers', N'U') IS NULL
    THROW 51022, 'UC-22 requires social.GroupMembers.', 1;

IF COL_LENGTH(N'social.GroupMembers', N'location_sharing_updated_at') IS NULL
BEGIN
    ALTER TABLE social.GroupMembers
        ADD location_sharing_updated_at DATETIME2 NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns AS c
    JOIN sys.types AS t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'social.GroupMembers')
      AND c.name = N'location_sharing_updated_at'
      AND t.name = N'datetime2'
      AND c.scale = 7
      AND c.is_nullable = 1
)
    THROW 51022, 'UC-22 location sharing timestamp column has the wrong shape.', 1;

-- Preserve a legacy opt-in while ensuring pre-upgrade coordinates are not
-- treated as current. Re-running this migration leaves the timestamp intact.
EXEC(N'UPDATE social.GroupMembers
SET location_sharing_updated_at = SYSUTCDATETIME()
WHERE location_sharing_enabled = 1
  AND location_sharing_updated_at IS NULL;');
GO
