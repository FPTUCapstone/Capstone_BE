using System.Data.Common;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Tours.Search;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class SearchToursSqlServerTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        9,
        18,
        0,
        0,
        0,
        TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Search_Default_EnforcesPublicPredicateAndChoosesAvailableScheduleFirst()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedVisibilityScenarioAsync(database);
        await using var context = database.CreateDbContext();
        var handler = CreateHandler(context);

        var result = await handler.Handle(new SearchToursQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(3);
        result.Value.TotalPages.Should().Be(1);
        result.Value.AsOfUtc.Should().Be(Now.UtcDateTime);
        result.Value.Items.Select(item => item.Title).Should()
            .Equal("Alpha public", "No schedule", "Sold out");

        var first = result.Value.Items[0];
        first.TourId.Should().Be("9007199254740993");
        first.RepresentativeScheduleId.Should().Be("7002");
        first.DepartureAtUtc.Should().Be(
            new DateTime(2026, 9, 20, 2, 0, 0, DateTimeKind.Utc));
        first.AvailabilityStatus.Should().Be("available");
        first.RemainingSlots.Should().Be(8);
        first.BasePrice.Should().Be(1_250_000);
        first.Currency.Should().Be("VND");

        var noSchedule = result.Value.Items[1];
        noSchedule.RepresentativeScheduleId.Should().BeNull();
        noSchedule.DepartureAtUtc.Should().BeNull();
        noSchedule.AvailabilityStatus.Should().Be("noUpcomingSchedule");
        noSchedule.RemainingSlots.Should().BeNull();

        var soldOut = result.Value.Items[2];
        soldOut.RepresentativeScheduleId.Should().Be("7004");
        soldOut.AvailabilityStatus.Should().Be("soldOut");
        soldOut.RemainingSlots.Should().Be(0);

        var dateFiltered = await handler.Handle(
            new SearchToursQuery(DepartureDate: new DateOnly(2026, 9, 20)),
            CancellationToken.None);
        dateFiltered.Value.TotalCount.Should().Be(2);
        dateFiltered.Value.Items.Select(item => item.Title)
            .Should().Equal("Alpha public", "Sold out");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Search_CombinesLiteralDestinationVietnamDateAndInclusivePriceBounds()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedFilterScenarioAsync(database);
        await using var context = database.CreateDbContext();
        var handler = CreateHandler(context);

        var result = await handler.Handle(
            new SearchToursQuery(
                Destination: "  đà%_nẵng  ",
                DepartureDate: new DateOnly(2026, 9, 20),
                MinPrice: 500_000,
                MaxPrice: 1_000_000,
                Page: 1,
                PageSize: 1),
            CancellationToken.None);

        result.Value.TotalCount.Should().Be(1);
        result.Value.TotalPages.Should().Be(1);
        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].Title.Should().Be("Literal match");
        result.Value.Items[0].Destinations.Should().Equal("ĐÀ%_Nẵng", "Hội An");
        result.Value.Items[0].BasePrice.Should().Be(500_000);
        result.Value.Items[0].DepartureAtUtc.Should().Be(
            new DateTime(2026, 9, 19, 17, 0, 0, DateTimeKind.Utc));

        var injectionAttempt = await handler.Handle(
            new SearchToursQuery(Destination: "%' OR 1=1 --"),
            CancellationToken.None);
        injectionAttempt.Value.TotalCount.Should().Be(0);

        var hoiAn = await handler.Handle(
            new SearchToursQuery(Destination: "Hội An"), CancellationToken.None);
        hoiAn.Value.Items.Select(item => item.Title)
            .Should().Equal("Literal match");
        hoiAn.Value.TotalCount.Should().Be(1);

        var noRegion = await handler.Handle(
            new SearchToursQuery(Destination: "Hải Phòng"), CancellationToken.None);
        noRegion.Value.Items.Should().BeEmpty();

        await database.ExecuteNonQueryAsync(
            "UPDATE commerce.Tours SET destination = N'Hải Phòng' WHERE tour_id = 201;");
        var legacyText = await handler.Handle(
            new SearchToursQuery(Destination: "Hải Phòng"), CancellationToken.None);
        legacyText.Value.TotalCount.Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Search_PageBeyondTotal_ReturnsEmptyWithStableCount()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedFilterScenarioAsync(database);
        await using var context = database.CreateDbContext();
        var handler = CreateHandler(context);

        var result = await handler.Handle(
            new SearchToursQuery(Page: 4, PageSize: 2),
            CancellationToken.None);

        result.Value.Page.Should().Be(4);
        result.Value.TotalCount.Should().Be(4);
        result.Value.TotalPages.Should().Be(2);
        result.Value.Items.Should().BeEmpty();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Search_CorruptCapacity_ReturnsUnknownWithoutLeakingScheduleDetails()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedVisibilityScenarioAsync(database);
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE commerce.TourSchedules
                NOCHECK CONSTRAINT CK_TourSchedules_CapacityWithinLimit;
            UPDATE commerce.TourSchedules
            SET reserved_capacity = total_capacity + 1
            WHERE tour_id = 108;
            """);
        await using var context = database.CreateDbContext();
        var handler = CreateHandler(context);

        var result = await handler.Handle(
            new SearchToursQuery(Destination: "Huế"),
            CancellationToken.None);

        result.Value.Items.Should().ContainSingle();
        var item = result.Value.Items[0];
        item.AvailabilityStatus.Should().Be("unknown");
        item.RepresentativeScheduleId.Should().BeNull();
        item.DepartureAtUtc.Should().BeNull();
        item.RemainingSlots.Should().BeNull();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Search_SerializableSnapshot_PreventsPublicationChangeBetweenCountAndPage()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedFilterScenarioAsync(database);
        var gate = new PauseAfterCountInterceptor();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(database.ConnectionString)
            .AddInterceptors(gate)
            .Options;
        await using var context = new ApplicationDbContext(options);
        var handler = CreateHandler(context);

        var searchTask = handler.Handle(
            new SearchToursQuery(
                DepartureDate: new DateOnly(2026, 9, 20),
                PageSize: 10),
            CancellationToken.None);

        await gate.CountCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        SqlException lockException;
        try
        {
            lockException = await AttemptBlockedPublicationChangeAsync(database.ConnectionString);
        }
        finally
        {
            gate.ReleasePageQuery.TrySetResult();
        }

        lockException.Number.Should().Be(1222);
        var result = await searchTask.WaitAsync(TimeSpan.FromSeconds(10));
        result.Value.TotalCount.Should().Be(3);
        result.Value.Items.Should().HaveCount(3);
        gate.ReaderCommandCount.Should().Be(3);
        gate.PageSql.Should().Contain("COLLATE Vietnamese_100_CI_AS");

        await database.ExecuteNonQueryAsync(
            "UPDATE commerce.Tours SET status = 'Inactive' WHERE tour_id = 201;");
        await using var afterContext = database.CreateDbContext();
        var afterHandler = CreateHandler(afterContext);
        var after = await afterHandler.Handle(
            new SearchToursQuery(DepartureDate: new DateOnly(2026, 9, 20)),
            CancellationToken.None);
        after.Value.TotalCount.Should().Be(2);
    }

    private static SearchToursQueryHandler CreateHandler(IApplicationDbContext context) =>
        new(context, new FixedClock(Now), NullLogger<SearchToursQueryHandler>.Instance);

    private static async Task SeedVisibilityScenarioAsync(SqlServerTestDatabase database)
    {
        await database.ExecuteNonQueryAsync("""
            SET IDENTITY_INSERT dbo.Users ON;
            INSERT INTO dbo.Users (user_id, role, email, full_name, status)
            VALUES
                (101, 'TourOperator', N'active@example.test', N'Active operator', 'Active'),
                (102, 'TourOperator', N'locked@example.test', N'Locked operator', 'Locked'),
                (103, 'TourOperator', N'pending@example.test', N'Pending operator', 'Active');
            SET IDENTITY_INSERT dbo.Users OFF;

            INSERT INTO dbo.OperatorProfiles
                (user_id, company_name, tax_code, business_license_no, approval_status)
            VALUES
                (101, N'Visible Travel', N'TAX-101', N'LIC-101', 'Approved'),
                (102, N'Locked Travel', N'TAX-102', N'LIC-102', 'Approved'),
                (103, N'Pending Travel', N'TAX-103', N'LIC-103', 'PendingApproval');

            SET IDENTITY_INSERT commerce.Tours ON;
            INSERT INTO commerce.Tours
                (tour_id, operator_user_id, title, destination, base_price,
                 duration_days, status, published_at)
            VALUES
                (9007199254740993, 101, N'Alpha public', N'Đà Nẵng', 1250000, 3,
                 'Approved', '2026-09-17T00:00:00'),
                (102, 101, N'No schedule', NULL, 900000, 2,
                 'Approved', '2026-09-17T00:00:00'),
                (103, 101, N'No publication', N'Huế', 900000, 2,
                 'Approved', NULL),
                (104, 101, N'Draft tour', N'Huế', 900000, 2,
                 'Draft', '2026-09-17T00:00:00'),
                (105, 102, N'Locked owner', N'Huế', 900000, 2,
                 'Approved', '2026-09-17T00:00:00'),
                (106, 103, N'Pending owner', N'Huế', 900000, 2,
                 'Approved', '2026-09-17T00:00:00'),
                (107, 101, N'Future publication', N'Huế', 900000, 2,
                 'Approved', '2026-09-19T00:00:00'),
                (108, 101, N'Sold out', N'Huế', 700000, 1,
                 'Approved', '2026-09-17T00:00:00');
            SET IDENTITY_INSERT commerce.Tours OFF;

            INSERT INTO catalog.Destinations (name) VALUES (N'Đà Nẵng'), (N'Huế');
            INSERT INTO commerce.TourDestinations (tour_id, destination_id, sequence_no)
            SELECT 9007199254740993, destination_id, 1
            FROM catalog.Destinations WHERE name = N'Đà Nẵng';
            INSERT INTO commerce.TourDestinations (tour_id, destination_id, sequence_no)
            SELECT tour_id, destination_id, 1
            FROM commerce.Tours CROSS JOIN catalog.Destinations
            WHERE tour_id IN (103, 104, 105, 106, 107, 108) AND name = N'Huế';

            SET IDENTITY_INSERT commerce.TourSchedules ON;
            INSERT INTO commerce.TourSchedules
                (schedule_id, tour_id, start_datetime, end_datetime,
                 total_capacity, reserved_capacity, status)
            VALUES
                (7001, 9007199254740993, '2026-09-19T01:00:00', '2026-09-22T01:00:00',
                 10, 10, 'Scheduled'),
                (7002, 9007199254740993, '2026-09-20T02:00:00', '2026-09-23T02:00:00',
                 10, 2, 'Scheduled'),
                (7003, 9007199254740993, '2026-09-18T01:00:00', '2026-09-21T01:00:00',
                 10, 0, 'Cancelled'),
                (7004, 108, '2026-09-19T02:00:00', '2026-09-20T02:00:00',
                 8, 8, 'Scheduled'),
                (7005, 108, '2026-09-20T02:00:00', '2026-09-21T02:00:00',
                 8, 8, 'Scheduled');
            SET IDENTITY_INSERT commerce.TourSchedules OFF;
            """);
    }

    private static async Task SeedFilterScenarioAsync(SqlServerTestDatabase database)
    {
        await database.ExecuteNonQueryAsync("""
            SET IDENTITY_INSERT dbo.Users ON;
            INSERT INTO dbo.Users (user_id, role, email, full_name, status)
            VALUES (201, 'TourOperator', N'filter@example.test', N'Filter operator', 'Active');
            SET IDENTITY_INSERT dbo.Users OFF;

            INSERT INTO dbo.OperatorProfiles
                (user_id, company_name, tax_code, business_license_no, approval_status)
            VALUES (201, N'Filter Travel', N'TAX-201', N'LIC-201', 'Approved');

            SET IDENTITY_INSERT commerce.Tours ON;
            INSERT INTO commerce.Tours
                (tour_id, operator_user_id, title, destination, base_price,
                 duration_days, status, published_at)
            VALUES
                (201, 201, N'Literal match', N'ĐÀ%_Nẵng', 500000, 1,
                 'Approved', '2026-09-17T00:00:00'),
                (202, 201, N'Accent mismatch', N'Da%_Nang', 500000, 1,
                 'Approved', '2026-09-17T00:00:00'),
                (203, 201, N'Above price', N'Đà%_Nẵng', 1000001, 1,
                 'Approved', '2026-09-17T00:00:00'),
                (204, 201, N'Upper boundary excluded', N'Đà%_Nẵng', 750000, 1,
                 'Approved', '2026-09-17T00:00:00');
            SET IDENTITY_INSERT commerce.Tours OFF;

            INSERT INTO catalog.Destinations (name)
            VALUES (N'ĐÀ%_Nẵng'), (N'Hội An'), (N'Da%_Nang');
            INSERT INTO commerce.TourDestinations (tour_id, destination_id, sequence_no)
            SELECT 201, destination_id, 1 FROM catalog.Destinations WHERE name = N'ĐÀ%_Nẵng';
            INSERT INTO commerce.TourDestinations (tour_id, destination_id, sequence_no)
            SELECT 201, destination_id, 2 FROM catalog.Destinations WHERE name = N'Hội An';
            INSERT INTO commerce.TourDestinations (tour_id, destination_id, sequence_no)
            SELECT 202, destination_id, 1 FROM catalog.Destinations WHERE name = N'Da%_Nang';
            INSERT INTO commerce.TourDestinations (tour_id, destination_id, sequence_no)
            SELECT tour_id, destination_id, 1
            FROM commerce.Tours CROSS JOIN catalog.Destinations
            WHERE tour_id IN (203, 204) AND name = N'Đà%_Nẵng';

            INSERT INTO commerce.TourSchedules
                (tour_id, start_datetime, end_datetime,
                 total_capacity, reserved_capacity, status)
            VALUES
                (201, '2026-09-19T17:00:00', '2026-09-20T17:00:00', 5, 0, 'Scheduled'),
                (202, '2026-09-19T18:00:00', '2026-09-20T18:00:00', 5, 0, 'Scheduled'),
                (203, '2026-09-19T19:00:00', '2026-09-20T19:00:00', 5, 0, 'Scheduled'),
                (204, '2026-09-20T17:00:00', '2026-09-21T17:00:00', 5, 0, 'Scheduled');
            """);
    }

    private static async Task<SqlException> AttemptBlockedPublicationChangeAsync(
        string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SET LOCK_TIMEOUT 1500;
            UPDATE commerce.Tours SET status = 'Inactive' WHERE tour_id = 201;
            """;

        Func<Task> action = async () => await command.ExecuteNonQueryAsync();
        var assertion = await action.Should().ThrowAsync<SqlException>();
        assertion.Which.Message.Should().Contain("Lock request time out period exceeded");
        return assertion.Which;
    }

    private sealed class PauseAfterCountInterceptor : DbCommandInterceptor
    {
        private int _readerCommandCount;
        private string? _pageSql;

        public TaskCompletionSource CountCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleasePageQuery { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReaderCommandCount => Volatile.Read(ref _readerCommandCount);

        public string? PageSql => _pageSql;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readerCommandCount);

            if (_pageSql is null
                && command.CommandText.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase))
            {
                _pageSql = command.CommandText;
            }

            if (command.CommandText.Contains("COUNT_BIG", StringComparison.OrdinalIgnoreCase))
            {
                CountCompleted.TrySetResult();
                await ReleasePageQuery.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
