using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Bookings.Common;
using TripMate.Application.Features.Operator.CheckIn.Common;
using TripMate.Application.Features.Operator.CheckIn.Scan;
using TripMate.Application.Features.Tickets.Common;
using TripMate.Application.UnitTests.Features.Tickets;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Security;

namespace TripMate.Application.UnitTests.Features.Operator.CheckIn;

public sealed class CheckInTravelerCommandHandlerTests
{
    private const long OperatorUserId = 100;
    private const long OtherOperatorUserId = 999;
    private const long TravelerUserId = 200;

    // Departure 08:00 on 2026-10-30 in Vietnam (UTC+7) -> 2026-10-30 01:00 UTC
    private static readonly DateTimeOffset DepartureStartUtc = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset InWindowUtc = new(2026, 10, 30, 0, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeWindowUtc = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDbContext dbContext;
    private readonly FakeCurrentUserService currentUserService;
    private readonly ITicketQrCodec ticketQrCodec;
    private readonly TestClock clock;
    private readonly CheckInTravelerCommandHandler handler;

    public CheckInTravelerCommandHandlerTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;
        dbContext = new TestDbContext(options);

        currentUserService = new FakeCurrentUserService
        {
            UserId = OperatorUserId,
            Role = nameof(UserRole.TourOperator),
        };

        ticketQrCodec = new AesGcmHmacTicketQrCodec(Options.Create(new TicketQrOptions
        {
            EncryptionKey = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=",
            SigningKey = "ISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0A=",
        }));
        clock = new TestClock { UtcNow = InWindowUtc };

        handler = new CheckInTravelerCommandHandler(
            dbContext,
            currentUserService,
            ticketQrCodec,
            clock);
    }

