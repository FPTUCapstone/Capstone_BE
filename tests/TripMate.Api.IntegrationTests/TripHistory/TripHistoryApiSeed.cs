using System.Reflection;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TripHistory;

/// <summary>Compact seeding for the Trip History endpoint tests (entities have private setters).</summary>
internal sealed class TripHistoryApiSeed(IApplicationDbContext db)
{
    public static readonly DateTimeOffset Created = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private long _tourId;
    private PointOfInterest? _poi;

    public async Task AddUserAsync(long id, UserRole role = UserRole.Traveler, AccountStatus status = AccountStatus.Active)
    {
        db.Users.Add(new User
        {
            Id = id,
            Email = $"user-{id}@example.com",
            FullName = $"User {id}",
            Role = role,
            Status = status,
            CreatedAtUtc = Created,
            UpdatedAtUtc = Created,
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    public async Task<TourSchedule> AddScheduleAsync(DateTimeOffset startAtUtc, string title = "Hoi An Lantern Tour")
    {
        var id = ++_tourId;
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.Id), id);
        Set(tour, nameof(Tour.OperatorUserId), 900L);
        Set(tour, nameof(Tour.Title), title);
        Set(tour, nameof(Tour.DestinationSummary), "Hoi An");
        Set(tour, nameof(Tour.BasePrice), 100m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Approved);
        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        Set(schedule, nameof(TourSchedule.Id), id);
        Set(schedule, nameof(TourSchedule.TourId), id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        Set(schedule, nameof(TourSchedule.StartAtUtc), startAtUtc);
        Set(schedule, nameof(TourSchedule.EndAtUtc), startAtUtc.AddHours(8));
        Set(schedule, nameof(TourSchedule.MeetingPoint), "Old Town gate");
        Set(schedule, nameof(TourSchedule.TotalCapacity), 10);
        Set(schedule, nameof(TourSchedule.Status), TourScheduleStatus.Scheduled);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync(CancellationToken.None);
        return schedule;
    }

    public async Task<Booking> AddBookingAsync(long travelerId, TourSchedule schedule, BookingStatus status, int quantity = 2)
    {
        var booking = Booking.Restore(
            0 + NextBookingId(),
            travelerId,
            schedule.Id,
            quantity,
            3_200_000m,
            status,
            status is BookingStatus.PendingPayment or BookingStatus.Expired ? PaymentStatus.Unpaid : PaymentStatus.Paid,
            Booking.DefaultCancellationPolicySnapshot);
        Set(booking, nameof(Booking.BookingCode), $"TM-{booking.Id:D6}");
        db.Bookings.Add(booking);
        await db.SaveChangesAsync(CancellationToken.None);
        return booking;
    }

    public async Task MarkCancelledAsync(Booking booking, DateTimeOffset at, string? reason)
    {
        Set(booking, nameof(Booking.CancelledAtUtc), (DateTimeOffset?)at);
        Set(booking, nameof(Booking.CancelReason), reason);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    public async Task AddTicketAsync(long bookingId, TicketStatus status, long id)
    {
        var ticket = (Ticket)Activator.CreateInstance(typeof(Ticket), nonPublic: true)!;
        ticket.Id = id;
        Set(ticket, nameof(Ticket.BookingId), bookingId);
        Set(ticket, nameof(Ticket.Status), status);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    public async Task AddPaymentAndRefundAsync(long bookingId, long id, PaymentGateway gateway, decimal amount)
    {
        var payment = (PaymentTransaction)Activator.CreateInstance(typeof(PaymentTransaction), nonPublic: true)!;
        payment.Id = id;
        Set(payment, nameof(PaymentTransaction.BookingId), bookingId);
        Set(payment, nameof(PaymentTransaction.Gateway), gateway);
        Set(payment, nameof(PaymentTransaction.Amount), amount);
        Set(payment, nameof(PaymentTransaction.TransactionType), PaymentTransaction.TypePayment);
        Set(payment, nameof(PaymentTransaction.Status), PaymentTransactionStatus.Success);
        Set(payment, nameof(PaymentTransaction.CreatedAtUtc), Created);
        db.PaymentTransactions.Add(payment);
        var refund = (Refund)Activator.CreateInstance(typeof(Refund), nonPublic: true)!;
        refund.Id = id;
        Set(refund, nameof(Refund.BookingId), bookingId);
        Set(refund, nameof(Refund.TransactionId), (long?)id);
        Set(refund, nameof(Refund.Amount), amount);
        Set(refund, nameof(Refund.Status), RefundStatus.Processed);
        Set(refund, nameof(Refund.RequestedAtUtc), Created);
        db.Refunds.Add(refund);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    public async Task<Itinerary> AddItineraryAsync(
        long travelerId,
        string status = Itinerary.ActiveStatus,
        DateTimeOffset? validFromUtc = null,
        int visitCount = 2)
    {
        var poi = await GetPoiAsync();
        var itinerary = Itinerary.CreateManual(travelerId, "Da Nang 2 days", status, Created);
        Set(itinerary, nameof(Itinerary.ValidFromUtc), validFromUtc);
        for (var sequence = 1; sequence <= visitCount; sequence++)
        {
            itinerary.AddItem(ItineraryItem.CreateVisit(
                sequence,
                poi.Id,
                Created.AddHours(sequence),
                Created.AddHours(sequence + 1),
                isMandatory: sequence == 1,
                0m,
                "Planned"));
        }

        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync(CancellationToken.None);
        return itinerary;
    }

    public async Task<TripSession> AddSessionAsync(
        Itinerary itinerary,
        long travelerId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? endedAtUtc,
        int reachedItems,
        TimeSpan lifetime)
    {
        var poi = await GetPoiAsync();
        var items = itinerary.Items.OrderBy(item => item.SequenceNo).ToArray();
        var session = TripSession.Start(
            itinerary.Id,
            itinerary.Id,
            travelerId,
            Guid.NewGuid(),
            startedAtUtc,
            startedAtUtc + lifetime,
            items.Select(item => TripSessionItem.Snapshot(
                item.Id,
                item.SequenceNo,
                poi.Id,
                poi.Name,
                poi.Latitude,
                poi.Longitude,
                item.PlannedArrivalUtc!.Value,
                item.PlannedDepartureUtc!.Value,
                item.IsMandatory)));
        foreach (var item in items.Take(reachedItems))
        {
            session.ReachItem(item.Id, startedAtUtc.AddMinutes(item.SequenceNo));
        }

        if (endedAtUtc is { } ended)
        {
            session.Finish(ended);
        }

        db.TripSessions.Add(session);
        await db.SaveChangesAsync(CancellationToken.None);
        return session;
    }

    private long _lastBookingId;

    private long NextBookingId() => ++_lastBookingId;

    private async Task<PointOfInterest> GetPoiAsync()
    {
        if (_poi is not null)
        {
            return _poi;
        }

        var category = PoiCategory.Create("Trip history", null);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync(CancellationToken.None);
        _poi = PointOfInterest.Create(category, "Dragon Bridge", 16m, 108m, 1, Created);
        db.PointsOfInterest.Add(_poi);
        await db.SaveChangesAsync(CancellationToken.None);
        return _poi;
    }

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType()
            .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}