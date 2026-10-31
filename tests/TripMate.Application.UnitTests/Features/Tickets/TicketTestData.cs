using System.Reflection;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Tickets.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Tickets;

/// <summary>Seeding helpers and fakes shared by the UC-29 handler tests.</summary>
internal sealed class TicketTestData
{
    public const long TravelerId = 10;
    public const long OtherTravelerId = 11;
    public const long OperatorId = 99;
    public const string OperatorName = "Hoi An Travel Co.";

    /// <summary>Departs 08:00 on 30 Oct in Vietnam; the window is 29 Oct 17:00Z to 30 Oct 16:59:59.9999999Z.</summary>
    public static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset BeforeWindow = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset InWindow = new(2026, 10, 30, 0, 30, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset AfterWindow = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private readonly string databaseName = Guid.NewGuid().ToString();
    private long lastId;
    private TourSchedule? schedule;

    public TicketTestData()
    {
        Db = NewContext();
    }

    public TestDbContext Db { get; }

    public FakeTicketQrCodec Codec { get; } = new();

    public FakeTicketQrClassifier Classifier { get; } = new();

    /// <summary>A second context over the same in-memory database, standing in for a concurrent request.</summary>
    public TestDbContext NewContext() =>
        new(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(databaseName).Options);

    public async Task<Booking> AddBookingAsync(BookingStatus status, PaymentStatus paymentStatus,
        long travelerId = TravelerId, int adults = 2, int children = 1, int infants = 0,
        string? meetingPoint = "Old Town gate")
    {
        var tourSchedule = await GetScheduleAsync(meetingPoint);
        var quantity = adults + children + infants;
        var booking = Booking.Restore(++lastId, travelerId, tourSchedule.Id, quantity, 1_000_000m * quantity,
            status, paymentStatus, Booking.DefaultCancellationPolicySnapshot);
        Set(booking, nameof(Booking.BookingCode), $"BK-20261017-{booking.Id:D5}");
        Db.Bookings.Add(booking);

        void AddParticipants(ParticipantType type, int count)
        {
            for (var index = 0; index < count; index++)
            {
                var participant = BookingParticipant.Create($"{type} {index + 1}", type,
                    null, null, null, null, null);
                Set(participant, nameof(BookingParticipant.BookingId), booking.Id);
                Db.BookingParticipants.Add(participant);
            }
        }

        AddParticipants(ParticipantType.Adult, adults);
        AddParticipants(ParticipantType.Child, children);
        AddParticipants(ParticipantType.Infant, infants);
        await Db.SaveChangesAsync();
        return booking;
    }

    public async Task<Ticket> AddTicketAsync(Booking booking, TicketStatus status = TicketStatus.Valid,
        DateTimeOffset? checkedInAtUtc = null)
    {
        var ticket = (Ticket)Activator.CreateInstance(typeof(Ticket), nonPublic: true)!;
        ticket.Id = ++lastId;
        Set(ticket, nameof(Ticket.BookingId), booking.Id);
        Set(ticket, nameof(Ticket.TicketCode), Ticket.CodeFor(booking.Id));
        Set(ticket, nameof(Ticket.Status), status);
        Set(ticket, nameof(Ticket.IssuedAtUtc), BeforeWindow.AddDays(-1));
        Set(ticket, nameof(Ticket.CheckedInAtUtc), checkedInAtUtc);
        Db.Tickets.Add(ticket);
        await Db.SaveChangesAsync();
        return ticket;
    }

    public async Task<TicketQrCode> AddQrAsync(long ticketId, string token, TicketQrCodeStatus status,
        DateTimeOffset issuedAtUtc)
    {
        var code = TicketQrCode.Issue(ticketId, token, $"sig-{token}", issuedAtUtc);
        if (status == TicketQrCodeStatus.Revoked)
            code.Revoke();
        else if (status != TicketQrCodeStatus.Active)
            Set(code, nameof(TicketQrCode.Status), status);
        Db.TicketQrCodes.Add(code);
        await Db.SaveChangesAsync();
        return code;
    }

    public async Task<List<TicketQrCode>> QrCodesAsync(long ticketId)
    {
        await using var context = NewContext();
        return await context.TicketQrCodes.Where(code => code.TicketId == ticketId)
            .OrderBy(code => code.Id).ToListAsync();
    }

    public static IDateTimeProvider ClockAt(DateTimeOffset now) => new FixedClock(now);

    private async Task<TourSchedule> GetScheduleAsync(string? meetingPoint)
    {
        if (schedule is not null)
            return schedule;

        Db.Users.AddRange(NewUser(TravelerId, UserRole.Traveler), NewUser(OtherTravelerId, UserRole.Traveler),
            NewUser(OperatorId, UserRole.TourOperator));
        Db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = OperatorId,
            CompanyName = OperatorName,
            TaxCode = "TAX-99",
            BusinessLicenseNo = "LIC-99",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.Id), 45L);
        Set(tour, nameof(Tour.OperatorUserId), OperatorId);
        Set(tour, nameof(Tour.Title), "Hoi An Lantern Tour");
        Set(tour, nameof(Tour.BasePrice), 1_000_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);

        schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        Set(schedule, nameof(TourSchedule.Id), 123L);
        Set(schedule, nameof(TourSchedule.TourId), 45L);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        Set(schedule, nameof(TourSchedule.StartAtUtc), Departure);
        Set(schedule, nameof(TourSchedule.EndAtUtc), Departure.AddHours(9));
        Set(schedule, nameof(TourSchedule.MeetingPoint), meetingPoint);
        Set(schedule, nameof(TourSchedule.TotalCapacity), 20);
        Set(schedule, nameof(TourSchedule.Status), TourScheduleStatus.Scheduled);

        Db.Tours.Add(tour);
        Db.TourSchedules.Add(schedule);
        await Db.SaveChangesAsync();
        return schedule;
    }

    private static User NewUser(long id, UserRole role) => new()
    {
        Id = id,
        Email = $"user-{id}@example.com",
        FullName = $"User {id}",
        Role = role,
        Status = AccountStatus.Active,
    };

    private static void Set<T>(object instance, string property, T value) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(instance, value);

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
}

/// <summary>Deterministic stand-in for the AES-GCM/HMAC codec; tokens are numbered per issue.</summary>
internal sealed class FakeTicketQrCodec : ITicketQrCodec
{
    private int issued;

    public TicketQrPayload Issue(long ticketId, DateTimeOffset issuedAtUtc)
    {
        var token = $"token-{ticketId}-{++issued}";
        var signature = $"sig-{token}";
        return new TicketQrPayload(token, signature, ContentFor(token, signature));
    }

    public string ContentFor(string qrToken, string signature) => $"TM1.{qrToken}.{signature}";

    public bool TryVerify(string content, out TicketQrVerification? verification)
    {
        verification = null;
        return false;
    }
}

internal sealed class FakeTicketQrClassifier : ITicketQrPersistenceErrorClassifier
{
    public bool IsOneActiveCodeConflict(DbUpdateException exception) =>
        exception.Message.Contains("UX_TicketQrCodes_OneActivePerTicket", StringComparison.Ordinal);
}