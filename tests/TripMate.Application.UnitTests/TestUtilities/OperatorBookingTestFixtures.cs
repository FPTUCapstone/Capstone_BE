using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.TestUtilities.OperatorRevenueTestFixtures;

namespace TripMate.Application.UnitTests.TestUtilities;

/// <summary>Seeding helpers for the UC-40 operator booking list and detail tests.</summary>
public static class OperatorBookingTestFixtures
{
    public static readonly DateTimeOffset DefaultStartUtc = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    public static async Task<TourSchedule> SeedTourWithScheduleAsync(
        TestDbContext db, long operatorId, long tourId, long scheduleId, string title = "Tour",
        DateTimeOffset? startUtc = null, string? meetingPoint = "Meeting point")
    {
        if (!db.Tours.Any(tour => tour.Id == tourId))
        {
            db.Tours.Add(CreateTour(tourId, operatorId, title));
        }

        DateTimeOffset start = startUtc ?? DefaultStartUtc;
        var schedule = TourSchedule.Create(start, start.AddHours(10), meetingPoint, 50,
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        Set(schedule, nameof(TourSchedule.Id), scheduleId);
        Set(schedule, nameof(TourSchedule.TourId), tourId);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();
        return schedule;
    }

    public static async Task<User> SeedBookerAsync(
        TestDbContext db, long id, string fullName, string? email = null, string? phoneNumber = null)
    {
        var user = new User
        {
            Id = id,
            Email = email ?? $"booker{id}@test.local",
            PhoneNumber = phoneNumber,
            FullName = fullName,
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task<Booking> SeedBookingAsync(
        TestDbContext db, long bookingId, long scheduleId, long bookerId,
        string? bookingCode = null,
        BookingStatus status = BookingStatus.Confirmed,
        PaymentStatus paymentStatus = PaymentStatus.Paid,
        int quantity = 1,
        decimal totalAmount = 500_000m,
        decimal discountAmount = 0m,
        DateTimeOffset? bookedAtUtc = null,
        long? voucherId = null,
        IReadOnlyCollection<BookingParticipant>? participants = null)
    {
        DateTimeOffset bookedAt = bookedAtUtc ?? new DateTimeOffset(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);
        participants ??= Enumerable.Range(1, quantity)
            .Select(index => BookingParticipant.Create($"Guest {bookingId}-{index}", ParticipantType.Adult,
                null, null, null, null, null))
            .ToList();
        var booking = Booking.Create(
            bookingCode ?? $"BK-T-{bookingId}", bookerId, scheduleId, voucherId,
            totalAmount + discountAmount, discountAmount, totalAmount, participants,
            bookedAt, bookedAt.AddMinutes(30));
        Set(booking, nameof(Booking.Id), bookingId);
        Set(booking, nameof(Booking.Status), status);
        Set(booking, nameof(Booking.PaymentStatus), paymentStatus);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    public static async Task<Ticket> SeedTicketAsync(
        TestDbContext db, long ticketId, long bookingId, TicketStatus status = TicketStatus.Valid,
        DateTimeOffset? checkedInAtUtc = null, DateTimeOffset? issuedAtUtc = null)
    {
        var ticket = (Ticket)Activator.CreateInstance(typeof(Ticket), nonPublic: true)!;
        Set(ticket, nameof(Ticket.Id), ticketId);
        Set(ticket, nameof(Ticket.BookingId), bookingId);
        Set(ticket, nameof(Ticket.TicketCode), $"TK-{ticketId:D10}");
        Set(ticket, nameof(Ticket.Status), status);
        Set(ticket, nameof(Ticket.IssuedAtUtc), issuedAtUtc ?? new DateTimeOffset(2026, 10, 20, 3, 1, 0, TimeSpan.Zero));
        Set(ticket, nameof(Ticket.CheckedInAtUtc), checkedInAtUtc);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        return ticket;
    }
}