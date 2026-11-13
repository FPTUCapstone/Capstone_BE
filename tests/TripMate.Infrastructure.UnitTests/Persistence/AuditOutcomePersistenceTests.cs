using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public class AuditOutcomePersistenceTests
{
    [Fact]
    public void JournalIdentityMapping_UsesNullableUniqueIdentifiersAndFilteredUniqueIndex()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(new SqlConnection()).Options);
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(AuditLog))!;
        var table = StoreObjectIdentifier.Table("AuditLogs", "dbo");

        var attempt = entity.FindProperty(nameof(AuditLog.ScanAttemptId))!;
        attempt.GetColumnName(table).Should().Be("scan_attempt_id");
        attempt.GetColumnType().Should().Be("uniqueidentifier");
        attempt.IsNullable.Should().BeTrue();

        var journalEvent = entity.FindProperty(nameof(AuditLog.JournalEventId))!;
        journalEvent.GetColumnName(table).Should().Be("journal_event_id");
        journalEvent.GetColumnType().Should().Be("uniqueidentifier");
        journalEvent.IsNullable.Should().BeTrue();

        var index = entity.GetIndexes().Single(candidate =>
            candidate.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AuditLog.JournalEventId)]));
        index.IsUnique.Should().BeTrue();
        index.GetDatabaseName().Should().Be("UX_AuditLogs_JournalEventId");
        index.GetFilter().Should().Be("[journal_event_id] IS NOT NULL");
    }

    [Fact]
    public void CheckInJournalProjection_RequiresNonEmptyIdentityAndPreservesSafeOutcome()
    {
        Guid attemptId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 11, 5, 4, 30, 0, TimeSpan.Zero);

        var audit = AuditLog.CreateCheckInJournalProjection(
            actorUserId: 17,
            scheduleId: 23,
            ticketId: 29,
            scanAttemptId: attemptId,
            journalEventId: eventId,
            reasonCode: "checkin.invalid_qr",
            occurredAtUtc: occurredAt);

        audit.ScanAttemptId.Should().Be(attemptId);
        audit.JournalEventId.Should().Be(eventId);
        audit.ActorUserId.Should().Be(17);
        audit.AffectedEntityId.Should().Be(29);
        audit.ActionType.Should().Be(TripMate.Domain.Common.AuditActionTypes.CheckInScanRejected);
        audit.AffectedEntity.Should().Be(TripMate.Domain.Common.AuditEntityTypes.CheckIn);
        audit.Result.Should().Be(AuditOutcome.Failure);
        audit.Reason.Should().Be("checkin.invalid_qr");
        audit.AfterData.Should().Be("{\"scheduleId\":23}");
        audit.CreatedAtUtc.Should().Be(occurredAt);

        var emptyAttempt = () => AuditLog.CreateCheckInJournalProjection(
            17, 23, 29, Guid.Empty, eventId, "checkin.invalid_qr", occurredAt);
        emptyAttempt.Should().Throw<ArgumentException>();
        var emptyEvent = () => AuditLog.CreateCheckInJournalProjection(
            17, 23, 29, attemptId, Guid.Empty, "checkin.invalid_qr", occurredAt);
        emptyEvent.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void OutcomeMapping_PreservesLegacyNullAndUsesConstrainedStringColumns()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(new SqlConnection()).Options);
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(AuditLog))!;
        var table = StoreObjectIdentifier.Table("AuditLogs", "dbo");
        var outcome = entity.FindProperty(nameof(AuditLog.Result))!;
        outcome.GetColumnName(table).Should().Be("result");
        outcome.GetColumnType().Should().Be("varchar(20)");
        outcome.IsNullable.Should().BeTrue();
        var converter = outcome.GetTypeMapping().Converter!;
        converter.ConvertToProvider(AuditOutcome.Success).Should().Be("Success");
        converter.ConvertToProvider(AuditOutcome.Failure).Should().Be("Failure");
        converter.ConvertFromProvider("Failure").Should().Be(AuditOutcome.Failure);
        converter.ConvertToProvider(null).Should().BeNull();
        outcome.GetDefaultValueSql().Should().BeNull();
        var reason = entity.FindProperty(nameof(AuditLog.Reason))!;
        reason.GetColumnName(table).Should().Be("reason");
        reason.GetColumnType().Should().Be("nvarchar(1000)");
        reason.IsNullable.Should().BeTrue();
    }
}