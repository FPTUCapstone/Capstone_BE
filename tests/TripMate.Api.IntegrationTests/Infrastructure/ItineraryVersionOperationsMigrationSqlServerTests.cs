using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Infrastructure;

[Collection(nameof(TripMateApiFactory))]
public sealed class ItineraryVersionOperationsMigrationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_AppliesToLegacyDatabase_RerunsAndRestoresPartialConstraints()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedLegacyDataAsync(database);

        // v7 already contains this table. Removing only the UC-11 table models a
        // database created from the previous schema while preserving its data.
        await database.ExecuteNonQueryAsync(
            "DROP TABLE planning.ItineraryVersionOperations;");

        var migration = await ReadMigrationAsync();
        await database.ExecuteNonQueryAsync(migration);
        await database.ExecuteNonQueryAsync(migration);

        await InsertValidOperationAsync(database, Guid.NewGuid());
        await DropPartialConstraintsAsync(database);
        await database.ExecuteNonQueryAsync(migration);

        await using var context = database.CreateDbContext();
        (await context.Users.CountAsync()).Should().Be(1);
        (await context.Itineraries.CountAsync()).Should().Be(1);
        (await context.ItineraryVersionOperations.CountAsync()).Should().Be(1);

        var operation = await context.ItineraryVersionOperations.SingleAsync();
        operation.CreatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        operation.SourceItineraryId.Should().Be(1);

        var duplicate = ItineraryVersionOperation.Create(
            operation.TravelerUserId,
            operation.SourceItineraryId,
            operation.OperationType,
            operation.IdempotencyKey,
            operation.RequestHash,
            DateTimeOffset.UtcNow);
        context.ItineraryVersionOperations.Add(duplicate);

        var duplicateAction = () => context.SaveChangesAsync();
        await duplicateAction.Should().ThrowAsync<DbUpdateException>();
    }

    private static async Task SeedLegacyDataAsync(SqlServerTestDatabase database)
    {
        await database.ExecuteNonQueryAsync("""
            SET IDENTITY_INSERT dbo.Users ON;
            INSERT INTO dbo.Users (user_id, role, email, full_name, status)
            VALUES (1, 'Traveler', 'uc11-migration@example.com', 'UC11 Migration Traveler', 'Active');
            SET IDENTITY_INSERT dbo.Users OFF;

            SET IDENTITY_INSERT planning.Itineraries ON;
            INSERT INTO planning.Itineraries
                (itinerary_id, traveler_user_id, source_type, title, status, version)
            VALUES (1, 1, 'Manual', 'Legacy itinerary', 'Active', 1);
            SET IDENTITY_INSERT planning.Itineraries OFF;
            """);
    }

    private static async Task InsertValidOperationAsync(
        SqlServerTestDatabase database,
        Guid idempotencyKey)
    {
        await database.ExecuteNonQueryAsync($"""
            INSERT INTO planning.ItineraryVersionOperations
                (traveler_user_id, source_itinerary_id, operation_type,
                 idempotency_key, request_hash, created_at)
            VALUES (1, 1, 'Adjust', '{idempotencyKey}', 'migration-test-hash',
                    '2026-09-20T12:00:00');
            """);
    }

    private static Task DropPartialConstraintsAsync(SqlServerTestDatabase database) =>
        database.ExecuteNonQueryAsync("""
            ALTER TABLE planning.ItineraryVersionOperations
                DROP CONSTRAINT FK_ItineraryVersionOperations_Traveler;
            ALTER TABLE planning.ItineraryVersionOperations
                DROP CONSTRAINT FK_ItineraryVersionOperations_Source;
            ALTER TABLE planning.ItineraryVersionOperations
                DROP CONSTRAINT FK_ItineraryVersionOperations_Result;
            ALTER TABLE planning.ItineraryVersionOperations
                DROP CONSTRAINT CK_ItineraryVersionOperations_Type;
            ALTER TABLE planning.ItineraryVersionOperations
                DROP CONSTRAINT UQ_ItineraryVersionOperations_TravelerKey;
            DROP INDEX IX_ItineraryVersionOperations_Source
                ON planning.ItineraryVersionOperations;
            """);

    private static async Task<string> ReadMigrationAsync()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "migrations",
            "20260920_add_itinerary_version_operations.sql");
        var migration = await File.ReadAllTextAsync(path);
        return Regex.Replace(migration, @"^\s*GO\s*$", string.Empty, RegexOptions.Multiline);
    }
}