using FluentAssertions;

using TripMate.Application.Features.Admin.AuditLogs.GetDetail;
using TripMate.Application.Features.Admin.AuditLogs.GetList;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Admin.AuditLogs.GetDetail;

public class AuditLogOutcomeTests
{
    [Fact]
    public void LegacyLog_DoesNotInventOutcomeOrReason()
    {
        var log = new AuditLog(null, "Update", "POI", 1, DateTimeOffset.UtcNow);
        log.Result.Should().BeNull();
        log.Reason.Should().BeNull();
    }

    [Fact]
    public void ExistingProductionFactories_RecordSuccess()
    {
        AuditLog.CreatePoiCreated(1, 2, "{}", DateTimeOffset.UtcNow).Result.Should().Be(AuditOutcome.Success);
        AuditLog.CreateOperatorApplicationApproved(1, 2, "{}", "{}", DateTimeOffset.UtcNow)
            .Result.Should().Be(AuditOutcome.Success);
    }

    [Fact]
    public void RecordedOutcome_RejectsUnknownOutcomeAndOversizedReason()
    {
        Action invalid = () => AuditLog.CreateRecordedOutcome(null, "Update", "POI", 1,
            DateTimeOffset.UtcNow, (AuditOutcome)42);
        invalid.Should().Throw<ArgumentOutOfRangeException>();
        Action oversized = () => AuditLog.CreateRecordedOutcome(null, "Update", "POI", 1,
            DateTimeOffset.UtcNow, AuditOutcome.Failure, new string('a', 1001));
        oversized.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(AuditOutcome.Success)]
    [InlineData(AuditOutcome.Failure)]
    public async Task Queries_ExposeRecordedOutcomeAndBusinessReason(AuditOutcome outcome)
    {
        using var db = TestDbContext.Create();
        var log = AuditLog.CreateRecordedOutcome(null, "Review", "OperatorProfile", 2,
            DateTimeOffset.UtcNow, outcome, "Giấy phép không hợp lệ");
        db.AuditLogs.Add(log);
        await db.SaveChangesAsync();
        var user = new FakeCurrentUserService { UserId = 1, Role = "Administrator" };
        var detail = await new GetAuditLogDetailQueryHandler(db, user).Handle(new(log.Id), default);
        detail.Value.Result.Should().Be(outcome.ToString());
        detail.Value.Reason.Should().Be("Giấy phép không hợp lệ");
        var list = await new GetAuditLogsQueryHandler(db, user).Handle(new(), default);
        list.Value.Items.Single().Result.Should().Be(outcome.ToString());
    }
}