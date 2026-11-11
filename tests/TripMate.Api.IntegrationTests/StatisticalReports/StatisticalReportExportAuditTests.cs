using System.Net;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.StatisticalReports;

[Collection(nameof(TripMateApiFactory))]
public sealed class StatisticalReportExportAuditTests
{
    // Far-future period keys: the real StatisticalPeriodCloseBackgroundService only ever closes
    // calendar periods that have genuinely ended, so it can never race with — or duplicate — a
    // period this test seeds itself for a month that has not happened yet.
    [Fact]
    public async Task Export_Succeeds_WritesOneAuditLogRowWithReportTypeAndPeriod()
    {
        const string periodKey = "2099-01";
        await using var factory = new TripMateApiFactory();
        await SeedClosedPeriodAsync(factory, periodKey, 1);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);
        var operationId = Guid.NewGuid();

        var response = await client.GetAsync(
            $"/api/v1/admin/statistical-reports/export?reportType=USER_GROWTH&periodKey={periodKey}&bookingType=ALL&format=csv&exportOperationId={operationId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.WithDbContextAsync(async db =>
        {
            var rows = await db.AuditLogs.AsNoTracking()
                .Where(log => log.ActionType == AuditActionTypes.StatisticalReportExportRequested)
                .ToListAsync();
            rows.Should().ContainSingle();
            rows[0].AffectedEntity.Should().Be(AuditEntityTypes.StatisticalReportExport);
            rows[0].ActorUserId.Should().Be(1);
            rows[0].AfterData.Should().Contain("USER_GROWTH").And.Contain(periodKey);
            return 0;
        });
    }

    [Fact]
    public async Task Export_IdempotentReplay_WritesItsOwnAuditLogRow()
    {
        const string periodKey = "2099-02";
        await using var factory = new TripMateApiFactory();
        await SeedClosedPeriodAsync(factory, periodKey, 2);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);
        var operationId = Guid.NewGuid();
        string path =
            $"/api/v1/admin/statistical-reports/export?reportType=USER_GROWTH&periodKey={periodKey}&bookingType=ALL&format=csv&exportOperationId={operationId}";

        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);

        await factory.WithDbContextAsync(async db =>
        {
            int count = await db.AuditLogs.AsNoTracking()
                .CountAsync(log => log.ActionType == AuditActionTypes.StatisticalReportExportRequested);
            count.Should().Be(2);
            return 0;
        });
    }

    private static async Task SeedClosedPeriodAsync(TripMateApiFactory factory, string periodKey, int month)
    {
        await factory.WithDbContextAsync(async db =>
        {
            db.ClosedStatisticalPeriods.Add(ClosedStatisticalPeriod.CreateSystemClosed(
                periodKey, new DateOnly(2099, month, 1), new DateOnly(2099, month, 28),
                new DateTimeOffset(2099, month, 28, 0, 0, 0, TimeSpan.Zero)));
            await db.SaveChangesAsync(CancellationToken.None);
            return 0;
        });
    }
}
