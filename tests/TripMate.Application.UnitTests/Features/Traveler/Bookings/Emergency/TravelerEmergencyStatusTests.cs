using FluentAssertions;

using TripMate.Application.Features.Operator.Tours.Emergency;
using TripMate.Application.Features.Traveler.Bookings.Emergency;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.Bookings.Emergency;

public sealed class TravelerEmergencyStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FakePasswordHasher PasswordHasher = new();
    private const string ValidOperatorPassword = "OperatorPass123!";

    [Fact]
    public async Task Status_OwnerLoadsSensitiveInstructionsAndTickets()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db);
        var booking = SeedBooking(db, schedule);
        SeedTraveler(db, booking.TravelerUserId);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking);
        db.Tickets.AddRange(
            Ticket.Create(booking.Id, TicketStatus.Valid),
            Ticket.Create(booking.Id, TicketStatus.Used));
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService
        {
            UserId = 2,
            Role = nameof(UserRole.TourOperator),
        };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        await StopScheduleAsync(db, schedule, currentUser, clock);
        currentUser.UserId = booking.TravelerUserId;
        currentUser.Role = nameof(UserRole.Traveler);

        var result = await new GetTourBookingEmergencyStatusQueryHandler(db, currentUser)
            .Handle(new GetTourBookingEmergencyStatusQuery(booking.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ImmediateInstructions.Should().Be(
            "Stay with the guide at the verified meeting point.");
        result.Value.EmergencyContact.Should().Be("0900000000");
        result.Value.BookingStatus.Should().Be(nameof(BookingStatus.Interrupted));
        result.Value.TotalTicketCount.Should().Be(2);
        result.Value.CancelledTicketCount.Should().Be(1);
        result.Value.UsedTicketCount.Should().Be(1);
    }

    [Fact]
    public async Task Acknowledge_IsIdempotentAndDoesNotInferTravelerSafety()
    {
        await using var db = TestDbContext.Create();
        var schedule = SeedSchedule(db);
        var booking = SeedBooking(db, schedule);
        SeedTraveler(db, booking.TravelerUserId);
        schedule.ReserveCapacity(1);
        SeedSuccessfulPayment(db, booking);
        await db.SaveChangesAsync();
        var currentUser = new FakeCurrentUserService
        {
            UserId = 2,
            Role = nameof(UserRole.TourOperator),
        };
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        await StopScheduleAsync(db, schedule, currentUser, clock);
        currentUser.UserId = booking.TravelerUserId;
        currentUser.Role = nameof(UserRole.Traveler);
        var handler = new AcknowledgeTourBookingEmergencyStatusCommandHandler(
            db, currentUser, clock);

        var first = await handler.Handle(
            new AcknowledgeTourBookingEmergencyStatusCommand(booking.Id),
            CancellationToken.None);
        clock.UtcNow = Now.AddMinutes(10);
        var replay = await handler.Handle(
            new AcknowledgeTourBookingEmergencyStatusCommand(booking.Id),
            CancellationToken.None);
        var status = await new GetTourBookingEmergencyStatusQueryHandler(db, currentUser)
            .Handle(new GetTourBookingEmergencyStatusQuery(booking.Id), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        first.Value.AcknowledgedAtUtc.Should().Be(Now);
        replay.Value.AcknowledgedAtUtc.Should().Be(Now);
        status.Value.AcknowledgedAtUtc.Should().Be(Now);
    }

    private static async Task StopScheduleAsync(
        TestDbContext db,
        TourSchedule schedule,
        FakeCurrentUserService currentUser,
        FakeDateTimeProvider clock)
    {
        var preview = await new GetEmergencyTourImpactQueryHandler(db, currentUser, clock)
            .Handle(new GetEmergencyTourImpactQuery(1, schedule.Id), CancellationToken.None);
        await new EmergencyTourCommandHandler(db, currentUser, clock, PasswordHasher, new NoOpWeatherSafetyReviewNotifier())
            .Handle(new EmergencyStopActiveCommand(1, schedule.Id, Guid.NewGuid(),
                preview.Value.ImpactVersion, ValidOperatorPassword,
                EmergencyTourReason.SafetyIncident,
                "Road access is unsafe and the trip must stop.",
                "Stay with the guide at the verified meeting point.", "0900000000",
                "incident-123"), CancellationToken.None);
    }

    private static TourSchedule SeedSchedule(TestDbContext db)
    {
        SeedApprovedOperator(db);
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, 1L);
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, 2L);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);
        var schedule = TourSchedule.Create(
            Now.AddHours(-1), Now.AddHours(7), "Da Nang", 20, Now);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, 5L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, 1L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        return schedule;
    }

    private static void SeedApprovedOperator(TestDbContext db)
    {
        var user = new User
        {
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            Email = "operator@example.test",
            FullName = "UC70 Operator",
            PasswordHash = PasswordHasher.Hash(ValidOperatorPassword),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, 2L);
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = 2,
            User = user,
            CompanyName = "UC70 Tours",
            TaxCode = "UC70-TAX",
            BusinessLicenseNo = "UC70-LICENSE",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        });
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

    private static Booking SeedBooking(TestDbContext db, TourSchedule schedule)
    {
        var booking = Booking.Restore(10, 3, schedule.Id, 1, 100_000m,
            BookingStatus.Confirmed, PaymentStatus.Paid,
            Booking.DefaultCancellationPolicySnapshot);
        typeof(Booking).GetProperty(nameof(Booking.TourSchedule))!.SetValue(booking, schedule);
        db.Bookings.Add(booking);
        return booking;
    }

    private static void SeedSuccessfulPayment(TestDbContext db, Booking booking)
    {
        var payment = PaymentTransaction.Create(
            booking.Id, PaymentGateway.PayOS, 100_000m, 1234, Now);
        payment.MarkSuccess($"paid-{booking.Id}", null, Now, 0m);
        db.PaymentTransactions.Add(payment);
    }
}
