using System.Reflection;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Tickets;

/// <summary>Seeding for the UC-29 endpoint tests on the in-memory test host.</summary>
internal sealed class TicketApiSeed(IApplicationDbContext db)
{
    public const long Alice = 11;
    public const long Bob = 12;
    public const long LockedTraveler = 21;
    public const long Operator = 900;

    /// <summary>Departs 08:00 on 30 Oct in Vietnam.</summary>
    public static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset InWindow = new(2026, 10, 30, 0, 30, 0, TimeSpan.Zero);

    private long lastId;

    public async Task AddPeopleAsync()
    {
        db.Users.AddRange(NewUser(Alice, UserRole.Traveler), NewUser(Bob, UserRole.Traveler),
            NewUser(LockedTraveler, UserRole.Traveler, AccountStatus.Locked),
            NewUser(Operator, UserRole.TourOperator));
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = Operator,
            CompanyName = "Hoi An Travel Co.",
            TaxCode = "TAX-900",
            BusinessLicenseNo = "LIC-900",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.Id), 45L);
        Set(tour, nameof(Tour.OperatorUserId), Operator);
        Set(tour, nameof(Tour.Title), "Hoi An Lantern Tour");
        Set(tour, nameof(Tour.BasePrice), 1_000_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        Set(schedule, nameof(TourSchedule.Id), 123L);
        Set(schedule, nameof(TourSchedule.TourId), 45L);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        Set(schedule, nameof(TourSchedule.StartAtUtc), Departure);
        Set(schedule, nameof(TourSchedule.EndAtUtc), Departure.AddHours(9));
        Set(schedule, nameof(TourSchedule.MeetingPoint), "Old Town gate");
        Set(schedule, nameof(TourSchedule.TotalCapacity), 20);
        Set(schedule, nameof(TourSchedule.Status), TourScheduleStatus.Scheduled);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    public async Task<long> AddBookingAsync(long travelerId, BookingStatus status, PaymentStatus paymentStatus)
    {
        var booking = Booking.Restore(++lastId + 400, travelerId, 123, quantity: 1, totalAmount: 1_000_000m,
            status, paymentStatus, Booking.DefaultCancellationPolicySnapshot);
        Set(booking, nameof(Booking.BookingCode), $"BK-20261017-{booking.Id:D5}");
        db.Bookings.Add(booking);
        var participant = BookingParticipant.Create("Alice", ParticipantType.Adult, null, null, null, null, null);
        Set(participant, nameof(BookingParticipant.BookingId), booking.Id);
        db.BookingParticipants.Add(participant);
        await db.SaveChangesAsync(CancellationToken.None);
        return booking.Id;
    }

    public async Task<long> AddTicketAsync(long bookingId, TicketStatus status = TicketStatus.Valid)
    {
        var ticket = (Ticket)Activator.CreateInstance(typeof(Ticket), nonPublic: true)!;
        ticket.Id = ++lastId + 70;
        Set(ticket, nameof(Ticket.BookingId), bookingId);
        Set(ticket, nameof(Ticket.TicketCode), Ticket.CodeFor(bookingId));
        Set(ticket, nameof(Ticket.Status), status);
        Set(ticket, nameof(Ticket.IssuedAtUtc), InWindow.AddDays(-5));
        if (status == TicketStatus.Used)
            Set(ticket, nameof(Ticket.CheckedInAtUtc), (DateTimeOffset?)InWindow.AddMinutes(-10));
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(CancellationToken.None);
        return ticket.Id;
    }

    private static User NewUser(long id, UserRole role, AccountStatus status = AccountStatus.Active) => new()
    {
        Id = id,
        Email = $"user-{id}@example.com",
        FullName = $"User {id}",
        Role = role,
        Status = status,
    };

    private static void Set<T>(object instance, string property, T value) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(instance, value);
}