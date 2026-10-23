using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Authentication;

public sealed class StaffRoleMigrationSqlServerTests
{
    private const string MigrationFileName = "20261007_allow_staff_role.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FreshSchema_HasCanonicalNamedRoleConstraint()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        var constraint = await ReadRoleConstraintAsync(database);

        constraint.Name.Should().Be("CK_Users_Role");
        Normalize(constraint.Definition).Should().Be(
            Normalize(SqlServerCanonicalRoleConstraint));
        constraint.IsDisabled.Should().BeFalse();
        constraint.IsNotTrusted.Should().BeFalse();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task LegacyConstraint_MigrationPreservesUsersAndSupportsSafeRerun()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE dbo.Users (
                user_id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                role VARCHAR(20) NOT NULL
                    CONSTRAINT CK_Users_LegacyRole
                    CHECK (role IN ('Traveler','TourOperator','Administrator')),
                email NVARCHAR(256) NULL);

            INSERT dbo.Users(role, email)
            VALUES
                ('Traveler', N'traveler@example.com'),
                ('TourOperator', N'operator@example.com'),
                ('Administrator', N'admin@example.com');
            """);

        await database.ExecuteScriptAsync(GetMigrationPath());
        await database.ExecuteNonQueryAsync(
            "INSERT dbo.Users(role, email) VALUES ('Staff', N'staff@example.com');");
        await database.ExecuteScriptAsync(GetMigrationPath());

        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Users;"))
            .Should().Be(4);
        var constraint = await ReadRoleConstraintAsync(database);
        constraint.Name.Should().Be("CK_Users_Role");
        Normalize(constraint.Definition).Should().Be(
            Normalize(SqlServerCanonicalRoleConstraint));
        constraint.IsDisabled.Should().BeFalse();
        constraint.IsNotTrusted.Should().BeFalse();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task UnsupportedExistingRole_MigrationRollsBackWithoutDroppingLegacyConstraint()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE dbo.Users (
                user_id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                role VARCHAR(20) NOT NULL
                    CONSTRAINT CK_Users_LegacyRole
                    CHECK (role IN ('Traveler','TourOperator','Administrator','LegacyStaff')));

            INSERT dbo.Users(role) VALUES ('LegacyStaff');
            """);

        var action = () => database.ExecuteScriptAsync(GetMigrationPath());

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*outside the canonical role set*");
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Users;"))
            .Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'dbo.Users')
              AND name = N'CK_Users_LegacyRole';
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task MultipleRoleConstraints_AreRejectedWithoutPartialSchemaOrDataChanges()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE dbo.Users (
                user_id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                role VARCHAR(20) NOT NULL
                    CONSTRAINT CK_Users_LegacyRole
                    CHECK (role IN ('Traveler','TourOperator','Administrator')),
                email NVARCHAR(256) NULL,
                CONSTRAINT CK_Users_RoleRequiresEmail
                    CHECK (role <> 'Administrator' OR email IS NOT NULL));

            INSERT dbo.Users(role, email)
            VALUES ('Administrator', N'admin@example.com');
            """);

        var action = () => database.ExecuteScriptAsync(GetMigrationPath());

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*multiple CHECK constraints referencing dbo.Users.role*");
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Users;"))
            .Should().Be(1);
        (await CountConstraintsAsync(database,
            "CK_Users_LegacyRole", "CK_Users_RoleRequiresEmail"))
            .Should().Be(2);
        (await CountConstraintsAsync(database, "CK_Users_Role")).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CompositeRoleConstraint_IsRejectedWithoutRemovingBusinessRuleOrData()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE dbo.Users (
                user_id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                role VARCHAR(20) NOT NULL,
                email NVARCHAR(256) NULL,
                CONSTRAINT CK_Users_RoleRequiresEmail
                    CHECK (role <> 'Administrator' OR email IS NOT NULL));

            INSERT dbo.Users(role, email)
            VALUES ('Administrator', N'admin@example.com');
            """);

        var action = () => database.ExecuteScriptAsync(GetMigrationPath());

        await action.Should().ThrowAsync<SqlException>()
            .WithMessage("*combines dbo.Users.role with other columns*");
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Users;"))
            .Should().Be(1);
        (await CountConstraintsAsync(database, "CK_Users_RoleRequiresEmail"))
            .Should().Be(1);
        (await CountConstraintsAsync(database, "CK_Users_Role")).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task UnrelatedUsersConstraint_IsPreservedDuringRoleConstraintUpgrade()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE dbo.Users (
                user_id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                role VARCHAR(20) NOT NULL
                    CONSTRAINT CK_Users_LegacyRole
                    CHECK (role IN ('Traveler','TourOperator','Administrator')),
                email NVARCHAR(256) NULL,
                CONSTRAINT CK_Users_Email_NotBlank
                    CHECK (email IS NULL OR LEN(LTRIM(RTRIM(email))) > 0));

            INSERT dbo.Users(role, email)
            VALUES ('Traveler', N'traveler@example.com');
            """);

        await database.ExecuteScriptAsync(GetMigrationPath());

        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Users;"))
            .Should().Be(1);
        (await CountConstraintsAsync(database, "CK_Users_Email_NotBlank"))
            .Should().Be(1);
        var constraint = await ReadRoleConstraintAsync(database);
        constraint.Name.Should().Be("CK_Users_Role");
        constraint.IsDisabled.Should().BeFalse();
        constraint.IsNotTrusted.Should().BeFalse();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SingleTableLevelRoleConstraint_IsReplacedAndUsersArePreserved()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE dbo.Users (
                user_id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                role VARCHAR(20) NOT NULL,
                email NVARCHAR(256) NULL,
                CONSTRAINT CK_Users_LegacyRole
                    CHECK (role IN ('Traveler','TourOperator','Administrator')));

            INSERT dbo.Users(role, email)
            VALUES ('TourOperator', N'operator@example.com');
            """);

        await database.ExecuteScriptAsync(GetMigrationPath());

        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Users;"))
            .Should().Be(1);
        (await CountConstraintsAsync(database, "CK_Users_LegacyRole"))
            .Should().Be(0);
        var constraint = await ReadRoleConstraintAsync(database);
        constraint.Name.Should().Be("CK_Users_Role");
        constraint.IsDisabled.Should().BeFalse();
        constraint.IsNotTrusted.Should().BeFalse();
    }

    private static Task<int> CountConstraintsAsync(
        SqlServerTestDatabase database,
        params string[] names)
    {
        var literals = string.Join(",", names.Select(name => $"N'{name.Replace("'", "''")}'"));
        return database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'dbo.Users')
              AND name IN ({literals});
            """);
    }

    private static async Task<RoleConstraint> ReadRoleConstraintAsync(
        SqlServerTestDatabase database)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cc.name, cc.definition, cc.is_disabled, cc.is_not_trusted
            FROM sys.check_constraints AS cc
            INNER JOIN sys.columns AS c
                ON c.object_id = cc.parent_object_id
               AND c.column_id = cc.parent_column_id
            WHERE cc.parent_object_id = OBJECT_ID(N'dbo.Users')
              AND c.name = N'role';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var result = new RoleConstraint(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3));
        (await reader.ReadAsync()).Should().BeFalse();
        return result;
    }

    private static string Normalize(string definition) =>
        string.Concat(definition.Where(character => !char.IsWhiteSpace(character)))
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal);

    // SQL Server expands an IN predicate into this equivalent OR expression in
    // sys.check_constraints.definition, in reverse literal order.
    private const string SqlServerCanonicalRoleConstraint =
        "([role]='Staff' OR [role]='Administrator' OR " +
        "[role]='TourOperator' OR [role]='Traveler')";

    private static string GetMigrationPath() =>
        Path.Combine(AppContext.BaseDirectory, "Database", "migrations", MigrationFileName);

    private sealed record RoleConstraint(
        string Name,
        string Definition,
        bool IsDisabled,
        bool IsNotTrusted);
}