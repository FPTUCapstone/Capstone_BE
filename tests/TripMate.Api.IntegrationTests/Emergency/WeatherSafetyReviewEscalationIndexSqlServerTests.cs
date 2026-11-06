using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Emergency;

/// <summary>
/// The escalation sweep's own SQL, as EF generates it, must seek
/// IX_WeatherSafetyReviews_StatusDeadline once reviews accumulate instead of scanning them.
/// </summary>
public sealed class WeatherSafetyReviewEscalationIndexSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task EscalationQuery_SeeksTheStatusDeadlineIndex()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            INSERT INTO dbo.Users (role, full_name, email) VALUES ('TourOperator', N'UC70 Operator', 'uc70-plan@example.test');
            DECLARE @operator BIGINT = SCOPE_IDENTITY();
            INSERT INTO dbo.OperatorProfiles (user_id, company_name, tax_code, business_license_no, approval_status)
                VALUES (@operator, N'UC70 Tours', N'UC70-PLAN', N'UC70-PLAN', 'Approved');
            INSERT INTO commerce.Tours (operator_user_id, title, base_price) VALUES (@operator, N'Da Nang', 1000000);
            INSERT INTO commerce.TourSchedules (tour_id, start_datetime, end_datetime, total_capacity)
                VALUES (SCOPE_IDENTITY(), '2026-12-01T08:00:00', '2026-12-01T17:00:00', 20);
            DECLARE @schedule BIGINT = SCOPE_IDENTITY();

            -- Months of history: closed and acknowledged reviews vastly outnumber pending ones.
            WITH numbers AS (
                SELECT TOP (5000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO trip.WeatherSafetyReviews (tour_schedule_id, provider, provider_event_id, status,
                urgent_notice_sent_at, acknowledgement_deadline, created_at)
            SELECT @schedule, 'TomorrowIo', CONCAT('evt-', n),
                CASE WHEN n % 500 = 0 THEN 'PendingAcknowledgement' WHEN n % 2 = 0 THEN 'Closed' ELSE 'Acknowledged' END,
                DATEADD(MINUTE, -n, '2026-11-06T12:00:00'), DATEADD(MINUTE, 30 - n, '2026-11-06T12:00:00'),
                DATEADD(MINUTE, -n, '2026-11-06T12:00:00')
            FROM numbers;
            UPDATE STATISTICS trip.WeatherSafetyReviews WITH FULLSCAN;
            """);

        string sql;
        await using (var db = database.CreateDbContext())
        {
            var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
            // Same predicate as WeatherSafetyReviewEscalationBackgroundService.ProcessEscalationsAsync.
            sql = db.WeatherSafetyReviews
                .Include(r => r.TourSchedule)
                    .ThenInclude(s => s.Tour)
                .Where(r => r.Status == WeatherSafetyReview.StatusPendingAcknowledgement
                    && r.AcknowledgementDeadlineUtc <= now
                    && r.TourSchedule.Status == TourScheduleStatus.Scheduled)
                .ToQueryString();
        }

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var on = new SqlCommand("SET SHOWPLAN_XML ON;", connection))
        {
            await on.ExecuteNonQueryAsync();
        }

        var plans = new List<string>();
        await using (var command = new SqlCommand(sql, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                while (await reader.ReadAsync())
                {
                    plans.Add(reader.GetString(0));
                }
            }
            while (await reader.NextResultAsync());
        }

        string plan = string.Join('\n', plans);
        plan.Should().Contain("Index=\"[IX_WeatherSafetyReviews_StatusDeadline]\"");
        plan.Should().MatchRegex("PhysicalOp=\"Index Seek\"[^>]*>[\\s\\S]*?IX_WeatherSafetyReviews_StatusDeadline");
    }
}
