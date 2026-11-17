using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.RecommendationFeedback.Derived;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.RecommendationFeedback;

/// <summary>
/// UC-10 item 6 (spec .scratch/uc10-behavior-learning, D7): the sources migration, its coexistence with the
/// original TM-214 script (apply-schema.sh reruns every migration), and the promise that a derived signal never
/// fails the traveler's action, even when the table or the relaxed constraints are missing.
/// <see cref="SqlServerTestDatabase.CreateAsync()"/> already applies every migration, so the older database
/// states are rebuilt explicitly.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class DerivedBehaviorSignalsSqlServerTests
{
    private const string EventsMigration = "20260929_add_recommendation_behavior_events.sql";
    private const string SourcesMigration = "20261111_uc10_behavior_event_sources.sql";

    private const string InsertColumns = """
        INSERT INTO social.RecommendationBehaviorEvents (
            traveler_user_id, poi_id, itinerary_id, event_type,
            original_position, new_position, was_mandatory, source, client_event_id)
        """;

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 4, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SourcesMigration_RerunsWithTheOriginalScript_AndChecksTheDerivedSourceShapes()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        // apply-schema.sh reruns every historical migration on an existing database, in file order.
        await database.ExecuteScriptAsync(MigrationPath(EventsMigration));
        await database.ExecuteScriptAsync(MigrationPath(SourcesMigration));
        await database.ExecuteScriptAsync(MigrationPath(EventsMigration));
        var seed = await SeedAsync(database);

        (await TrustedCheckCountAsync(database)).Should().Be(3);

        await database.ExecuteNonQueryAsync($"""
            {InsertColumns}
            VALUES
                ({seed.TravelerId}, {seed.PoiId}, {seed.ItineraryId}, 'Dislike', NULL, NULL, 0, 'ItineraryEdit', NEWID()),
                ({seed.TravelerId}, {seed.PoiId}, {seed.ItineraryId}, 'Like', NULL, NULL, 0, 'ItineraryEdit', NEWID()),
                ({seed.TravelerId}, {seed.PoiId}, {seed.ItineraryId}, 'Like', NULL, NULL, 1, 'Navigation', NEWID()),
                ({seed.TravelerId}, {seed.PoiId}, {seed.ItineraryId}, 'Skip', 2, NULL, 1, 'Navigation', NEWID()),
                ({seed.TravelerId}, {seed.PoiId}, NULL, 'Like', NULL, NULL, 1, 'SchedulingRequest', NEWID()),
                ({seed.TravelerId}, {seed.PoiId}, NULL, 'Like', NULL, NULL, NULL, 'Explore', NEWID()),
                ({seed.TravelerId}, {seed.PoiId}, {seed.ItineraryId}, 'Reorder', 1, 2, 0, 'Itinerary', NEWID());
            """);

        foreach (var invalid in new[]
                 {
                     $"({seed.TravelerId}, {seed.PoiId}, {seed.ItineraryId}, 'Like', NULL, NULL, 1, 'SchedulingRequest', NEWID())",
                     $"({seed.TravelerId}, {seed.PoiId}, NULL, 'Like', NULL, NULL, 0, 'SchedulingRequest', NEWID())",
                     $"({seed.TravelerId}, {seed.PoiId}, NULL, 'Like', NULL, NULL, 0, 'ItineraryEdit', NEWID())",
                     $"({seed.TravelerId}, {seed.PoiId}, {seed.ItineraryId}, 'Skip', 1, NULL, 0, 'ItineraryEdit', NEWID())",
                     $"({seed.TravelerId}, {seed.PoiId}, {seed.ItineraryId}, 'Reorder', 1, 2, 0, 'Navigation', NEWID())",
                     $"({seed.TravelerId}, {seed.PoiId}, NULL, 'Like', NULL, NULL, NULL, 'Unknown', NEWID())",
                 })
        {
            Func<Task> insert = () => database.ExecuteNonQueryAsync($"{InsertColumns} VALUES {invalid};");
            await insert.Should().ThrowAsync<SqlException>(invalid);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SourcesMigration_UpgradesADatabaseThatStillHasTheOriginalConstraints()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await RestoreOriginalConstraintsAsync(database);
        // The original script verifies its own shape exactly, so this proves the restore is faithful.
        await database.ExecuteScriptAsync(MigrationPath(EventsMigration));
        var seed = await SeedAsync(database);
        var derivedRow =
            $"{InsertColumns} VALUES ({seed.TravelerId}, {seed.PoiId}, NULL, 'Like', NULL, NULL, 1, 'SchedulingRequest', NEWID());";
        Func<Task> beforeUpgrade = () => database.ExecuteNonQueryAsync(derivedRow);
        await beforeUpgrade.Should().ThrowAsync<SqlException>();

        await database.ExecuteScriptAsync(MigrationPath(SourcesMigration));

        await database.ExecuteNonQueryAsync(derivedRow);
        (await TrustedCheckCountAsync(database)).Should().Be(3);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Recorder_WritesTheSignalInTheCallersTransaction()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);

        var (written, categoryName) = await RecordInsideATransactionAsync(database, seed);

        written.Should().Be(1);
        (await CountAsync(database, "social.RecommendationBehaviorEvents", "source = 'SchedulingRequest'")).Should().Be(1);
        (await CountAsync(database, "catalog.POICategories", $"name = N'{categoryName}'")).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Recorder_BeforeTheSourcesMigration_RecordsNothingAndTheCallersChangeStillCommits()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await RestoreOriginalConstraintsAsync(database);
        var seed = await SeedAsync(database);

        var (written, categoryName) = await RecordInsideATransactionAsync(database, seed);

        written.Should().Be(0);
        (await CountAsync(database, "social.RecommendationBehaviorEvents", "1 = 1")).Should().Be(0);
        (await CountAsync(database, "catalog.POICategories", $"name = N'{categoryName}'")).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Recorder_WithoutTheEventsTable_RecordsNothingAndTheCallersChangeStillCommits()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("DROP TABLE social.RecommendationBehaviorEvents;");
        var seed = await SeedAsync(database);

        var (written, categoryName) = await RecordInsideATransactionAsync(database, seed);

        written.Should().Be(0);
        (await CountAsync(database, "catalog.POICategories", $"name = N'{categoryName}'")).Should().Be(1);
    }

    /// <summary>Mimics a handler: save its own change, then record a derived signal, in one transaction.</summary>
    private static async Task<(int Written, string CategoryName)> RecordInsideATransactionAsync(
        SqlServerTestDatabase database,
        Seed seed)
    {
        var categoryName = $"Caller-{Guid.NewGuid():N}";
        await using ApplicationDbContext db = database.CreateDbContext();
        var written = await db.ExecuteInTransactionAsync(
            async cancellationToken =>
            {
                db.PoiCategories.Add(PoiCategory.Create(categoryName, null));
                await db.SaveChangesAsync(cancellationToken);
                var count = await new DerivedBehaviorSignalRecorder(db).RecordMandatoryAsync(
                    seed.TravelerId, 501, [seed.PoiId], Now, cancellationToken);
                // A later save by the caller must not retry the failed signal rows.
                await db.SaveChangesAsync(cancellationToken);
                return count;
            },
            CancellationToken.None);
        return (written, categoryName);
    }

    /// <summary>Puts back the two CHECK constraints exactly as TM-214 created them (a database before item 6).</summary>
    private static Task RestoreOriginalConstraintsAsync(SqlServerTestDatabase database) =>
        database.ExecuteNonQueryAsync("""
            ALTER TABLE social.RecommendationBehaviorEvents
                DROP CONSTRAINT CK_RecommendationBehaviorEvents_SourceContext,
                     CK_RecommendationBehaviorEvents_TypeSource;
            ALTER TABLE social.RecommendationBehaviorEvents WITH CHECK
                ADD CONSTRAINT CK_RecommendationBehaviorEvents_SourceContext CHECK (
                    (source = 'Itinerary' AND itinerary_id IS NOT NULL
                        AND was_mandatory IS NOT NULL)
                    OR (source IN ('Explore','PoiDetail') AND itinerary_id IS NULL
                        AND was_mandatory IS NULL));
            ALTER TABLE social.RecommendationBehaviorEvents WITH CHECK
                ADD CONSTRAINT CK_RecommendationBehaviorEvents_TypeSource CHECK (
                    event_type IN ('Like','Dislike')
                    OR (event_type IN ('Skip','Reorder') AND source = 'Itinerary'
                        AND itinerary_id IS NOT NULL));
            """);

    private static Task<int> TrustedCheckCountAsync(SqlServerTestDatabase database) =>
        database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'social.RecommendationBehaviorEvents')
              AND is_disabled = 0 AND is_not_trusted = 0;
            """);

    private static Task<int> CountAsync(SqlServerTestDatabase database, string table, string predicate) =>
        database.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE {predicate};");

    private static async Task<Seed> SeedAsync(SqlServerTestDatabase database)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var travelerId = await database.ExecuteScalarAsync<long>($"""
            INSERT INTO dbo.Users (role, email, full_name, status)
            OUTPUT INSERTED.user_id
            VALUES ('Traveler', 'derived-{suffix}@example.test', N'Derived', 'Active');
            """);
        var categoryId = await database.ExecuteScalarAsync<int>($"""
            INSERT INTO catalog.POICategories (name)
            OUTPUT INSERTED.category_id
            VALUES (N'Category-{suffix}');
            """);
        var poiId = await database.ExecuteScalarAsync<long>($"""
            INSERT INTO catalog.POIs (category_id, name, latitude, longitude, status, created_by)
            OUTPUT INSERTED.poi_id
            VALUES ({categoryId}, N'Poi-{suffix}', 16.000000, 108.000000, 'Active', {travelerId});
            """);
        var itineraryId = await database.ExecuteScalarAsync<long>($"""
            INSERT INTO planning.Itineraries (traveler_user_id, source_type, title)
            OUTPUT INSERTED.itinerary_id
            VALUES ({travelerId}, 'Manual', N'Derived itinerary');
            """);
        return new Seed(travelerId, poiId, itineraryId);
    }

    private static string MigrationPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "database",
            "migrations",
            fileName);
    }

    private sealed record Seed(long TravelerId, long PoiId, long ItineraryId);
}