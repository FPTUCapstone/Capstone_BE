using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Admin.Tours.Review;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Admin.Tours.Review;

/// <summary>
/// Reject must satisfy the same shared moderation invariant as Approve: a Pending Tour receives at most
/// ONE successful review decision, executed in a serializable transaction with bounded deadlock retry and
/// MSG127 persistence-failure semantics.
/// </summary>
public sealed class RejectTourCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    private const string ValidReason = "The itinerary does not match the submitted price.";
    private const string Msg127 = "TripMate is temporarily unable to process your request. Please check your connection and try again.";

    private static RejectTourCommandHandler CreateHandler(TestDbContext db, long? userId = 55, string? role = nameof(UserRole.Staff)) =>
        new(db, new FakeCurrentUserService { UserId = userId, Role = role }, new FakeDateTimeProvider { UtcNow = Now });

    private static async Task<Tour> SeedPendingTourAsync(TestDbContext db)
    {
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        return await PublicTourTestData.AddTourAsync(db, 1, TourStatus.Pending);
    }

    [Fact]
    public async Task Handle_WhenCallerNotStaff_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();

        var result = await CreateHandler(db, userId: 99, role: nameof(UserRole.Administrator))
            .Handle(new RejectTourCommand(100, ValidReason), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AdminAccessRequired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_WhenReasonMissing_ReturnsRejectionReasonInvalid(string? reason)
    {
        await using var db = TestDbContext.Create();
        var tour = await SeedPendingTourAsync(db);

        var result = await CreateHandler(db).Handle(new RejectTourCommand(tour.Id, reason!), CancellationToken.None);

        result.ErrorCode.Should().Be(OperatorTourErrorCodes.RejectionReasonInvalid);
        (await db.Tours.AsNoTracking().SingleAsync(t => t.Id == tour.Id)).Status.Should().Be(TourStatus.Pending);
    }

    [Fact]
    public async Task Handle_WhenTourMissing_ReturnsNotFound()
    {
        await using var db = TestDbContext.Create();

        var result = await CreateHandler(db).Handle(new RejectTourCommand(404, ValidReason), CancellationToken.None);

        result.ErrorCode.Should().Be(OperatorTourErrorCodes.TourNotFound);
    }

    [Fact]
    public async Task Handle_WhenTourPending_RejectsAndWritesSingleAuditInOneSerializableTransaction()
    {
        await using var db = TestDbContext.Create();
        var tour = await SeedPendingTourAsync(db);

        var result = await CreateHandler(db).Handle(new RejectTourCommand(tour.Id, ValidReason), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: $"{result.ErrorCode}: {result.ErrorMessage}");
        result.Value.Status.Should().Be(TourStatus.Rejected.ToString());
        var stored = await db.Tours.AsNoTracking().SingleAsync(t => t.Id == tour.Id);
        stored.Status.Should().Be(TourStatus.Rejected);
        stored.RejectionReason.Should().Be(ValidReason);
        (await db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.AffectedEntityId == tour.Id && a.ActionType == AuditActionTypes.TourRejected))
            .Should().Be(1);
        db.TransactionExecutionCount.Should().Be(1, "the decision must run inside the shared serializable transaction");
    }

    [Theory]
    [InlineData(TourStatus.Approved)]
    [InlineData(TourStatus.Rejected)]
    [InlineData(TourStatus.Draft)]
    public async Task Handle_WhenTourAlreadyDecidedOrNotPending_ReturnsInvalidStateAndWritesNoAudit(TourStatus status)
    {
        await using var db = TestDbContext.Create();
        await PublicTourTestData.SeedOperatorAsync(db, 1);
        var tour = await PublicTourTestData.AddTourAsync(db, 1, status);

        var result = await CreateHandler(db).Handle(new RejectTourCommand(tour.Id, ValidReason), CancellationToken.None);

        result.ErrorCode.Should().Be(OperatorTourErrorCodes.InvalidState);
        (await db.Tours.AsNoTracking().SingleAsync(t => t.Id == tour.Id)).Status.Should().Be(status);
        (await db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.AffectedEntityId == tour.Id && a.ActionType == AuditActionTypes.TourRejected))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WhenTransientDeadlockOccurs_RetriesAndSucceedsOnNextAttempt()
    {
        await using var db = TestDbContext.Create();
        var tour = await SeedPendingTourAsync(db);
        var initialSaves = db.SaveChangesAsyncCallCount;
        db.SaveChangesAttemptFailureFactory = attempt =>
            attempt == initialSaves + 1 ? new SimulatedDeadlockException() : null;

        var result = await CreateHandler(db).Handle(new RejectTourCommand(tour.Id, ValidReason), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: $"{result.ErrorCode}: {result.ErrorMessage}");
        (await db.Tours.AsNoTracking().SingleAsync(t => t.Id == tour.Id)).Status.Should().Be(TourStatus.Rejected);
        db.SaveChangesAsyncCallCount.Should().Be(initialSaves + 2);
    }

    [Fact]
    public async Task Handle_WhenDeadlockOccursAndTourDecidedConcurrently_RetriesAndReturnsInvalidState()
    {
        await using var db = TestDbContext.Create();
        var tour = await SeedPendingTourAsync(db);
        db.SerializableTransactionCompletionFailureFactory = attempt =>
            attempt == 1 ? new SimulatedDeadlockException() : null;

        var result = await CreateHandler(db).Handle(new RejectTourCommand(tour.Id, ValidReason), CancellationToken.None);

        // A deadlock must be retried, and the retry must re-read authoritative state instead of surfacing a 500.
        result.IsSuccess.Should().BeFalse();
        db.TransactionExecutionCount.Should().Be(2);
        result.ErrorCode.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Handle_WhenDeadlockExhaustsRetries_ReturnsServiceUnavailableMsg127_AndLeavesTourPending()
    {
        await using var db = TestDbContext.Create();
        var tour = await SeedPendingTourAsync(db);
        db.SaveChangesFailureFactory = () => new SimulatedDeadlockException();

        var result = await CreateHandler(db).Handle(new RejectTourCommand(tour.Id, ValidReason), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.ServiceUnavailable);
        result.ErrorMessage.Should().Be(Msg127);
        db.TransactionExecutionCount.Should().Be(3);
        (await db.Tours.AsNoTracking().SingleAsync(t => t.Id == tour.Id)).Status.Should().Be(TourStatus.Pending);
        (await db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.AffectedEntityId == tour.Id && a.ActionType == AuditActionTypes.TourRejected))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WhenUnrecoverableSaveFailureOccurs_ReturnsServiceUnavailableMsg127_WithoutRetrying()
    {
        await using var db = TestDbContext.Create();
        var tour = await SeedPendingTourAsync(db);
        db.SaveChangesFailureFactory = () => new DbUpdateException("Simulated storage outage.");

        var result = await CreateHandler(db).Handle(new RejectTourCommand(tour.Id, ValidReason), CancellationToken.None);

        result.ErrorCode.Should().Be(OperatorTourErrorCodes.ServiceUnavailable);
        result.ErrorMessage.Should().Be(Msg127);
        db.TransactionExecutionCount.Should().Be(1);
        (await db.Tours.AsNoTracking().SingleAsync(t => t.Id == tour.Id)).Status.Should().Be(TourStatus.Pending);
    }

    [Fact]
    public async Task Handle_WhenUnexpectedProgrammingErrorOccurs_DoesNotDisguiseAsMsg127()
    {
        await using var db = TestDbContext.Create();
        var tour = await SeedPendingTourAsync(db);
        db.SaveChangesFailureFactory = () => new InvalidOperationException("Unexpected programming invariant failure.");

        var act = () => CreateHandler(db).Handle(new RejectTourCommand(tour.Id, ValidReason), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Unexpected programming invariant failure.");
        db.TransactionExecutionCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_WhenCancellationRequested_PropagatesOperationCanceledException()
    {
        await using var db = TestDbContext.Create();
        var tour = await SeedPendingTourAsync(db);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => CreateHandler(db).Handle(new RejectTourCommand(tour.Id, ValidReason), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}