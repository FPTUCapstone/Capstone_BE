using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Domain;

public class RevenueExportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    private static RevenueExport CreateQueued() =>
        RevenueExport.Create(
            operatorUserId: 101,
            startDate: new DateOnly(2026, 8, 1),
            endDate: new DateOnly(2026, 8, 31),
            granularity: "monthly",
            tourId: 142,
            format: RevenueExportFormat.Xlsx,
            scope: RevenueExportScope.SummaryAndDetails,
            requestedAtUtc: Now);

    [Fact]
    public void Create_SetsImmutableFilterSnapshotAndQueuedStatus()
    {
        var export = CreateQueued();

        export.Status.Should().Be(RevenueExportStatus.Queued);
        export.OperatorUserId.Should().Be(101);
        export.StartDate.Should().Be(new DateOnly(2026, 8, 1));
        export.EndDate.Should().Be(new DateOnly(2026, 8, 31));
        export.TourId.Should().Be(142);
        export.Format.Should().Be(RevenueExportFormat.Xlsx);
        export.Scope.Should().Be(RevenueExportScope.SummaryAndDetails);
        export.RequestedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Create_WithEndDateBeforeStartDate_Throws()
    {
        var act = () => RevenueExport.Create(
            101, new DateOnly(2026, 8, 31), new DateOnly(2026, 8, 1), "monthly", null,
            RevenueExportFormat.Csv, RevenueExportScope.Summary, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkReady_FromQueued_SucceedsForTheSynchronousPath()
    {
        var export = CreateQueued();

        var changed = export.MarkReady("file.xlsx", "storage/key", rowCount: 12,
            expiresAtUtc: Now.AddDays(7), nowUtc: Now.AddSeconds(2));

        changed.Should().BeTrue();
        export.Status.Should().Be(RevenueExportStatus.Ready);
        export.FileName.Should().Be("file.xlsx");
        export.StorageKey.Should().Be("storage/key");
        export.RowCount.Should().Be(12);
        export.CompletedAtUtc.Should().Be(Now.AddSeconds(2));
        export.IsDownloadable(Now.AddSeconds(3)).Should().BeTrue();
    }

    [Fact]
    public void ClaimForProcessing_FromQueued_TransitionsOnceAndRejectsSecondClaim()
    {
        var export = CreateQueued();

        export.ClaimForProcessing(Now).Should().BeTrue();
        export.Status.Should().Be(RevenueExportStatus.Processing);

        // A second worker racing to claim the same (now Processing) record must be rejected.
        export.ClaimForProcessing(Now).Should().BeFalse();
    }

    [Fact]
    public void MarkReady_FromProcessing_SucceedsForTheAsynchronousWorkerPath()
    {
        var export = CreateQueued();
        export.ClaimForProcessing(Now);

        var changed = export.MarkReady("file.pdf", "storage/key2", rowCount: 500,
            expiresAtUtc: Now.AddDays(7), nowUtc: Now.AddMinutes(1));

        changed.Should().BeTrue();
        export.Status.Should().Be(RevenueExportStatus.Ready);
    }

    [Fact]
    public void MarkReady_OnAnAlreadyTerminalRecord_IsRejected()
    {
        var export = CreateQueued();
        export.MarkFailed("render_error", Now);

        var changed = export.MarkReady("file.xlsx", "storage/key", 1, Now.AddDays(7), Now);

        changed.Should().BeFalse("a Failed export must never be reopened to Ready");
        export.Status.Should().Be(RevenueExportStatus.Failed);
    }

    [Fact]
    public void MarkFailed_RecordsFailureCodeAndCompletionTime()
    {
        var export = CreateQueued();

        var changed = export.MarkFailed("storage_unavailable", Now.AddSeconds(5));

        changed.Should().BeTrue();
        export.Status.Should().Be(RevenueExportStatus.Failed);
        export.FailureCode.Should().Be("storage_unavailable");
        export.CompletedAtUtc.Should().Be(Now.AddSeconds(5));
    }

    [Fact]
    public void MarkExpired_OnlyFromReady_AndNeverReExpiresATerminalNonReadyRecord()
    {
        var export = CreateQueued();
        export.MarkFailed("render_error", Now);

        export.MarkExpired(Now.AddDays(8)).Should().BeFalse();
        export.Status.Should().Be(RevenueExportStatus.Failed);
    }

    [Fact]
    public void IsDownloadable_AfterExpiryTimestamp_ReturnsFalseEvenWhileStatusIsStillReady()
    {
        var export = CreateQueued();
        export.MarkReady("file.csv", "storage/key", 3, expiresAtUtc: Now.AddDays(7), nowUtc: Now);

        export.IsDownloadable(Now.AddDays(8)).Should().BeFalse();
    }
}