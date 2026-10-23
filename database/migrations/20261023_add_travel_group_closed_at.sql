IF COL_LENGTH('social.TravelGroups', 'closed_at') IS NULL
BEGIN
    ALTER TABLE social.TravelGroups
        ADD closed_at DATETIME2 NULL;
END;
GO
