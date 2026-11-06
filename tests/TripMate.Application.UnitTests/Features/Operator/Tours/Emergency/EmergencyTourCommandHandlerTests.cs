using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.Tours.Emergency;
using TripMate.Application.Features.Operator.Tours.Emergency.AddEvidence;
using TripMate.Application.Features.Operator.Tours.Emergency.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.Emergency;

public sealed class EmergencyTourCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FakePasswordHasher PasswordHasher = new();
    private static readonly NoOpWeatherSafetyReviewNotifier Notifier = new();
    private const string ValidOperatorPassword = "OperatorPass123!";

    [Fact]
    public async Task CancelBeforeDeparture_CancelsOnlyTargetOnceAndCreatesNoRefund()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(1));
        var paid = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed, PaymentStatus.Paid, 100_000m);
        SeedBooking(db, schedule, 11, 4, BookingStatus.PendingPayment, PaymentStatus.Unpaid, 50_000m);
        schedule.ReserveCapacity(2);
        SeedSuccessfulPayment(db, paid, 100_000m);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);
        var commandId = Guid.NewGuid();
        var command = new CancelBeforeDepartureCommand(1, schedule.Id, commandId,
            preview.Value.ImpactVersion, EmergencyTourReason.WeatherEmergency,
            "Typhoon makes the departure unsafe.", "0900000000", "bulletin-123");
        var handler = new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier);

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        schedule.Status.Should().Be(TourScheduleStatus.Cancelled);
        first.Value.AffectedBookingCount.Should().Be(2);
        replay.Value.Should().Be(first.Value, "a replay answers with the committed decision");
        var stored = await db.EmergencyTourCancellationOperations.SingleAsync();
        stored.AffectedBookingCount.Should().Be(2);
        stored.ReasonCategory.Should().Be(nameof(EmergencyTourReason.WeatherEmergency));
        db.Bookings.Should().OnlyContain(booking => booking.Status == BookingStatus.Cancelled);
        db.Refunds.Should().BeEmpty();
        paid.PaymentStatus.Should().Be(PaymentStatus.Paid);
        db.EmergencyTourCancellationOperations.Should().ContainSingle();
        db.Notifications.Should().HaveCount(2); // One per booking.
    }

    [Fact]
    public async Task CancelBeforeDeparture_OfAPaidBooking_LeavesPaymentsAndPayoutsUntouched()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(4));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed,
            PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking, 100_000m);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, EmergencyTourReason.WeatherEmergency,
                "Typhoon makes the departure unsafe.", "0900000000"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        booking.Status.Should().Be(BookingStatus.Cancelled);
        booking.PaymentStatus.Should().Be(PaymentStatus.Paid);
        db.Refunds.Should().BeEmpty();
        db.PaymentTransactions.Should().ContainSingle()
            .Which.TransactionType.Should().Be(PaymentTransaction.TypePayment);
        db.Payouts.Should().BeEmpty();
        db.PayoutItems.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_ReturnsTotalPassengerCountAcrossAffectedBookings()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(4));
        SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed,
            PaymentStatus.Paid, 100_000m, quantity: 2);
        SeedBooking(db, schedule, 11, 4, BookingStatus.PendingPayment,
            PaymentStatus.Unpaid, 50_000m, quantity: 3);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };

        var result = await new GetEmergencyTourImpactQueryHandler(
                db, currentUser, new FakeDateTimeProvider { UtcNow = Now })
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalPassengerCount.Should().Be(5);
    }

    [Fact]
    public async Task EmergencyStopActive_InterruptsCommercialSessionAndCreatesNoRefund()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddHours(-1));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed, PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking, 100_000m);
        SeedCommercialSession(db, schedule.Tour, booking);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);
        var command = new EmergencyStopActiveCommand(1, schedule.Id, Guid.NewGuid(),
            preview.Value.ImpactVersion, ValidOperatorPassword, EmergencyTourReason.SafetyIncident,
            "Road access is unsafe and the trip must stop.",
            "Stay with the guide at the verified meeting point.", "0900000000",
            "incident-123");

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        schedule.Status.Should().Be(TourScheduleStatus.EmergencyStopped);
        booking.Status.Should().Be(BookingStatus.Interrupted);
        (await db.TripSessions.SingleAsync()).FsmState.Should().Be(TripSession.InterruptedState);
        booking.PaymentStatus.Should().Be(PaymentStatus.Paid);
        db.Refunds.Should().BeEmpty();
        db.Notifications.Should().Contain(notification =>
            notification.UserId == booking.TravelerUserId
            && notification.Body != null
            && notification.Body == Notification.TripUpdateBody);
    }

    [Fact]
    public async Task EmergencyStopActive_PendingPaymentBookingIsInterruptedNotCancelled()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddHours(-1));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.PendingPayment,
            PaymentStatus.Unpaid, 50_000m);
        schedule.ReserveCapacity(1);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new EmergencyStopActiveCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, ValidOperatorPassword, EmergencyTourReason.SafetyIncident,
                "Road access is unsafe and the trip must stop.",
                "Stay with the guide at the verified meeting point.", "0900000000"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        booking.Status.Should().Be(BookingStatus.Interrupted);
        schedule.ReservedCapacity.Should().Be(1, "an active stop never releases capacity");
    }

    [Fact]
    public async Task CancelBeforeDeparture_PendingPaymentBookingIsCancelledAndReleasesCapacity()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(4));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.PendingPayment,
            PaymentStatus.Unpaid, 50_000m);
        schedule.ReserveCapacity(1);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, EmergencyTourReason.WeatherEmergency,
                "Typhoon makes the departure unsafe."), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        booking.Status.Should().Be(BookingStatus.Cancelled);
        schedule.ReservedCapacity.Should().Be(0, "a pre-start cancellation releases capacity");
    }

    [Fact]
    public async Task CancelBeforeDeparture_OfAnUnpaidBooking_CreatesNoRefund()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(4));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.PendingPayment,
            PaymentStatus.Unpaid, 50_000m);
        schedule.ReserveCapacity(1);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, EmergencyTourReason.WeatherEmergency,
                "Typhoon makes the departure unsafe."), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        booking.Status.Should().Be(BookingStatus.Cancelled);
        db.Refunds.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelBeforeDeparture_OfMixedBookings_CancelsBothAndCreatesNoRefund()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(4));
        var paidBooking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed,
            PaymentStatus.Paid, 100_000m, quantity: 2);
        var unpaidBooking = SeedBooking(db, schedule, 11, 4, BookingStatus.PendingPayment,
            PaymentStatus.Unpaid, 50_000m, quantity: 3);
        schedule.ReserveCapacity(5);
        SeedSuccessfulPayment(db, paidBooking, 100_000m);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, EmergencyTourReason.WeatherEmergency,
                "Typhoon makes the departure unsafe."), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        paidBooking.Status.Should().Be(BookingStatus.Cancelled);
        unpaidBooking.Status.Should().Be(BookingStatus.Cancelled);
        db.Refunds.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelBeforeDeparture_WithUnverifiedSession_FailsClosedWithoutMutation()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(4));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed,
            PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking, 100_000m);
        SeedUnverifiedSession(db, schedule.Tour, booking.TravelerUserId);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, EmergencyTourReason.TransportFailure,
                "The contracted vehicle is unavailable.", "0900000000", null),
                CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(EmergencyTourErrorCodes.TripSessionLinkUnverified);
        schedule.Status.Should().Be(TourScheduleStatus.Scheduled);
        booking.Status.Should().Be(BookingStatus.Confirmed);
        db.EmergencyTourCancellationOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelBeforeDeparture_WhenSessionStartedEarly_InterruptsThatSession()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddHours(1));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed,
            PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking, 100_000m);
        TripSession session = SeedCommercialSession(db, schedule.Tour, booking);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, EmergencyTourReason.TransportFailure,
                "The contracted vehicle is unavailable.", "0900000000", null),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        schedule.Status.Should().Be(TourScheduleStatus.Cancelled);
        booking.Status.Should().Be(BookingStatus.Cancelled);
        session.FsmState.Should().Be(TripSession.InterruptedState);
        session.EndedAtUtc.Should().Be(Now);
    }

    [Fact]
    public async Task EmergencyStop_WithWrongPassword_RecordsAFailedAuditWithoutThePassword()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddHours(-1));
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        const string wrongPassword = "NotTheRightPassword1!";

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new EmergencyStopActiveCommand(1, schedule.Id, Guid.NewGuid(),
                new string('a', 64), wrongPassword, EmergencyTourReason.SafetyIncident,
                "Road access is unsafe and the trip must stop.",
                "Stay with the guide at the verified meeting point.", "0900000000", null),
                CancellationToken.None);

        result.ErrorCode.Should().Be(EmergencyTourErrorCodes.InvalidPassword);
        AuditLog audit = await db.AuditLogs.SingleAsync();
        audit.ActionType.Should().Be(AuditActionTypes.TourEmergencyStopReauthenticationFailed);
        audit.Result.Should().Be(AuditOutcome.Failure);
        audit.ActorUserId.Should().Be(2);
        audit.AffectedEntityId.Should().Be(schedule.Id);
        string stored = System.Text.Json.JsonSerializer.Serialize(new { audit.Reason, audit.BeforeData, audit.AfterData });
        stored.Should().NotContain(wrongPassword);
    }

    [Theory]
    [InlineData("NotTheRightPassword1!")]
    [InlineData(ValidOperatorPassword)]
    public async Task EmergencyStop_ByAnotherOperator_ReturnsNotFoundBeforeCheckingThePassword(string password)
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddHours(-1));
        SeedApprovedOperator(db, 99, "other-operator@example.test");
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 99, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new EmergencyStopActiveCommand(1, schedule.Id, Guid.NewGuid(),
                new string('a', 64), password, EmergencyTourReason.SafetyIncident,
                "Road access is unsafe and the trip must stop.",
                "Stay with the guide at the verified meeting point.", "0900000000", null),
                CancellationToken.None);

        // The same safe 404 whether the password is right or wrong: nothing is learned about
        // the password and no audit row points at another Operator's departure.
        result.ErrorCode.Should().Be(EmergencyTourErrorCodes.NotFound);
        (await db.AuditLogs.CountAsync()).Should().Be(0);
        schedule.Status.Should().Be(TourScheduleStatus.Scheduled);
    }

    [Fact]
    public async Task CancelBeforeDeparture_WhenTheSaveFailsForAnUnclassifiedReason_DoesNotRetry()
    {
        // A FK or CHECK violation is a real defect; replaying the decision cannot fix it.
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(1));
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);
        int savesBefore = db.SaveChangesAsyncCallCount;
        db.SaveChangesFailureFactory = () => new DbUpdateException("Simulated CHECK constraint violation.");

        var act = () => new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(), preview.Value.ImpactVersion,
                EmergencyTourReason.WeatherEmergency, "Unsafe weather prevents departure.", "0900000000", "bulletin-123"),
                CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        (db.SaveChangesAsyncCallCount - savesBefore).Should().Be(1);
    }

    [Fact]
    public async Task CancelBeforeDeparture_WhenItLosesTheOneDecisionRace_RetriesSoTheReplayPathAnswers()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(1));
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);
        int failures = 0;
        db.SaveChangesFailureFactory = () => failures++ == 0
            ? new DbUpdateException("duplicate", new SimulatedEmergencyDecisionUniqueConflictException())
            : null;

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(), preview.Value.ImpactVersion,
                EmergencyTourReason.WeatherEmergency, "Unsafe weather prevents departure.", "0900000000", "bulletin-123"),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        failures.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task CancelBeforeDeparture_WhenChosenAsADeadlockVictim_RetriesAndCommits()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(1));
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);
        int failures = 0;
        db.SaveChangesFailureFactory = () => failures++ == 0 ? new DbUpdateException("deadlock", new SimulatedDeadlockException()) : null;

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(), preview.Value.ImpactVersion,
                EmergencyTourReason.WeatherEmergency, "Unsafe weather prevents departure.", "0900000000", "bulletin-123"),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
    }

    [Fact]
    public async Task Preview_CountsOnlyValidTicketsAsUnused()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(1));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed, PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        db.Tickets.AddRange(
            Ticket.Create(booking.Id, TicketStatus.Valid),
            Ticket.Create(booking.Id, TicketStatus.Used),
            Ticket.Create(booking.Id, TicketStatus.Cancelled),
            Ticket.Create(booking.Id, TicketStatus.Expired));
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };

        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        preview.Value.UnusedTicketCount.Should().Be(1);
    }

    [Fact]
    public async Task Preview_ReportsUnverifiedSessionBlockerAndRotatesImpactVersion()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(4));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed,
            PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking, 100_000m);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var handler = new GetEmergencyTourImpactQueryHandler(db, currentUser, clock);

        var clean = await handler.Handle(
            new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);
        clean.IsSuccess.Should().BeTrue();
        clean.Value.HasUnverifiedSessions.Should().BeFalse();

        // A session on an itinerary without an exact booking link: legacy/unverified identity.
        SeedUnverifiedSession(db, schedule.Tour, booking.TravelerUserId);
        await db.SaveChangesAsync();

        var blocked = await handler.Handle(
            new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);
        blocked.IsSuccess.Should().BeTrue();
        blocked.Value.HasUnverifiedSessions.Should().BeTrue();
        blocked.Value.ImpactVersion.Should().NotBe(clean.Value.ImpactVersion,
            "a blocker state change changes whether the command can commit");
    }

    [Fact]
    public async Task Confirm_WhenImpactChanged_ReturnsConflictWithoutMutation()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(1));
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);
        SeedBooking(db, schedule, 10, 3, BookingStatus.PendingPayment, PaymentStatus.Unpaid, 50_000m);
        await db.SaveChangesAsync();

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, EmergencyTourReason.TransportFailure,
                "The contracted vehicle is unavailable.", "0900000000", null),
                CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(EmergencyTourErrorCodes.ConcurrentStateChanged);
        schedule.Status.Should().Be(TourScheduleStatus.Scheduled);
        db.EmergencyTourCancellationOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelBeforeDeparture_OtherOperatorCannotSeeOrChangeDeparture()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(1));
        SeedApprovedOperator(db, 99, "other-operator@example.test");
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 99, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(), new string('a', 64),
                EmergencyTourReason.Other, "A documented emergency prevents operation.",
                "0900000000", null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(EmergencyTourErrorCodes.NotFound);
        schedule.Status.Should().Be(TourScheduleStatus.Scheduled);
    }

    [Fact]
    public async Task CancelBeforeDeparture_WithMaximumReasonDetails_TruncatesBoundedDependentFields()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(1));
        SeedBooking(db, schedule, 10, 3, BookingStatus.PendingPayment, PaymentStatus.Unpaid, 50_000m);
        schedule.ReserveCapacity(1);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, EmergencyTourReason.Other,
                new string('x', 1000), "0900000000", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.Bookings.Single().CancelReason.Should().HaveLength(Booking.CancelReasonMaxLength);
        db.AuditLogs.Single().Reason.Should().HaveLength(AuditLog.ReasonMaxLength);
        db.EmergencyTourCancellationOperations.Single().ReasonDetails.Should().HaveLength(1000);
    }

    [Fact]
    public async Task Preview_WhenOperatorIsSuspended_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(1));
        db.OperatorProfiles.Local.Single(profile => profile.UserId == 2).User.Status = AccountStatus.Locked;
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };

        var result = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(EmergencyTourErrorCodes.Forbidden);
    }

    [Fact]
    public async Task CancelBeforeDeparture_WithMultipleCaptures_CreatesNoRefundOrSettlement()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(1));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed,
            PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking, 60_000m);
        SeedSuccessfulPayment(db, booking, 40_000m);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, EmergencyTourReason.TransportFailure,
                "The contracted vehicle is unavailable.", "0900000000", null),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.Refunds.Should().BeEmpty();
        db.PaymentTransactions.Should().HaveCount(2)
            .And.OnlyContain(transaction => transaction.TransactionType == PaymentTransaction.TypePayment);
    }

    [Fact]
    public async Task CancelBeforeDeparture_WithAnEarlierRefund_LeavesThatRefundUntouched()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(1));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed,
            PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        PaymentTransaction deposit = SeedSuccessfulPayment(db, booking, 60_000m);
        await db.SaveChangesAsync();
        Refund priorRefund = Refund.CreatePending(booking.Id, deposit.Id, 999,
            30_000m, "Prior partial refund", RefundInitiator.Administrator, Now.AddDays(-1));
        priorRefund.TryStartAttempt(Now.AddDays(-1)).Should().BeTrue();
        priorRefund.MarkProcessed("PRIOR-30K", Now.AddDays(-1));
        db.Refunds.Add(priorRefund);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, EmergencyTourReason.TransportFailure,
                "The contracted vehicle is unavailable.", "0900000000", null),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.Refunds.Should().ContainSingle().Which.Status.Should().Be(RefundStatus.Processed);
        priorRefund.Amount.Should().Be(30_000m);
    }

    [Fact]
    public async Task EmergencyStopActive_SiblingDepartureSessionDoesNotBlockOrGetInterrupted()
    {
        await using var db = TestDbContext.Create();
        var targetSchedule = SeedSchedule(db, Now.AddHours(-1));
        var targetBooking = SeedBooking(db, targetSchedule, 10, 3, BookingStatus.Confirmed,
            PaymentStatus.Paid, 100_000m);
        targetSchedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, targetBooking, 100_000m);
        TripSession targetSession = SeedCommercialSession(db, targetSchedule.Tour, targetBooking);

        var siblingSchedule = TourSchedule.Create(
            Now.AddHours(-2), Now.AddHours(6), "Da Nang", 20, Now);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(siblingSchedule, 6L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!
            .SetValue(siblingSchedule, targetSchedule.TourId);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!
            .SetValue(siblingSchedule, targetSchedule.Tour);
        db.TourSchedules.Add(siblingSchedule);
        var siblingBooking = SeedBooking(db, siblingSchedule, 11, 3, BookingStatus.Confirmed,
            PaymentStatus.Paid, 200_000m);
        siblingSchedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, siblingBooking, 200_000m);
        TripSession siblingSession = SeedCommercialSession(db, targetSchedule.Tour, siblingBooking);
        await db.SaveChangesAsync();

        var currentUser = new FakeCurrentUserService
        {
            UserId = 2,
            Role = nameof(UserRole.TourOperator),
        };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, targetSchedule.Id), CancellationToken.None);

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new EmergencyStopActiveCommand(1, targetSchedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, ValidOperatorPassword,
                EmergencyTourReason.SafetyIncident,
                "Road access is unsafe and the trip must stop.",
                "Stay with the guide at the verified meeting point.", "0900000000",
                "incident-123"), CancellationToken.None);

        preview.Value.HasUnverifiedSessions.Should().BeFalse();
        result.IsSuccess.Should().BeTrue();
        targetSession.FsmState.Should().Be(TripSession.InterruptedState);
        siblingSession.FsmState.Should().Be(TripSession.NavigatingState);
        siblingSchedule.Status.Should().Be(TourScheduleStatus.Scheduled);
        siblingBooking.Status.Should().Be(BookingStatus.Confirmed);
    }

    [Fact]
    public async Task CancelBeforeDeparture_WhenPaidBookingHasNoCapturedPayment_CreatesNoRefund()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(1));
        SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed, PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, EmergencyTourReason.TransportFailure,
                "The contracted vehicle is unavailable.", "0900000000", null),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.Refunds.Should().BeEmpty();
    }

    [Fact]
    public async Task EmergencyStopActive_WithWrongPassword_ReturnsPasswordInvalid()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddHours(-1));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed, PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking, 100_000m);
        SeedCommercialSession(db, schedule.Tour, booking);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);
        var command = new EmergencyStopActiveCommand(1, schedule.Id, Guid.NewGuid(),
            preview.Value.ImpactVersion, "WrongPassword999!", EmergencyTourReason.SafetyIncident,
            "Road access is unsafe and the trip must stop.",
            "Stay with the guide at the verified meeting point.", "0900000000",
            "incident-123");

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(EmergencyTourErrorCodes.InvalidPassword);
        schedule.Status.Should().Be(TourScheduleStatus.Scheduled);
        db.EmergencyTourCancellationOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task EmergencyStopActive_ReplayWithWrongPassword_DoesNotBypassStepUpAuthentication()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddHours(-1));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed,
            PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking, 100_000m);
        SeedCommercialSession(db, schedule.Tour, booking);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService
        {
            UserId = 2,
            Role = nameof(UserRole.TourOperator),
        };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);
        Guid idempotencyKey = Guid.NewGuid();
        var validCommand = new EmergencyStopActiveCommand(1, schedule.Id, idempotencyKey,
            preview.Value.ImpactVersion, ValidOperatorPassword,
            EmergencyTourReason.SafetyIncident,
            "Road access is unsafe and the trip must stop.",
            "Stay with the guide at the verified meeting point.", "0900000000",
            "incident-123");
        var handler = new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier);
        var committed = await handler.Handle(validCommand, CancellationToken.None);

        var replayWithWrongPassword = validCommand with { CurrentPassword = "WrongPassword999!" };
        var replay = await handler.Handle(replayWithWrongPassword, CancellationToken.None);

        committed.IsSuccess.Should().BeTrue();
        replay.IsFailure.Should().BeTrue();
        replay.ErrorCode.Should().Be(EmergencyTourErrorCodes.InvalidPassword);
        db.EmergencyTourCancellationOperations.Should().ContainSingle();
    }

    [Fact]
    public async Task EmergencyStopActive_WithInvalidEvidenceUrl_ReturnsInvalidRequest()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddHours(-1));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed, PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking, 100_000m);
        SeedCommercialSession(db, schedule.Tour, booking);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        // HTTP URL is rejected (must be absolute HTTPS)
        var httpCommand = new EmergencyStopActiveCommand(1, schedule.Id, Guid.NewGuid(),
            preview.Value.ImpactVersion, ValidOperatorPassword, EmergencyTourReason.SafetyIncident,
            "Road access is unsafe and the trip must stop.",
            "Stay with the guide at the verified meeting point.", "0900000000",
            EvidenceUrl: "http://insecure-site.test/report");

        var httpResult = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(httpCommand, CancellationToken.None);

        httpResult.IsFailure.Should().BeTrue();
        httpResult.ErrorCode.Should().Be(EmergencyTourErrorCodes.InvalidRequest);

        // URL containing credentials (userinfo) is rejected to prevent credential leakage
        var credCommand = new EmergencyStopActiveCommand(1, schedule.Id, Guid.NewGuid(),
            preview.Value.ImpactVersion, ValidOperatorPassword, EmergencyTourReason.SafetyIncident,
            "Road access is unsafe and the trip must stop.",
            "Stay with the guide at the verified meeting point.", "0900000000",
            EvidenceUrl: "https://admin:secret@weather-agency.gov.test/notice");

        var credResult = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(credCommand, CancellationToken.None);

        credResult.IsFailure.Should().BeTrue();
        credResult.ErrorCode.Should().Be(EmergencyTourErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task EmergencyStopActive_AuthorityDirective_WithoutEvidence_FailsUnlessAddLater()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddHours(-1));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed, PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking, 100_000m);
        SeedCommercialSession(db, schedule.Tour, booking);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        // Authority directive with no reference and AddEvidenceLater = false fails
        var failCommand = new EmergencyStopActiveCommand(1, schedule.Id, Guid.NewGuid(),
            preview.Value.ImpactVersion, ValidOperatorPassword, EmergencyTourReason.AuthorityDirective,
            "Civil defense issued an evacuation order.",
            "Evacuate to high ground immediately.", "0900000000",
            EvidenceReference: null, AuthorityReference: null, AddEvidenceLater: false);

        var failResult = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(failCommand, CancellationToken.None);

        failResult.IsFailure.Should().BeTrue();
        failResult.ErrorCode.Should().Be(EmergencyTourErrorCodes.InvalidRequest);

        // Authority directive with AddEvidenceLater = true succeeds and records OperatorProvided provenance
        var okCommand = new EmergencyStopActiveCommand(1, schedule.Id, Guid.NewGuid(),
            preview.Value.ImpactVersion, ValidOperatorPassword, EmergencyTourReason.AuthorityDirective,
            "Civil defense issued an evacuation order.",
            "Evacuate to high ground immediately.", "0900000000",
            EvidenceReference: null, AuthorityReference: null, AddEvidenceLater: true);

        var okResult = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(okCommand, CancellationToken.None);

        okResult.IsSuccess.Should().BeTrue();
        var op = await db.EmergencyTourCancellationOperations.SingleAsync();
        op.EvidenceAddedLater.Should().BeTrue();
        op.EvidenceProvenance.Should().Be(EmergencyEvidenceProvenance.OperatorProvided);
    }

    [Fact]
    public async Task EmergencyStopActive_WithUnverifiedSession_ReturnsTripSessionLinkUnverified()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddHours(-1));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed, PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking, 100_000m);

        // Unverified session: session for this traveler on this tour during this window, but NO SourceBookingId!
        var unverifiedItinerary = Itinerary.CreateManual(booking.TravelerUserId, "Legacy unverified", Itinerary.ActiveStatus, Now.AddDays(-1));
        typeof(Itinerary).GetProperty(nameof(Itinerary.SourceTourId))!.SetValue(unverifiedItinerary, schedule.TourId);
        var item = TripSessionItem.Snapshot(1, 1, 1, "Stop", 16m, 108m, Now.AddHours(-1), Now.AddHours(1), true);
        var unverifiedSession = TripSession.Start(1, 1, booking.TravelerUserId, Guid.NewGuid(),
            Now.AddHours(-1), Now.AddHours(2), [item]);
        typeof(TripSession).GetProperty(nameof(TripSession.Itinerary))!.SetValue(unverifiedSession, unverifiedItinerary);
        db.Itineraries.Add(unverifiedItinerary);
        db.TripSessions.Add(unverifiedSession);

        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        var command = new EmergencyStopActiveCommand(1, schedule.Id, Guid.NewGuid(),
            preview.Value.ImpactVersion, ValidOperatorPassword, EmergencyTourReason.SafetyIncident,
            "Road access is unsafe and the trip must stop.",
            "Stay with the guide at the verified meeting point.", "0900000000",
            "incident-123");

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(EmergencyTourErrorCodes.TripSessionLinkUnverified);
        unverifiedSession.FsmState.Should().Be(TripSession.NavigatingState); // Unmutated, never interrupted!
    }

    [Fact]
    public async Task CancelBeforeDeparture_WithUsedAndUnusedTickets_RevokesOnlyUnusedTickets()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(1));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed, PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(2);
        SeedSuccessfulPayment(db, booking, 100_000m);

        // Add 2 tickets: 1 Valid (unused), 1 Used
        var unusedTicket = Ticket.Create(booking.Id, TicketStatus.Valid);
        var usedTicket = Ticket.Create(booking.Id, TicketStatus.Used);
        db.Tickets.AddRange(unusedTicket, usedTicket);

        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);

        var command = new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(),
            preview.Value.ImpactVersion, EmergencyTourReason.WeatherEmergency,
            "Severe weather prevents travel.", "0900000000");

        var result = await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        unusedTicket.Status.Should().Be(TicketStatus.Cancelled);
        usedTicket.Status.Should().Be(TicketStatus.Used); // History preserved!
    }

    [Fact]
    public async Task AddEmergencyEvidence_WhenMarkedForLater_UpdatesAuditOnly()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddHours(-1));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed,
            PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking, 100_000m);
        SeedCommercialSession(db, schedule.Tour, booking);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);
        await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new EmergencyStopActiveCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, ValidOperatorPassword, EmergencyTourReason.AuthorityDirective,
                "Civil defense issued an evacuation order.",
                "Evacuate to high ground immediately.", "0900000000",
                AddEvidenceLater: true), CancellationToken.None);
        int refundCount = await db.Refunds.CountAsync();

        var result = await new AddEmergencyTourEvidenceCommandHandler(db, currentUser, clock)
            .Handle(new AddEmergencyTourEvidenceCommand(1, schedule.Id,
                "https://authority.example.test/orders/123", "EVAC-123",
                ExpectedVersion: EmergencyEvidenceVersion.Of(
                    await db.EmergencyTourCancellationOperations.SingleAsync())),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var operation = await db.EmergencyTourCancellationOperations.SingleAsync();
        operation.EvidenceUrl.Should().Be("https://authority.example.test/orders/123");
        operation.AuthorityReference.Should().Be("EVAC-123");
        operation.EvidenceAddedLater.Should().BeFalse();
        operation.EvidenceProvenance.Should().Be(EmergencyEvidenceProvenance.OperatorProvided);
        schedule.Status.Should().Be(TourScheduleStatus.EmergencyStopped);
        db.Refunds.Should().HaveCount(refundCount);
        db.AuditLogs.Should().Contain(audit =>
            audit.ActionType == AuditActionTypes.TourEmergencyEvidenceAdded);
    }

    [Fact]
    public async Task AddEmergencyEvidence_WithoutAddLaterFlag_ReplacesEvidence()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddDays(4));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed,
            PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking, 100_000m);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);
        await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new CancelBeforeDepartureCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, EmergencyTourReason.WeatherEmergency,
                "Typhoon makes the departure unsafe.", "0900000000"), CancellationToken.None);

        var result = await new AddEmergencyTourEvidenceCommandHandler(db, currentUser, clock)
            .Handle(new AddEmergencyTourEvidenceCommand(1, schedule.Id,
                "https://authority.example.test/orders/123", "EVAC-123",
                ExpectedVersion: EmergencyEvidenceVersion.Of(
                    await db.EmergencyTourCancellationOperations.SingleAsync())),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var operation = await db.EmergencyTourCancellationOperations.SingleAsync();
        operation.EvidenceUrl.Should().Be("https://authority.example.test/orders/123");
        operation.AuthorityReference.Should().Be("EVAC-123");
        operation.EvidenceProvenance.Should().Be(EmergencyEvidenceProvenance.OperatorProvided);
        schedule.Status.Should().Be(TourScheduleStatus.Cancelled, "evidence updates never reopen the departure");
        db.AuditLogs.Should().Contain(audit =>
            audit.ActionType == AuditActionTypes.TourEmergencyEvidenceAdded);
    }

    [Fact]
    public async Task WithdrawEmergencyEvidence_ClearsEvidenceAndAuditsWithoutStateChange()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db, Now.AddHours(-1));
        var booking = SeedBooking(db, schedule, 10, 3, BookingStatus.Confirmed,
            PaymentStatus.Paid, 100_000m);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking, 100_000m);
        SeedCommercialSession(db, schedule.Tour, booking);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService { UserId = 2, Role = nameof(UserRole.TourOperator) };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);
        await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, Notifier)
            .Handle(new EmergencyStopActiveCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, ValidOperatorPassword, EmergencyTourReason.AuthorityDirective,
                "Civil defense issued an evacuation order.",
                "Evacuate to high ground immediately.", "0900000000",
                EvidenceReference: "DOC-99"), CancellationToken.None);
        int refundCount = await db.Refunds.CountAsync();

        var result = await new AddEmergencyTourEvidenceCommandHandler(db, currentUser, clock)
            .Handle(new AddEmergencyTourEvidenceCommand(1, schedule.Id,
                WithdrawEvidence: true,
                ExpectedVersion: EmergencyEvidenceVersion.Of(await db.EmergencyTourCancellationOperations.SingleAsync())), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var operation = await db.EmergencyTourCancellationOperations.SingleAsync();
        operation.EvidenceReference.Should().BeNull();
        operation.EvidenceUrl.Should().BeNull();
        operation.AuthorityReference.Should().BeNull();
        schedule.Status.Should().Be(TourScheduleStatus.EmergencyStopped, "withdrawal never reopens the departure");
        db.Refunds.Should().HaveCount(refundCount, "withdrawal never reverses refund state");
        db.AuditLogs.Should().Contain(audit =>
            audit.ActionType == AuditActionTypes.TourEmergencyEvidenceWithdrawn);
    }

    private static TourSchedule SeedSchedule(TestDbContext db, DateTimeOffset start)
    {
        SeedApprovedOperator(db, 2, "operator@example.test");
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, 1L);
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, 2L);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);
        var schedule = TourSchedule.Create(start, start.AddHours(8), "Da Nang", 20, Now);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, 5L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        return schedule;
    }

    private static void SeedApprovedOperator(TestDbContext db, long userId, string email)
    {
        var operatorUser = new User
        {
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            Email = email,
            FullName = "UC70 Operator",
            PasswordHash = PasswordHasher.Hash(ValidOperatorPassword),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(operatorUser, userId);
        var operatorProfile = new OperatorProfile
        {
            UserId = userId,
            User = operatorUser,
            CompanyName = "UC70 Tours",
            TaxCode = "UC70-TAX",
            BusinessLicenseNo = "UC70-LICENSE",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        db.Users.Add(operatorUser);
        db.OperatorProfiles.Add(operatorProfile);
    }

    private static void SeedTraveler(TestDbContext db, long userId)
    {
        var traveler = new User
        {
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            Email = $"traveler-{userId}@example.test",
            FullName = "UC70 Traveler",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(traveler, userId);
        db.Users.Add(traveler);
    }

    private static Booking SeedBooking(TestDbContext db, TourSchedule schedule, long id,
        long travelerId, BookingStatus status, PaymentStatus paymentStatus, decimal amount,
        int quantity = 1)
    {
        var booking = Booking.Restore(id, travelerId, schedule.Id, quantity, amount, status,
            paymentStatus, Booking.DefaultCancellationPolicySnapshot);
        typeof(Booking).GetProperty(nameof(Booking.TourSchedule))!.SetValue(booking, schedule);
        db.Bookings.Add(booking);
        return booking;
    }

    private static PaymentTransaction SeedSuccessfulPayment(TestDbContext db, Booking booking, decimal amount)
    {
        var payment = PaymentTransaction.Create(booking.Id, PaymentGateway.PayOS, amount,
            1234 + db.PaymentTransactions.Local.Count, Now);
        payment.MarkSuccess($"paid-{booking.Id}", null, Now, 0m);
        db.PaymentTransactions.Add(payment);
        return payment;
    }

    private static TripSession SeedCommercialSession(TestDbContext db, Tour tour, Booking booking)
    {
        var itinerary = Itinerary.CreateBookedTour(booking.TravelerUserId, "Booked tour", booking.Id, tour.Id, Now.AddDays(-1));
        var item = TripSessionItem.Snapshot(1, 1, 1, "Stop", 16m, 108m,
            Now.AddHours(-1), Now.AddHours(1), true);
        var session = TripSession.Start(1, 1, booking.TravelerUserId, Guid.NewGuid(),
            Now.AddHours(-1), Now.AddHours(2), [item]);
        typeof(TripSession).GetProperty(nameof(TripSession.Itinerary))!.SetValue(session, itinerary);
        db.Itineraries.Add(itinerary);
        db.TripSessions.Add(session);
        return session;
    }

    private static void SeedUnverifiedSession(TestDbContext db, Tour tour, long travelerUserId)
    {
        // Legacy identity: same tour, overlapping window, but no exact booking link.
        var itinerary = Itinerary.CreateManual(travelerUserId, "Legacy plan",
            Itinerary.ActiveStatus, Now.AddDays(-1));
        typeof(Itinerary).GetProperty(nameof(Itinerary.SourceType))!.SetValue(itinerary, Itinerary.BookedTourSourceType);
        typeof(Itinerary).GetProperty(nameof(Itinerary.SourceTourId))!.SetValue(itinerary, (long?)tour.Id);
        var item = TripSessionItem.Snapshot(2, 1, 1, "Stop", 16m, 108m,
            Now.AddHours(-1), Now.AddHours(1), true);
        var session = TripSession.Start(2, 2, travelerUserId, Guid.NewGuid(),
            Now.AddDays(4).AddHours(-1), Now.AddDays(4).AddHours(2), [item]);
        typeof(TripSession).GetProperty(nameof(TripSession.Itinerary))!.SetValue(session, itinerary);
        db.Itineraries.Add(itinerary);
        db.TripSessions.Add(session);
    }
}