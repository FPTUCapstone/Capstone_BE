SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
        OR OBJECT_ID(N'commercial.ServiceProviders', N'U') IS NULL
        OR OBJECT_ID(N'catalog.POIs', N'U') IS NULL
        THROW 51000, 'TM-225 requires Users, ServiceProviders and POIs.', 1;

    IF COL_LENGTH(N'dbo.Users', N'must_change_password') IS NULL
        ALTER TABLE dbo.Users ADD must_change_password BIT NOT NULL
            CONSTRAINT DF_Users_MustChangePassword DEFAULT (0) WITH VALUES;

    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Users')
        AND name = N'must_change_password' AND system_type_id = TYPE_ID(N'bit') AND is_nullable = 0 AND is_computed = 0 AND is_identity = 0)
        THROW 51000, 'Users.must_change_password has an incompatible shape.', 1;

    IF NOT EXISTS (SELECT 1 FROM sys.default_constraints AS d
        JOIN sys.columns AS c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
        WHERE d.parent_object_id = OBJECT_ID(N'dbo.Users') AND c.name = N'must_change_password'
        AND REPLACE(REPLACE(REPLACE(d.definition, N'(', N''), N')', N''), N' ', N'') = N'0')
        THROW 51000, 'Users.must_change_password requires a zero default.', 1;

    IF OBJECT_ID(N'DF_Users_MustChangePassword') IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM sys.default_constraints AS d JOIN sys.columns AS c
            ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
        WHERE d.object_id = OBJECT_ID(N'DF_Users_MustChangePassword')
            AND d.parent_object_id = OBJECT_ID(N'dbo.Users') AND c.name = N'must_change_password')
        THROW 51000, 'DF_Users_MustChangePassword has an incompatible owner.', 1;

    DECLARE @usersId INT = OBJECT_ID(N'dbo.Users');
    DECLARE @roleColumn INT = COLUMNPROPERTY(@usersId, N'role', 'ColumnId');
    DECLARE @roleChecks TABLE (constraint_name SYSNAME, mixes_columns BIT);
    INSERT @roleChecks
    SELECT cc.name, CONVERT(BIT, CASE WHEN EXISTS (
        SELECT 1 FROM sys.sql_expression_dependencies AS other_dependency
        WHERE other_dependency.referencing_id = cc.object_id
            AND other_dependency.referenced_id = @usersId
            AND other_dependency.referenced_minor_id > 0
            AND other_dependency.referenced_minor_id <> @roleColumn) THEN 1 ELSE 0 END)
    FROM sys.check_constraints AS cc
    WHERE cc.parent_object_id = @usersId AND (cc.parent_column_id = @roleColumn OR EXISTS (
        SELECT 1 FROM sys.sql_expression_dependencies AS dependency
        WHERE dependency.referencing_id = cc.object_id AND dependency.referenced_id = @usersId
            AND dependency.referenced_minor_id = @roleColumn));

    IF (SELECT COUNT(*) FROM @roleChecks) > 1 OR EXISTS (SELECT 1 FROM @roleChecks WHERE mixes_columns = 1)
        THROW 51000, 'Users role CHECK constraints cannot be classified safely.', 1;
    IF OBJECT_ID(N'dbo.CK_Users_Role') IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM sys.check_constraints WHERE object_id = OBJECT_ID(N'dbo.CK_Users_Role')
            AND parent_object_id = @usersId AND name IN (SELECT constraint_name FROM @roleChecks))
        THROW 51000, 'CK_Users_Role has an incompatible shape.', 1;
    IF EXISTS (SELECT 1 FROM dbo.Users WHERE role NOT IN ('Traveler','TourOperator','Administrator','Staff','ServiceProvider'))
        THROW 51000, 'Users contains an unsupported role.', 1;

    DECLARE @oldRoleCheck SYSNAME = (SELECT constraint_name FROM @roleChecks);
    IF @oldRoleCheck IS NOT NULL
    BEGIN
        DECLARE @dropRoleCheck NVARCHAR(512) = N'ALTER TABLE dbo.Users DROP CONSTRAINT ' + QUOTENAME(@oldRoleCheck);
        EXEC sys.sp_executesql @dropRoleCheck;
    END;
    ALTER TABLE dbo.Users WITH CHECK ADD CONSTRAINT CK_Users_Role
        CHECK (role IN ('Traveler','TourOperator','Administrator','Staff','ServiceProvider'));
    ALTER TABLE dbo.Users CHECK CONSTRAINT CK_Users_Role;

    IF COL_LENGTH(N'commercial.ServiceProviders', N'user_id') IS NULL
        ALTER TABLE commercial.ServiceProviders ADD user_id BIGINT NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'commercial.ServiceProviders')
        AND name = N'user_id' AND system_type_id = TYPE_ID(N'bigint') AND is_nullable = 1 AND is_computed = 0 AND is_identity = 0)
        THROW 51000, 'ServiceProviders.user_id has an incompatible shape.', 1;

    IF OBJECT_ID(N'commercial.ProviderPoiLinks') IS NOT NULL
        AND OBJECT_ID(N'commercial.ProviderPoiLinks', N'U') IS NULL
        THROW 51000, 'ProviderPoiLinks is not a table.', 1;
    IF OBJECT_ID(N'commercial.ProviderPoiLinks', N'U') IS NULL
        CREATE TABLE commercial.ProviderPoiLinks (
            provider_id BIGINT NOT NULL,
            poi_id BIGINT NOT NULL,
            CONSTRAINT PK_ProviderPoiLinks PRIMARY KEY (provider_id, poi_id)
        );

    IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'commercial.ProviderPoiLinks')) <> 2
        OR (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'commercial.ProviderPoiLinks')
            AND name IN (N'provider_id', N'poi_id') AND system_type_id = TYPE_ID(N'bigint')
            AND is_nullable = 0 AND is_computed = 0 AND is_identity = 0) <> 2
        THROW 51000, 'ProviderPoiLinks columns have an incompatible shape.', 1;

    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints
        WHERE parent_object_id = OBJECT_ID(N'commercial.ProviderPoiLinks') AND type = N'PK')
        AND OBJECT_ID(N'commercial.PK_ProviderPoiLinks') IS NULL
        ALTER TABLE commercial.ProviderPoiLinks ADD CONSTRAINT PK_ProviderPoiLinks PRIMARY KEY (provider_id, poi_id);

    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints AS k
        WHERE k.parent_object_id = OBJECT_ID(N'commercial.ProviderPoiLinks') AND k.name = N'PK_ProviderPoiLinks'
            AND k.type = N'PK'
            AND (SELECT COUNT(*) FROM sys.index_columns AS ic WHERE ic.object_id = k.parent_object_id
                AND ic.index_id = k.unique_index_id AND ic.key_ordinal > 0) = 2
            AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c
                ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
                    AND ic.key_ordinal = 1 AND c.name = N'provider_id')
            AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c
                ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
                    AND ic.key_ordinal = 2 AND c.name = N'poi_id'))
        THROW 51000, 'PK_ProviderPoiLinks has an incompatible shape.', 1;

    -- Dynamic DDL keeps newly added columns out of SQL Server's initial batch binding.
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'commercial.ServiceProviders')
        AND name = N'UX_ServiceProviders_User')
        EXEC(N'CREATE UNIQUE INDEX UX_ServiceProviders_User ON commercial.ServiceProviders(user_id) WHERE user_id IS NOT NULL;');
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'commercial.ProviderPoiLinks')
        AND name = N'UX_ProviderPoiLinks_Poi')
        CREATE UNIQUE INDEX UX_ProviderPoiLinks_Poi ON commercial.ProviderPoiLinks(poi_id);

    DECLARE @indexes TABLE (table_id INT, index_name SYSNAME, column_name SYSNAME, filtered BIT);
    INSERT @indexes VALUES
        (OBJECT_ID(N'commercial.ServiceProviders'), N'UX_ServiceProviders_User', N'user_id', 1),
        (OBJECT_ID(N'commercial.ProviderPoiLinks'), N'UX_ProviderPoiLinks_Poi', N'poi_id', 0);
    IF EXISTS (SELECT 1 FROM @indexes AS expected WHERE NOT EXISTS (
        SELECT 1 FROM sys.indexes AS i WHERE i.object_id = expected.table_id AND i.name = expected.index_name
            AND i.is_unique = 1 AND i.is_disabled = 0 AND i.is_hypothetical = 0 AND i.has_filter = expected.filtered
            AND (expected.filtered = 0 OR REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(LOWER(i.filter_definition),
                N'[', N''), N']', N''), N'(', N''), N')', N''), N' ', N'') = N'user_idisnotnull')
            AND (SELECT COUNT(*) FROM sys.index_columns AS ic WHERE ic.object_id = i.object_id
                AND ic.index_id = i.index_id) = 1
            AND EXISTS (SELECT 1 FROM sys.index_columns AS ic JOIN sys.columns AS c
                ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                    AND ic.key_ordinal = 1 AND c.name = expected.column_name)))
        THROW 51000, 'A TM-225 unique index has an incompatible shape.', 1;

    IF OBJECT_ID(N'commercial.FK_ServiceProviders_User') IS NULL
        EXEC(N'ALTER TABLE commercial.ServiceProviders WITH CHECK ADD CONSTRAINT FK_ServiceProviders_User FOREIGN KEY(user_id) REFERENCES dbo.Users(user_id);');
    IF OBJECT_ID(N'commercial.FK_ProviderPoiLinks_Provider') IS NULL
        ALTER TABLE commercial.ProviderPoiLinks WITH CHECK ADD CONSTRAINT FK_ProviderPoiLinks_Provider
            FOREIGN KEY(provider_id) REFERENCES commercial.ServiceProviders(provider_id);
    IF OBJECT_ID(N'commercial.FK_ProviderPoiLinks_Poi') IS NULL
        ALTER TABLE commercial.ProviderPoiLinks WITH CHECK ADD CONSTRAINT FK_ProviderPoiLinks_Poi
            FOREIGN KEY(poi_id) REFERENCES catalog.POIs(poi_id);

    DECLARE @foreignKeys TABLE (constraint_name SYSNAME, table_id INT, column_name SYSNAME,
        referenced_id INT, referenced_column SYSNAME);
    INSERT @foreignKeys VALUES
        (N'FK_ServiceProviders_User', OBJECT_ID(N'commercial.ServiceProviders'), N'user_id', OBJECT_ID(N'dbo.Users'), N'user_id'),
        (N'FK_ProviderPoiLinks_Provider', OBJECT_ID(N'commercial.ProviderPoiLinks'), N'provider_id', OBJECT_ID(N'commercial.ServiceProviders'), N'provider_id'),
        (N'FK_ProviderPoiLinks_Poi', OBJECT_ID(N'commercial.ProviderPoiLinks'), N'poi_id', OBJECT_ID(N'catalog.POIs'), N'poi_id');
    IF EXISTS (SELECT 1 FROM @foreignKeys AS expected WHERE NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys AS fk WHERE fk.parent_object_id = expected.table_id
            AND fk.name = expected.constraint_name AND fk.referenced_object_id = expected.referenced_id
            AND fk.is_disabled = 0 AND fk.is_not_trusted = 0
            AND fk.delete_referential_action = 0 AND fk.update_referential_action = 0
            AND (SELECT COUNT(*) FROM sys.foreign_key_columns AS fc WHERE fc.constraint_object_id = fk.object_id) = 1
            AND EXISTS (SELECT 1 FROM sys.foreign_key_columns AS fc
                JOIN sys.columns AS pc ON pc.object_id = fc.parent_object_id AND pc.column_id = fc.parent_column_id
                JOIN sys.columns AS rc ON rc.object_id = fc.referenced_object_id AND rc.column_id = fc.referenced_column_id
                WHERE fc.constraint_object_id = fk.object_id AND pc.name = expected.column_name
                    AND rc.name = expected.referenced_column)))
        THROW 51000, 'A TM-225 foreign key has an incompatible shape.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
