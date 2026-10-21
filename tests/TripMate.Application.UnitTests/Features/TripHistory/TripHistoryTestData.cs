using Microsoft.EntityFrameworkCore;

using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripHistory;

/// <summary>Seeding helpers shared by the Trip History query tests.</summary>
internal sealed class TripHistoryTestData(TestDbContext db)
{
    public static readonly DateTimeOffset BaseTime = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private PointOfInterest? _poi;
    private long _lastTourId;
    private long _lastBookingId;

    public TestDbContext Db { get; } = db;

    public static TestDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public async Task AddTravelerAsync(long id)
    {
        Db.Users.Add(new User
        {
            Id = id,
            Email = $"traveler-{id}@example.com",
            FullName = $"Traveler {id}",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = BaseTime,
            UpdatedAtUtc = BaseTime,
        });
        await Db.SaveChangesAsync();
    }

    public async Task<Itinerary> AddItineraryAsync(
        long travelerId,
        string status = Itinerary.ActiveStatus,
        DateTimeOffset? createdAtUtc = null,
        int visitCount = 2,
        string sourceType = Itinerary.ManualSourceType,
        long? schedulingRequestId = null,
        int version = 1,
        DateTimeOffset? validFromUtc = null,
        DateTimeOffset? validToUtc = null)
    {
        var poi = await GetPoiAsync();
        var created = createdAtUtc ?? BaseTime;
        var itinerary = Itinerary.CreateManual(travelerId, "Trip", status, created);
        Set(itinerary, nameof(Itinerary.SourceType), sourceType);
        Set(itinerary, nameof(Itinerary.SchedulingRequestId), schedulingRequestId);
        Set(itinerary, nameof(Itinerary.Version), version);
        Set(itinerary, nameof(Itinerary.ValidFromUtc), validFromUtc);
        Set(itinerary, nameof(Itinerary.ValidToUtc), validToUtc);
        for (var sequence = 1; sequence <= visitCount; sequence++)
        {
            itinerary.AddItem(ItineraryItem.CreateVisit(
                sequence,
                poi.Id,
                created.AddHours(sequence),
                created.AddHours(sequence + 1),
                isMandatory: sequence == 1,
                0m,
                "Planned"));
        }

        Db.Itineraries.Add(itinerary);
        await Db.SaveChangesAsync();
        return itinerary;
    }

    /// <summary>
    /// Adds a session over the itinerary's visit items. With <paramref name="endedAtUtc"/> the session is
    /// finished (Completed, EndedAtUtc set); without it the session stays open.
    /// </summary>
    public async Task<TripSession> AddSessionAsync(
        Itinerary itinerary,
        long travelerId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? endedAtUtc = null,
        int reachedItems = 0,
        TimeSpan? lifetime = null,
        int skippedItems = 0)
    {
        var poi = await GetPoiAsync();
        var items = itinerary.Items.OrderBy(item => item.SequenceNo).ToArray();
        var session = TripSession.Start(
            itinerary.Id,
            itinerary.Id,
            travelerId,
            Guid.NewGuid(),
            startedAtUtc,
            startedAtUtc + (lifetime ?? TimeSpan.FromDays(30)),
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

        foreach (var item in items.Skip(reachedItems).Take(skippedItems))
        {
            session.SkipItem(item.Id, startedAtUtc.AddMinutes(30 + item.SequenceNo));
        }

        if (endedAtUtc is { } ended)
        {
            session.Finish(ended);
        }

        Db.TripSessions.Add(session);
        await Db.SaveChangesAsync();
        return session;
    }

    public async Task<TourSchedule> AddScheduleAsync(
        DateTimeOffset startAtUtc,
        string tourTitle = "Hoi An Lantern Tour",
        string destinationSummary = "Hoi An")
    {
        var tourId = ++_lastTourId;
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.Id), tourId);
        Set(tour, nameof(Tour.OperatorUserId), 99L);
        Set(tour, nameof(Tour.Title), tourTitle);
        Set(tour, nameof(Tour.DestinationSummary), destinationSummary);
        Set(tour, nameof(Tour.BasePrice), 100m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Approved);

        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        Set(schedule, nameof(TourSchedule.Id), tourId);
        Set(schedule, nameof(TourSchedule.TourId), tourId);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        Set(schedule, nameof(TourSchedule.StartAtUtc), startAtUtc);
        Set(schedule, nameof(TourSchedule.EndAtUtc), startAtUtc.AddHours(8));
        Set(schedule, nameof(TourSchedule.MeetingPoint), "Old Town gate");
        Set(schedule, nameof(TourSchedule.TotalCapacity), 10);
        Set(schedule, nameof(TourSchedule.Status), TourScheduleStatus.Scheduled);

