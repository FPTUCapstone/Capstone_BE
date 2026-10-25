using System.Reflection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Tickets;

/// <summary>
/// Seeds one traveler, one approved operator, one published tour and one schedule into a SQL Server
/// test database, then creates bookings in any stored state for the UC-29 ticket tests.
/// </summary>
internal sealed class TicketSqlServerSeed
{
    private TicketSqlServerSeed(SqlServerTestDatabase database, long travelerId, long otherTravelerId,
        long operatorId, long scheduleId)
    {
        Database = database;
        TravelerId = travelerId;
        OtherTravelerId = otherTravelerId;
        OperatorId = operatorId;
        ScheduleId = scheduleId;
    }

    public SqlServerTestDatabase Database { get; }

    public long TravelerId { get; }

    public long OtherTravelerId { get; }

    public long OperatorId { get; }

    public long ScheduleId { get; }

    public static async Task<TicketSqlServerSeed> CreateAsync(SqlServerTestDatabase database,
        DateTimeOffset departureAtUtc)
    {
        await using var context = database.CreateDbContext();

        var traveler = NewUser("traveler", UserRole.Traveler);
        var otherTraveler = NewUser("other-traveler", UserRole.Traveler);
        var operatorUser = NewUser("operator", UserRole.TourOperator);
        context.Users.AddRange(traveler, otherTraveler, operatorUser);
        await context.SaveChangesAsync();

        context.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = operatorUser.Id,
            CompanyName = "Hoi An Travel Co.",
            TaxCode = $"TAX-{operatorUser.Id}",
            BusinessLicenseNo = $"LIC-{operatorUser.Id}",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        await context.SaveChangesAsync();

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorUser.Id);
        Set(tour, nameof(Tour.Title), "Hoi An Lantern Tour");
        Set(tour, nameof(Tour.BasePrice), 1_000_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        context.Tours.Add(tour);
        await context.SaveChangesAsync();

        var schedule = TourSchedule.Create(departureAtUtc, departureAtUtc.AddHours(9), "Old Town gate", 20,
            departureAtUtc.AddDays(-30), adultPrice: 1_000_000m);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        context.TourSchedules.Add(schedule);
        await context.SaveChangesAsync();

        return new TicketSqlServerSeed(database, traveler.Id, otherTraveler.Id, operatorUser.Id, schedule.Id);
    }

    /// <summary>Creates a booking for <see cref="TravelerId"/> and forces its stored state.</summary>
    public async Task<long> AddBookingAsync(BookingStatus status, PaymentStatus paymentStatus,
        params ParticipantType[] participantTypes)
    {
        await using var context = Database.CreateDbContext();
        var types = participantTypes.Length == 0 ? [ParticipantType.Adult] : participantTypes;
        var participants = types
            .Select((type, index) => BookingParticipant.Create($"Participant {index + 1}", type,
                null, null, null, null, null))
            .ToList();
        var bookedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var booking = Booking.Create($"BK-{Guid.NewGuid():N}"[..20], TravelerId, ScheduleId, voucherId: null,
            unitPrice: 1_000_000m, discountAmount: 0m, totalAmount: 1_000_000m * participants.Count,
            participants, bookedAt, bookedAt.AddMinutes(15));
        context.Bookings.Add(booking);
        await context.SaveChangesAsync();

        // Reserve the seats as UC-27 would, so a later cancellation can release them.
        await Database.ExecuteNonQueryAsync($"""
            UPDATE commerce.Bookings
            SET status = '{status}', payment_status = '{paymentStatus}'
            WHERE booking_id = {booking.Id};
            UPDATE commerce.TourSchedules
            SET reserved_capacity = reserved_capacity + {participants.Count}
            WHERE schedule_id = {ScheduleId};
            """);
        return booking.Id;
    }

    /// <summary>Inserts a ticket row directly, as legacy data or UC-43 would leave it.</summary>
    public async Task<long> AddTicketAsync(long bookingId, TicketStatus status = TicketStatus.Valid,
        DateTimeOffset? checkedInAtUtc = null)
    {
        var checkedIn = checkedInAtUtc is null
            ? "NULL"
            : $"'{checkedInAtUtc.Value.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}'";
        return await Database.ExecuteScalarAsync<long>($"""
            INSERT INTO commerce.Tickets (booking_id, ticket_code, status, checked_in_at)
            OUTPUT INSERTED.ticket_id
            VALUES ({bookingId}, '{Ticket.CodeFor(bookingId)}', '{status}', {checkedIn});
            """);
    }

    private static User NewUser(string prefix, UserRole role) => new()
    {
        Email = $"uc29-{prefix}-{Guid.NewGuid():N}@test.local",
        FullName = $"UC-29 {prefix}",
        Role = role,
        Status = AccountStatus.Active,
    };

    private static void Set<T>(object instance, string property, T value) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(instance, value);
}