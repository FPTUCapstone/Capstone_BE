using System.Data.Common;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Traveler.Preferences.Update;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Traveler;

public sealed class TravelerPreferencesSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 6, 30, 0, TimeSpan.Zero);
    private const string RemoveDefaultBudgetMigrationFileName =
        "20261013_remove_traveler_profile_default_budget.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task DefaultBudgetRemovalMigration_RemovesLegacyColumnAndIsIdempotent()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(
            "ALTER TABLE dbo.TravelerProfiles ADD default_budget DECIMAL(12,2) NULL;");

        var migrationPath = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "migrations",
            RemoveDefaultBudgetMigrationFileName);

        await database.ExecuteScriptAsync(migrationPath);
        await database.ExecuteScriptAsync(migrationPath);

        var columnCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dbo.TravelerProfiles')
              AND name = N'default_budget';
            """);
        columnCount.Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Update_InsertUpdateClear_RoundTripsThroughNewContext()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var travelerId = await SeedAsync(database, "Beach");

        await using (var context = database.CreateDbContext())
        {
            var result = await CreateHandler(context).Handle(
                Command(travelerId, ["beach"], "PublicTransit", "Fast", "High", ["Halal"]),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        await using (var verify = database.CreateDbContext())
        {
            var stored = await verify.TravelerProfiles.AsNoTracking().SingleAsync();
            stored.UserId.Should().Be(travelerId);
            stored.InterestTagsJson.Should().Be("[\"Beach\"]");
            stored.PreferredTransportMode.Should().Be(TransportMode.PublicTransit);
            stored.TravelPace.Should().Be(TravelerPace.Fast);
            stored.RiskTolerance.Should().Be(RiskToleranceLevel.High);
            stored.FoodPreferencesJson.Should().Be("[\"Halal\"]");
            stored.UpdatedAtUtc.Should().Be(Now);
            stored.UpdatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        }

        await using (var context = database.CreateDbContext())
        {
            var result = await CreateHandler(context).Handle(
                Command(travelerId, [], null, null, null, []),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        await using var cleared = database.CreateDbContext();
        var row = await cleared.TravelerProfiles.AsNoTracking().SingleAsync();
        row.InterestTagsJson.Should().BeNull();
        row.PreferredTransportMode.Should().BeNull();
        row.FoodPreferencesJson.Should().BeNull();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Update_AcceptsEveryEnumKeyAllowedBySchemaCheckConstraints()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var travelerId = await SeedAsync(database);

        foreach (var transport in Enum.GetNames<TransportMode>())
        {
            foreach (var pace in Enum.GetNames<TravelerPace>())
            {
                foreach (var risk in Enum.GetNames<RiskToleranceLevel>())
                {
                    await using var context = database.CreateDbContext();
                    var result = await CreateHandler(context).Handle(
                        Command(travelerId, null, transport, pace, risk, null),
                        CancellationToken.None);
                    result.IsSuccess.Should().BeTrue($"{transport}/{pace}/{risk}");
                }
            }
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Update_WhenConcurrentFirstInsertWins_RetriesOnceAsUpdate()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var travelerId = await SeedAsync(database);
        var interceptor = new InsertConcurrentRowFirstInterceptor(database.ConnectionString, travelerId);
        await using var context = database.CreateDbContext(interceptor);

        var result = await CreateHandler(context).Handle(
            Command(travelerId, null, "Car", null, null, null),
            CancellationToken.None);

        interceptor.Fired.Should().BeTrue("the competing insert must happen before ours");
        result.IsSuccess.Should().BeTrue();
        await using var verify = database.CreateDbContext();
        var rows = await verify.TravelerProfiles.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].PreferredTransportMode.Should().Be(TransportMode.Car);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Update_WhenWriteFails_KeepsPreviousState()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var travelerId = await SeedAsync(database);
        await using (var context = database.CreateDbContext())
        {
            (await CreateHandler(context).Handle(
                Command(travelerId, null, "Walking", null, null, null),
                CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        await using (var failing = database.CreateDbContext(new FailTravelerProfileWriteInterceptor()))
        {
            var act = () => CreateHandler(failing).Handle(
                Command(travelerId, null, "Car", null, null, null),
                CancellationToken.None);
            await act.Should().ThrowAsync<Exception>();
        }

        await using var verify = database.CreateDbContext();
        var stored = await verify.TravelerProfiles.AsNoTracking().SingleAsync();
        stored.PreferredTransportMode.Should().Be(TransportMode.Walking);
    }

    private static UpdateTravelerPreferencesCommandHandler CreateHandler(IApplicationDbContext context) =>
        new(context, new FixedClock());

    private static UpdateTravelerPreferencesCommand Command(
        long userId,
        string?[]? interests,
        string? transport,
        string? pace,
        string? risk,
        string?[]? food) =>
        new(userId, interests, transport, pace, risk, food);

    private static async Task<long> SeedAsync(SqlServerTestDatabase database, params string[] categories)
    {
        await using var context = database.CreateDbContext();
        var user = new User
        {
            Email = $"preferences-{Guid.NewGuid():N}@example.test",
            FullName = "SQL Preference Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        context.Users.Add(user);
        context.PoiCategories.AddRange(categories.Select(name => PoiCategory.Create(name, null)));
        await context.SaveChangesAsync();
        return user.Id;
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }

    /// <summary>
    /// Deterministic race: just before our INSERT into TravelerProfiles executes, a second
    /// connection inserts and commits the same user's row.
    /// </summary>
    private sealed class InsertConcurrentRowFirstInterceptor(string connectionString, long userId)
        : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await InsertCompetitorAsync(command, cancellationToken);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await InsertCompetitorAsync(command, cancellationToken);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private async Task InsertCompetitorAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (Fired || !IsTravelerProfileInsert(command))
            {
                return;
            }

            Fired = true;
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO dbo.TravelerProfiles (user_id, travel_pace) VALUES (@userId, 'Relaxed')";
            insert.Parameters.AddWithValue("@userId", userId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed class FailTravelerProfileWriteInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowOnWrite(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowOnWrite(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void ThrowOnWrite(DbCommand command)
        {
            if (command.CommandText.Contains("TravelerProfiles", StringComparison.OrdinalIgnoreCase)
                && (command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                    || command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Injected TravelerProfiles write failure.");
            }
        }
    }

    private static bool IsTravelerProfileInsert(DbCommand command) =>
        command.CommandText.Contains("INSERT INTO [dbo].[TravelerProfiles]", StringComparison.OrdinalIgnoreCase)
        || command.CommandText.Contains("INSERT INTO [TravelerProfiles]", StringComparison.OrdinalIgnoreCase);
}