        Db.Tours.Add(tour);
        Db.TourSchedules.Add(schedule);
        await Db.SaveChangesAsync();
        return schedule;
    }

    public async Task<Booking> AddBookingAsync(
        long travelerId,
        TourSchedule schedule,
        BookingStatus status,
        int quantity = 2,
        decimal totalAmount = 3_200_000m)
    {
        var booking = Booking.Restore(
            ++_lastBookingId,
            travelerId,
            schedule.Id,
            quantity,
            totalAmount,
            status,
            status is BookingStatus.PendingPayment or BookingStatus.Expired ? PaymentStatus.Unpaid : PaymentStatus.Paid,
            Booking.DefaultCancellationPolicySnapshot);
        Set(booking, nameof(Booking.BookingCode), $"TM-{booking.Id:D6}");
        Db.Bookings.Add(booking);
        await Db.SaveChangesAsync();
        return booking;
    }

    public async Task AddParticipantsAsync(long bookingId, int adults = 0, int children = 0, int infants = 0)
    {
        void Add(ParticipantType type, int count)
        {
            for (var index = 0; index < count; index++)
            {
                var participant = (BookingParticipant)Activator.CreateInstance(typeof(BookingParticipant), nonPublic: true)!;
                Set(participant, nameof(BookingParticipant.BookingId), bookingId);
                Set(participant, nameof(BookingParticipant.FullName), $"{type} {index + 1}");
                Set(participant, nameof(BookingParticipant.ParticipantType), type);
                Db.BookingParticipants.Add(participant);
            }
        }

        Add(ParticipantType.Adult, adults);
        Add(ParticipantType.Child, children);
        Add(ParticipantType.Infant, infants);
        await Db.SaveChangesAsync();
    }

    public async Task MarkCancelledAsync(Booking booking, DateTimeOffset cancelledAtUtc, string? reason)
    {
        Set(booking, nameof(Booking.CancelledAtUtc), (DateTimeOffset?)cancelledAtUtc);
        Set(booking, nameof(Booking.CancelReason), reason);
        await Db.SaveChangesAsync();
    }

    public async Task<PaymentTransaction> AddPaymentAsync(long bookingId, long id, PaymentGateway gateway)
    {
        var payment = (PaymentTransaction)Activator.CreateInstance(typeof(PaymentTransaction), nonPublic: true)!;
        payment.Id = id;
        Set(payment, nameof(PaymentTransaction.BookingId), bookingId);
        Set(payment, nameof(PaymentTransaction.Gateway), gateway);
        Set(payment, nameof(PaymentTransaction.Amount), 1_000_000m);
        Set(payment, nameof(PaymentTransaction.TransactionType), PaymentTransaction.TypePayment);
        Set(payment, nameof(PaymentTransaction.Status), PaymentTransactionStatus.Success);
        Set(payment, nameof(PaymentTransaction.CreatedAtUtc), BaseTime);
        Db.PaymentTransactions.Add(payment);
        await Db.SaveChangesAsync();
        return payment;
    }

    public async Task<Ticket> AddTicketAsync(long bookingId, TicketStatus status, long id)
    {
        var ticket = (Ticket)Activator.CreateInstance(typeof(Ticket), nonPublic: true)!;
        ticket.Id = id;
        Set(ticket, nameof(Ticket.BookingId), bookingId);
        Set(ticket, nameof(Ticket.Status), status);
        Db.Tickets.Add(ticket);
        await Db.SaveChangesAsync();
        return ticket;
    }

    public async Task<Refund> AddRefundAsync(
        long bookingId,
        long id,
        RefundStatus status = RefundStatus.Processed,
        decimal amount = 1_000_000m,
        long? transactionId = null)
    {
        var refund = (Refund)Activator.CreateInstance(typeof(Refund), nonPublic: true)!;
        refund.Id = id;
        Set(refund, nameof(Refund.BookingId), bookingId);
        Set(refund, nameof(Refund.TransactionId), transactionId);
        Set(refund, nameof(Refund.Amount), amount);
        Set(refund, nameof(Refund.Status), status);
        Set(refund, nameof(Refund.RequestedAtUtc), BaseTime);
        Db.Refunds.Add(refund);
        await Db.SaveChangesAsync();
        return refund;
    }

    public async Task<TripReview> AddReviewAsync(long bookingId, long travelerId, long id, byte rating = 5)
    {
        var review = (TripReview)Activator.CreateInstance(typeof(TripReview), nonPublic: true)!;
        review.Id = id;
        Set(review, nameof(TripReview.BookingId), (long?)bookingId);
        Set(review, nameof(TripReview.TravelerUserId), travelerId);
        Set(review, nameof(TripReview.OverallRating), rating);
        Set(review, nameof(TripReview.CreatedAtUtc), BaseTime);
        Db.TripReviews.Add(review);
        await Db.SaveChangesAsync();
        return review;
    }

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType()
            .GetProperty(property, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private async Task<PointOfInterest> GetPoiAsync()
    {
        if (_poi is not null)
        {
            return _poi;
        }

        var category = PoiCategory.Create("Trip history", null);
        Db.PoiCategories.Add(category);
        await Db.SaveChangesAsync();
        _poi = PointOfInterest.Create(category, "Dragon Bridge", 16m, 108m, 1, BaseTime);
        Db.PointsOfInterest.Add(_poi);
        await Db.SaveChangesAsync();
        return _poi;
    }
}