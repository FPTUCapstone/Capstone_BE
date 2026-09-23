using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Services;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.SignOut;

public class AuditRetentionTests
{
    [Fact]
    public async Task DeleteExpiredSignOutAuditEvents_UsesStrictThirtyDayBoundaryAndKeepsOtherEvents()
    {
        var now = new DateTimeOffset(2026, 9, 20, 2, 0, 0, TimeSpan.Zero);
        var cutoff = now.AddDays(-30);
        await using var db = TestDbContext.Create();
        db.AuditLogs.AddRange(
            AuditLog.CreateSignOut(1, 10, cutoff.AddTicks(-1), "Web", null, null),
            AuditLog.CreateSignOut(1, 11, cutoff, "Web", null, null),
            AuditLog.CreateSignOut(1, 12, now.AddDays(-29).AddHours(-23), "Mobile", null, null),
            AuditLog.CreatePoiCreated(1, 1, "{}", cutoff.AddDays(-10)));
        await db.SaveChangesAsync();
        var service = new AuditService(db, NullLogger<AuditService>.Instance);

        var deleted = await service.DeleteExpiredSignOutAuditEventsAsync(
            cutoff,
            CancellationToken.None);

        deleted.Should().Be(1);
        var remaining = await db.AuditLogs.OrderBy(audit => audit.Id).ToListAsync();
        remaining.Should().HaveCount(3);
        remaining.Should().Contain(audit => audit.CreatedAtUtc == cutoff);
        remaining.Should().Contain(audit => audit.ActionType == "POI_CREATE");
    }
}