    [Fact]
    public async Task Handle_WhenOperatorAccessDenied_ReturnsForbidden()
    {
        currentUserService.Role = nameof(UserRole.Traveler);

        var result = await handler.Handle(
            new CheckInTravelerCommand(1, QrContent: "TM1.any.signature"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TripMate.Application.Features.Operator.Tours.Common.OperatorTourErrorCodes.AccessRequired);
    }

    [Fact]
    public async Task Handle_WhenScheduleNotFound_ReturnsNotFound()
    {
        await SeedOperatorProfileAsync(OperatorUserId);

        var result = await handler.Handle(
            new CheckInTravelerCommand(99999, QrContent: "TM1.any.signature"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(CheckInErrorCodes.ScheduleNotFound);
    }

    [Fact]
    public async Task Handle_WhenScheduleBelongsToAnotherOperator_ReturnsScheduleForbiddenAndLogsAudit()
    {
        await SeedOperatorProfileAsync(OperatorUserId);
        var schedule = await SeedTourScheduleAsync(OtherOperatorUserId, DepartureStartUtc);

        var result = await handler.Handle(
            new CheckInTravelerCommand(schedule.Id, QrContent: "TM1.any.signature"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(CheckInErrorCodes.ScheduleForbidden);

        var audit = await dbContext.AuditLogs.FirstOrDefaultAsync(a => a.ActionType == "CheckInScanRejected");
        audit.Should().NotBeNull();
        audit!.ActorUserId.Should().Be(OperatorUserId);
        audit.Reason.Should().Be("Operator does not own this tour departure schedule.");
    }

    [Fact]
    public async Task Handle_WhenAuditPersistenceFails_LogsStructuredDiagnosticAndReturnsFailureGracefully()
    {
        await SeedOperatorProfileAsync(OperatorUserId);
        var schedule = await SeedTourScheduleAsync(OtherOperatorUserId, DepartureStartUtc);

        var testLogger = new TestLogger<CheckInTravelerCommandHandler>();
        var testHandler = new CheckInTravelerCommandHandler(
            dbContext,
            currentUserService,
            ticketQrCodec,
            clock,
            testLogger);

        // Simulate database failure during audit save
        dbContext.SaveChangesFailureFactory = () => new InvalidOperationException("Simulated database outage");

        var result = await testHandler.Handle(
            new CheckInTravelerCommand(schedule.Id, QrContent: "TM1.any.signature"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(CheckInErrorCodes.ScheduleForbidden);

        testLogger.Logs.Should().ContainSingle(l =>
            l.Level == LogLevel.Error &&
            l.Message.Contains("Failed to persist rejection audit log") &&
            l.Message.Contains(OperatorUserId.ToString()));
    }

    [Fact]
    public async Task Handle_WhenOutsideDepartureDateWindow_ReturnsDepartureNotTodayAndLogsAudit()
    {
        await SeedOperatorProfileAsync(OperatorUserId);
        var schedule = await SeedTourScheduleAsync(OperatorUserId, DepartureStartUtc);
        clock.UtcNow = BeforeWindowUtc; // Not departure day

        var result = await handler.Handle(
            new CheckInTravelerCommand(schedule.Id, QrContent: "TM1.any.signature"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(CheckInErrorCodes.DepartureNotToday);

        var audit = await dbContext.AuditLogs.FirstOrDefaultAsync(a => a.ActionType == "CheckInScanRejected");
        audit.Should().NotBeNull();
        audit!.ActorUserId.Should().Be(OperatorUserId);
    }

    [Fact]
    public async Task Handle_WhenQrSignatureInvalid_ReturnsInvalidQrAndLogsAudit()
    {
        await SeedOperatorProfileAsync(OperatorUserId);
        var schedule = await SeedTourScheduleAsync(OperatorUserId, DepartureStartUtc);

        var result = await handler.Handle(
            new CheckInTravelerCommand(schedule.Id, QrContent: "invalid.forged.content"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(CheckInErrorCodes.InvalidQr);

        var audit = await dbContext.AuditLogs.FirstOrDefaultAsync(a => a.ActionType == "CheckInScanRejected");
        audit.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_WhenTicketBelongsToDifferentSchedule_ReturnsWrongSchedule()
    {
        await SeedOperatorProfileAsync(OperatorUserId);
        var targetSchedule = await SeedTourScheduleAsync(OperatorUserId, DepartureStartUtc);
        var otherSchedule = await SeedTourScheduleAsync(OperatorUserId, DepartureStartUtc);

        var (booking, ticket, qr) = await SeedBookingAndTicketAsync(otherSchedule.Id, BookingStatus.Confirmed, PaymentStatus.Paid);
        var qrContent = ticketQrCodec.ContentFor(qr.QrToken, qr.Signature);

        var result = await handler.Handle(
            new CheckInTravelerCommand(targetSchedule.Id, QrContent: qrContent),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(CheckInErrorCodes.WrongSchedule);
    }

    [Fact]
    public async Task Handle_WhenBookingCancelled_ReturnsBookingCancelled()
    {
        await SeedOperatorProfileAsync(OperatorUserId);
        var schedule = await SeedTourScheduleAsync(OperatorUserId, DepartureStartUtc);
        var (booking, ticket, qr) = await SeedBookingAndTicketAsync(schedule.Id, BookingStatus.Cancelled, PaymentStatus.Paid);
        var qrContent = ticketQrCodec.ContentFor(qr.QrToken, qr.Signature);

        var result = await handler.Handle(
            new CheckInTravelerCommand(schedule.Id, QrContent: qrContent),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(CheckInErrorCodes.BookingCancelled);
    }

    [Fact]
    public async Task Handle_WhenBookingNotPaid_ReturnsBookingNotConfirmed()
    {
        await SeedOperatorProfileAsync(OperatorUserId);
        var schedule = await SeedTourScheduleAsync(OperatorUserId, DepartureStartUtc);
        var (booking, ticket, qr) = await SeedBookingAndTicketAsync(schedule.Id, BookingStatus.Confirmed, PaymentStatus.Unpaid);
        var qrContent = ticketQrCodec.ContentFor(qr.QrToken, qr.Signature);

        var result = await handler.Handle(
            new CheckInTravelerCommand(schedule.Id, QrContent: qrContent),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(CheckInErrorCodes.BookingNotConfirmed);
    }

    [Fact]
    public async Task Handle_WhenValidQrScan_SuccessfullyChecksIn_AndReturnsDynamicProgress()
    {
        await SeedOperatorProfileAsync(OperatorUserId);
        var schedule = await SeedTourScheduleAsync(OperatorUserId, DepartureStartUtc);
        var (booking, ticket, qr) = await SeedBookingAndTicketAsync(schedule.Id, BookingStatus.Confirmed, PaymentStatus.Paid, quantity: 2);
        var qrContent = ticketQrCodec.ContentFor(qr.QrToken, qr.Signature);

        var result = await handler.Handle(
            new CheckInTravelerCommand(schedule.Id, QrContent: qrContent),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var data = result.Value;
        data.TicketCode.Should().Be(ticket.TicketCode);
        data.BookingCode.Should().Be(booking.BookingCode);
        data.PassengerName.Should().Be("Test Traveler");
        data.ParticipantCount.Should().Be(2);
        data.CheckedInAtUtc.Should().Be(InWindowUtc);

        data.Departure.CheckedInParticipants.Should().Be(2);
        data.Departure.TotalEligibleParticipants.Should().Be(2);
        data.Departure.WaitingParticipants.Should().Be(0);

        // Verify ticket marked Used
        var updatedTicket = await dbContext.Tickets.FindAsync(ticket.Id);
        updatedTicket!.Status.Should().Be(TicketStatus.Used);
        updatedTicket.CheckedInAtUtc.Should().Be(InWindowUtc);

        // Verify QR consumed
        var updatedQr = await dbContext.TicketQrCodes.FindAsync(qr.Id);
        updatedQr!.Status.Should().Be(TicketQrCodeStatus.Consumed);
        updatedQr.ConsumedAtUtc.Should().Be(InWindowUtc);

        // Verify CheckInLog inserted
        var checkInLog = await dbContext.CheckInLogs.FirstOrDefaultAsync(l => l.TicketId == ticket.Id);
        checkInLog.Should().NotBeNull();
        checkInLog!.ScanResult.Should().Be(CheckInScanResult.Success);
        checkInLog.CheckedInBy.Should().Be(OperatorUserId);
    }

    [Fact]
    public async Task Handle_WhenRepeatScan_StrictSingleUse_ReturnsAlreadyUsedWithPreviousTimestamp()
    {
        await SeedOperatorProfileAsync(OperatorUserId);
        var schedule = await SeedTourScheduleAsync(OperatorUserId, DepartureStartUtc);
        var (booking, ticket, qr) = await SeedBookingAndTicketAsync(schedule.Id, BookingStatus.Confirmed, PaymentStatus.Paid, quantity: 3);

        // First scan succeeds
        var firstResult = await handler.Handle(
            new CheckInTravelerCommand(schedule.Id, QrContent: ticketQrCodec.ContentFor(qr.QrToken, qr.Signature)),
            CancellationToken.None);
        firstResult.IsSuccess.Should().BeTrue();

        // Advance clock by 5 minutes for second scan
        clock.UtcNow = InWindowUtc.AddMinutes(5);

        // Repeat scan
        var repeatResult = await handler.Handle(
            new CheckInTravelerCommand(schedule.Id, QrContent: ticketQrCodec.ContentFor(qr.QrToken, qr.Signature)),
            CancellationToken.None);

        repeatResult.IsFailure.Should().BeTrue();
        repeatResult.ErrorCode.Should().Be(CheckInErrorCodes.AlreadyUsed);
        repeatResult.ErrorMetadata.Should().ContainKey("checkedInAt");
        repeatResult.ErrorMetadata["checkedInAt"].Should().Be(InWindowUtc);
        repeatResult.ErrorMetadata["ticketCode"].Should().Be(ticket.TicketCode);

        // Check that AlreadyUsed was logged to commerce.CheckInLogs
        var alreadyUsedLog = await dbContext.CheckInLogs
            .FirstOrDefaultAsync(l => l.TicketId == ticket.Id && l.ScanResult == CheckInScanResult.AlreadyUsed);
        alreadyUsedLog.Should().NotBeNull();
        alreadyUsedLog!.CheckedInAtUtc.Should().Be(InWindowUtc.AddMinutes(5));
    }

    [Fact]
    public async Task Handle_WhenManualBookingCode_SuccessfullyChecksIn()
    {
        await SeedOperatorProfileAsync(OperatorUserId);
        var schedule = await SeedTourScheduleAsync(OperatorUserId, DepartureStartUtc);
        var (booking, ticket, qr) = await SeedBookingAndTicketAsync(schedule.Id, BookingStatus.Confirmed, PaymentStatus.Paid, quantity: 1);

        var result = await handler.Handle(
            new CheckInTravelerCommand(schedule.Id, BookingCode: booking.BookingCode),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TicketCode.Should().Be(ticket.TicketCode);
        result.Value.BookingCode.Should().Be(booking.BookingCode);

        var updatedTicket = await dbContext.Tickets.FindAsync(ticket.Id);
        updatedTicket!.Status.Should().Be(TicketStatus.Used);
    }

    private async Task SeedOperatorProfileAsync(long userId)
    {
        var user = (User)Activator.CreateInstance(typeof(User), nonPublic: true)!;
        Set(user, nameof(User.Id), userId);
        Set(user, nameof(User.FullName), "Tour Operator Host");
        Set(user, nameof(User.Role), UserRole.TourOperator);
        Set(user, nameof(User.Status), AccountStatus.Active);
        dbContext.Users.Add(user);

        var profile = new OperatorProfile
        {
            UserId = userId,
            CompanyName = "Company",
            TaxCode = "TAX-123",
            BusinessLicenseNo = "LIC-123",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        };
        dbContext.OperatorProfiles.Add(profile);
        await dbContext.SaveChangesAsync();
    }

    private async Task<TourSchedule> SeedTourScheduleAsync(long operatorUserId, DateTimeOffset startAtUtc)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        var tourId = new Random().Next(1000, 9999);
        Set(tour, nameof(Tour.Id), (long)tourId);
        Set(tour, nameof(Tour.OperatorUserId), operatorUserId);
        Set(tour, nameof(Tour.Title), "Sample Ba Na Hills Tour");
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        dbContext.Tours.Add(tour);
        await dbContext.SaveChangesAsync();

        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        var scheduleId = new Random().Next(10000, 99999);
        Set(schedule, nameof(TourSchedule.Id), (long)scheduleId);
        Set(schedule, nameof(TourSchedule.TourId), (long)tourId);
        Set(schedule, nameof(TourSchedule.StartAtUtc), startAtUtc);
        Set(schedule, nameof(TourSchedule.EndAtUtc), startAtUtc.AddHours(8));
        Set(schedule, nameof(TourSchedule.MeetingPoint), "Meeting Point Lobby");
        dbContext.TourSchedules.Add(schedule);
        await dbContext.SaveChangesAsync();

        return schedule;
    }

    private async Task<(Booking booking, Ticket ticket, TicketQrCode qr)> SeedBookingAndTicketAsync(
        long scheduleId,
        BookingStatus status,
        PaymentStatus paymentStatus,
        int quantity = 2)
    {
        var user = await dbContext.Users.FindAsync(TravelerUserId);
        if (user is null)
        {
            user = (User)Activator.CreateInstance(typeof(User), nonPublic: true)!;
            Set(user, nameof(User.Id), TravelerUserId);
            Set(user, nameof(User.FullName), "Test Traveler");
            Set(user, nameof(User.Role), UserRole.Traveler);
            Set(user, nameof(User.Status), AccountStatus.Active);
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync();
        }

        var booking = Booking.Restore(
            new Random().Next(1000, 99999),
            TravelerUserId,
            scheduleId,
            quantity,
            500_000m * quantity,
            status,
            paymentStatus,
            Booking.DefaultCancellationPolicySnapshot);
        Set(booking, nameof(Booking.BookingCode), $"BK-{booking.Id:D6}");
        dbContext.Bookings.Add(booking);

        var ticket = (Ticket)Activator.CreateInstance(typeof(Ticket), nonPublic: true)!;
        Set(ticket, nameof(Ticket.BookingId), booking.Id);
        Set(ticket, nameof(Ticket.TicketCode), Ticket.CodeFor(booking.Id));
        Set(ticket, nameof(Ticket.Status), TicketStatus.Valid);
        Set(ticket, nameof(Ticket.IssuedAtUtc), InWindowUtc.AddHours(-1));
        dbContext.Tickets.Add(ticket);
        await dbContext.SaveChangesAsync();

        var payload = ticketQrCodec.Issue(ticket.Id, ticket.IssuedAtUtc);
        var qr = TicketQrCode.Issue(ticket.Id, payload.QrToken, payload.Signature, ticket.IssuedAtUtc);
        dbContext.TicketQrCodes.Add(qr);
        await dbContext.SaveChangesAsync();

        return (booking, ticket, qr);
    }

    private static void Set(object target, string propertyName, object? value) =>
        target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private sealed class FakeCurrentUserService : ICurrentUserService
    {
        public long? UserId { get; set; }
        public string? Email { get; set; }
        public string? Role { get; set; }
    }

    private sealed class TestClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception), exception));
        }
    }
}