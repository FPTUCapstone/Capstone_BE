using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.CheckIn.Audit;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Services.Audit;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class CheckInAuditProjectorTests
{
    [Fact]
    public async Task Project_ReplayOfIdenticalEvent_IsDuplicateWithOneSqlEffect()
    {
        await using ApplicationDbContext db = CreateContext();
        var projector = new CheckInAuditProjector(db);
        CheckInAuditEvent auditEvent = Rejection();

        (await projector.ProjectAsync(auditEvent, CancellationToken.None))
            .Should().Be(CheckInAuditProjectionResult.Projected);
        (await projector.ProjectAsync(auditEvent, CancellationToken.None))
            .Should().Be(CheckInAuditProjectionResult.Duplicate);
        (await db.AuditLogs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Project_ExistingEventIdWithDifferentSemanticContent_IsConflict()
    {
        await using ApplicationDbContext db = CreateContext();
        CheckInAuditEvent auditEvent = Rejection();
        db.AuditLogs.Add(AuditLog.CreateCheckInJournalProjection(
            auditEvent.ActorUserId, auditEvent.ScheduleId, auditEvent.TicketId, auditEvent.ScanAttemptId,
            auditEvent.EventId, "checkin.already_used", auditEvent.OccurredAtUtc));
        await db.SaveChangesAsync();

        var projector = new CheckInAuditProjector(db);
        (await projector.ProjectAsync(auditEvent, CancellationToken.None))
            .Should().Be(CheckInAuditProjectionResult.Conflict);
    }

    [Fact]
    public async Task Project_ExistingEventIdWithDifferentSchedule_IsConflict()
    {
        await using ApplicationDbContext db = CreateContext();
        CheckInAuditEvent auditEvent = Rejection();
        db.AuditLogs.Add(AuditLog.CreateCheckInJournalProjection(
            auditEvent.ActorUserId, auditEvent.ScheduleId + 1, auditEvent.TicketId,
            auditEvent.ScanAttemptId, auditEvent.EventId, auditEvent.ReasonCode!,
            auditEvent.OccurredAtUtc));
        await db.SaveChangesAsync();

        var projector = new CheckInAuditProjector(db);
        (await projector.ProjectAsync(auditEvent, CancellationToken.None))
            .Should().Be(CheckInAuditProjectionResult.Conflict);
    }

    [Fact]
    public async Task Project_Admission_IsRejectedAndNeverCreatesSqlAudit()
    {
        await using ApplicationDbContext db = CreateContext();
        var projector = new CheckInAuditProjector(db);
        CheckInAuditEvent admission = CheckInAuditEvent.Admission(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, 7, 9);

        Func<Task> act = () => projector.ProjectAsync(admission, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.AuditLogs.CountAsync()).Should().Be(0);
    }

    private static CheckInAuditEvent Rejection() => CheckInAuditEvent.Rejected(
        Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Parse("2026-11-05T01:02:03Z"),
        7, 9, 11, "checkin.invalid_qr");

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"tm89-{Guid.NewGuid():N}")
            .Options;
        return new ApplicationDbContext(options);
    }
}