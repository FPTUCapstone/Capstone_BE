using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Routes;

[Collection(nameof(TripMateApiFactory))]
public sealed class RouteMigrationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task UpTwice_PreservesAndBackfillsLegacyRoute_AndGuardedDownRestoresLegacySchema()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteScriptAsync(MigrationPath(rollback: true));
        var seed = await CreateRouteSqlServerTests.SeedAsync(database);
        await database.ExecuteNonQueryAsync(Insert(seed.FromId, seed.ToId));
        var before = await database.ExecuteScalarAsync<string>("SELECT CONCAT(segment_id,'|',from_poi_id,'|',to_poi_id,'|',transport_mode,'|',distance_km,'|',est_duration_minutes) FROM catalog.RouteSegments;");

        await database.ExecuteScriptAsync(MigrationPath());
        await database.ExecuteScriptAsync(MigrationPath());
        (await database.ExecuteScalarAsync<string>("SELECT status FROM catalog.RouteSegments;")).Should().Be("Active");
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM catalog.RouteSegments WHERE route_note IS NULL AND created_by IS NULL AND idempotency_key IS NULL;")).Should().Be(1);
        var after = await database.ExecuteScalarAsync<string>("SELECT CONCAT(segment_id,'|',from_poi_id,'|',to_poi_id,'|',transport_mode,'|',distance_km,'|',est_duration_minutes) FROM catalog.RouteSegments;");
        after.Should().Be(before);

        await database.ExecuteScriptAsync(MigrationPath(rollback: true));
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name IN ('status','route_note','created_by','idempotency_key');")).Should().Be(0);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM catalog.RouteSegments;")).Should().Be(1);
        await database.ExecuteScriptAsync(MigrationPath());
        (await database.ExecuteScalarAsync<string>("SELECT status FROM catalog.RouteSegments;")).Should().Be("Active");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Up_WhenLegacyEndpointInactive_RefusesAndLeavesOriginalRouteAndSchemaUntouched()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteScriptAsync(MigrationPath(rollback: true));
        var seed = await CreateRouteSqlServerTests.SeedAsync(database);
        await database.ExecuteNonQueryAsync(Insert(seed.FromId, seed.ToId));
        await database.ExecuteNonQueryAsync($"UPDATE catalog.POIs SET status='Inactive' WHERE poi_id={seed.ToId};");

        var action = () => database.ExecuteScriptAsync(MigrationPath());
        (await action.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("AMBIGUOUS_REQUIREMENT");
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name='status';")).Should().Be(0);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM catalog.RouteSegments;")).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Up_WhenRequiredBaseColumnIsMissing_RejectsWithoutChangingTheSchema()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteScriptAsync(MigrationPath(rollback: true));
        await database.ExecuteNonQueryAsync("EXEC sys.sp_rename N'catalog.RouteSegments.created_at', N'legacy_created_at', N'COLUMN';");

        var migrate = () => database.ExecuteScriptAsync(MigrationPath());
        (await migrate.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("RouteSegments.created_at");
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'catalog.RouteSegments') AND name='status';"))
            .Should().Be(0);
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("wrong-trigger", "TR_RouteSegments_ActiveEndpoints")]
    [InlineData("disabled-trigger", "TR_RouteSegments_ActiveEndpoints")]
    [InlineData("wrong-default", "RouteSegments.status default")]
    [InlineData("wrong-check", "CK_RouteSegments_Status")]
    [InlineData("wrong-operation-check", "CK_RouteSegments_Operation")]
    [InlineData("disabled-check", "CK_RouteSegments_Status")]
    [InlineData("untrusted-check", "CK_RouteSegments_Status")]
    [InlineData("disabled-fk", "FK_RouteSegments_CreatedBy")]
    [InlineData("wrong-fk", "FK_RouteSegments_CreatedBy")]
    [InlineData("nonunique-index", "UX_RouteSegments_ActorOperation")]
    [InlineData("wrong-index-key", "UX_RouteSegments_ActorOperation")]
    [InlineData("wrong-index-filter", "UX_RouteSegments_ActorOperation")]
    [InlineData("disabled-index", "UX_RouteSegments_ActorOperation")]
    public async Task Up_WhenNamedObjectHasDrifted_RefusesWithoutChangingExistingData(string drift, string objectName)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await CreateRouteSqlServerTests.SeedAsync(database);
        await database.ExecuteNonQueryAsync(Insert(seed.FromId, seed.ToId));
        var change = drift switch
        {
            "wrong-trigger" => "EXEC(N'ALTER TRIGGER catalog.TR_RouteSegments_ActiveEndpoints ON catalog.RouteSegments AFTER INSERT AS BEGIN SET NOCOUNT ON; END;');",
            "disabled-trigger" => "DISABLE TRIGGER catalog.TR_RouteSegments_ActiveEndpoints ON catalog.RouteSegments;",
            "wrong-default" => "ALTER TABLE catalog.RouteSegments DROP CONSTRAINT DF_RouteSegments_Status; ALTER TABLE catalog.RouteSegments ADD CONSTRAINT DF_RouteSegments_Status DEFAULT ('Inactive') FOR status;",
            "wrong-check" => "ALTER TABLE catalog.RouteSegments DROP CONSTRAINT CK_RouteSegments_Status; ALTER TABLE catalog.RouteSegments ADD CONSTRAINT CK_RouteSegments_Status CHECK (status <> 'Rejected');",
            "wrong-operation-check" => "ALTER TABLE catalog.RouteSegments DROP CONSTRAINT CK_RouteSegments_Operation; ALTER TABLE catalog.RouteSegments ADD CONSTRAINT CK_RouteSegments_Operation CHECK (created_by IS NULL OR created_by > 0);",
            "disabled-check" => "ALTER TABLE catalog.RouteSegments NOCHECK CONSTRAINT CK_RouteSegments_Status;",
            "untrusted-check" => "ALTER TABLE catalog.RouteSegments NOCHECK CONSTRAINT CK_RouteSegments_Status; ALTER TABLE catalog.RouteSegments CHECK CONSTRAINT CK_RouteSegments_Status;",
            "disabled-fk" => "ALTER TABLE catalog.RouteSegments NOCHECK CONSTRAINT FK_RouteSegments_CreatedBy;",
            "wrong-fk" => "ALTER TABLE catalog.RouteSegments DROP CONSTRAINT FK_RouteSegments_CreatedBy; ALTER TABLE catalog.RouteSegments ADD CONSTRAINT FK_RouteSegments_CreatedBy FOREIGN KEY (created_by) REFERENCES catalog.POIs(poi_id);",
            "nonunique-index" => RecreateOperationIndex("", "created_by,idempotency_key", "idempotency_key IS NOT NULL"),
            "wrong-index-key" => RecreateOperationIndex("UNIQUE", "idempotency_key,created_by", "idempotency_key IS NOT NULL"),
            "wrong-index-filter" => RecreateOperationIndex("UNIQUE", "created_by,idempotency_key", "created_by IS NOT NULL"),
            "disabled-index" => "ALTER INDEX UX_RouteSegments_ActorOperation ON catalog.RouteSegments DISABLE;",
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };
        await database.ExecuteNonQueryAsync(change);

        var migrate = () => database.ExecuteScriptAsync(MigrationPath());
        (await migrate.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain(objectName);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM catalog.RouteSegments WHERE status='Active' AND distance_km=12.50 AND est_duration_minutes=25;"))
            .Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Down_WhenNewNoteOrOperationExists_RefusesWithoutDroppingData()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await CreateRouteSqlServerTests.SeedAsync(database);
        await database.ExecuteNonQueryAsync($"""
            INSERT INTO catalog.RouteSegments(from_poi_id,to_poi_id,transport_mode,distance_km,est_duration_minutes,route_note,created_by,idempotency_key)
            VALUES({seed.FromId},{seed.ToId},'Car',12.50,25,N'Preserve note',{seed.Staff.Id},NEWID());
            """);

        var action = () => database.ExecuteScriptAsync(MigrationPath(rollback: true));
        (await action.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("Rollback refused");
        (await database.ExecuteScalarAsync<string>("SELECT route_note FROM catalog.RouteSegments;")).Should().Be("Preserve note");
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.triggers WHERE name='TR_RouteSegments_ActiveEndpoints';")).Should().Be(1);
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("self-loop")]
    [InlineData("foreign-key")]
    [InlineData("zero-distance")]
    [InlineData("negative-distance")]
    [InlineData("zero-duration")]
    [InlineData("negative-duration")]
    [InlineData("unsupported-mode")]
    [InlineData("invalid-status")]
    [InlineData("inactive-origin")]
    [InlineData("inactive-destination")]
    public async Task Database_InvalidRouteCannotCommit(string invalidCase)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await CreateRouteSqlServerTests.SeedAsync(database);
        var from = seed.FromId;
        var to = seed.ToId;
        var distance = "12.50";
        var duration = "25";
        var mode = "Car";
        var status = "Active";
        switch (invalidCase)
        {
            case "self-loop": to = from; break;
            case "foreign-key": to = long.MaxValue; break;
            case "zero-distance": distance = "0"; break;
            case "negative-distance": distance = "-1"; break;
            case "zero-duration": duration = "0"; break;
            case "negative-duration": duration = "-1"; break;
            case "unsupported-mode": mode = "Flying"; break;
            case "invalid-status": status = "Unknown"; break;
            case "inactive-origin":
                await database.ExecuteNonQueryAsync($"UPDATE catalog.POIs SET status='Inactive' WHERE poi_id={from};");
                break;
            case "inactive-destination":
                await database.ExecuteNonQueryAsync($"UPDATE catalog.POIs SET status='Inactive' WHERE poi_id={to};");
                break;
        }
        var action = () => database.ExecuteNonQueryAsync($"""
            INSERT INTO catalog.RouteSegments(from_poi_id,to_poi_id,transport_mode,distance_km,est_duration_minutes,status)
            VALUES({from},{to},'{mode}',{distance},{duration},'{status}');
            """);
        await action.Should().ThrowAsync<SqlException>();
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM catalog.RouteSegments;")).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Database_DuplicateDirectedPairAndMode_IsRejected()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await CreateRouteSqlServerTests.SeedAsync(database);
        await database.ExecuteNonQueryAsync(Insert(seed.FromId, seed.ToId));
        var action = () => database.ExecuteNonQueryAsync(Insert(seed.FromId, seed.ToId));
        await action.Should().ThrowAsync<SqlException>();
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM catalog.RouteSegments;")).Should().Be(1);
    }

    private static string RecreateOperationIndex(string uniqueness, string keys, string filter) => $"""
        DROP INDEX UX_RouteSegments_ActorOperation ON catalog.RouteSegments;
        CREATE {uniqueness} INDEX UX_RouteSegments_ActorOperation ON catalog.RouteSegments({keys}) WHERE {filter};
        """;

    private static string Insert(long fromId, long toId) => $"""
        INSERT INTO catalog.RouteSegments(from_poi_id,to_poi_id,transport_mode,distance_km,est_duration_minutes)
        VALUES({fromId},{toId},'Car',12.50,25);
        """;

    private static string MigrationPath(bool rollback = false)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
        {
            directory = directory.Parent;
        }
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository not found."),
            "database", "migrations", rollback ? "rollback" : "", "20261113_add_route_creation.sql");
    }
}