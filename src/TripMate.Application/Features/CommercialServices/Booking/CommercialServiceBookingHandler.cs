using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;

using MediatR;
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.ServiceProviderRequests;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.CommercialServices.Booking;

public sealed record CommercialServiceOption(long Id, string Name, string Category, decimal PriceAmount, string PriceUnit);
public sealed record CommercialServiceSlot(DateTimeOffset StartsAt, int Quantity, int RemainingQuantity);
public sealed record CommercialServiceSubmission(long Id, string RequestCode, string Status, decimal EstimatedAmount);
public sealed record TravelerServiceBooking(long Id, string RequestCode, string ServiceName, string Status,
    DateTimeOffset StartsAt, int Quantity, decimal EstimatedAmount);
public sealed record SubmitServiceBookingBody(long ServiceId, DateTimeOffset StartsAt, int Quantity,
    string? ContactName, string? ContactPhone, string? ContactEmail, string? SpecialRequest = null, long? ItineraryId = null);

public sealed record ListBookableServiceOptions(bool ForProvider, long? PoiId = null)
    : IRequest<Result<IReadOnlyList<CommercialServiceOption>>>;
public sealed record ListCommercialServiceSlots(long ServiceId, bool ForProvider, DateTimeOffset? From, DateTimeOffset? To)
    : IRequest<Result<IReadOnlyList<CommercialServiceSlot>>>;
public sealed record PublishCommercialServiceSlot(long ServiceId, DateTimeOffset StartsAt, int Quantity)
    : IRequest<Result<CommercialServiceSlot>>;
public sealed record SubmitCommercialServiceBooking(SubmitServiceBookingBody Body, Guid Key)
    : IRequest<Result<CommercialServiceSubmission>>;
public sealed record ListTravelerServiceBookings(long? BeforeId)
    : IRequest<Result<IReadOnlyList<TravelerServiceBooking>>>;
public sealed record CancelTravelerServiceBooking(long BookingId)
    : IRequest<Result<CommercialServiceSubmission>>;

public sealed class CommercialServiceBookingHandler(IApplicationDbContext db, ICurrentUserService current,
    IDateTimeProvider clock, IServiceProviderRequestLock requestLock)
    : IRequestHandler<ListBookableServiceOptions, Result<IReadOnlyList<CommercialServiceOption>>>,
      IRequestHandler<ListCommercialServiceSlots, Result<IReadOnlyList<CommercialServiceSlot>>>,
      IRequestHandler<PublishCommercialServiceSlot, Result<CommercialServiceSlot>>,
      IRequestHandler<SubmitCommercialServiceBooking, Result<CommercialServiceSubmission>>,
      IRequestHandler<ListTravelerServiceBookings, Result<IReadOnlyList<TravelerServiceBooking>>>,
      IRequestHandler<CancelTravelerServiceBooking, Result<CommercialServiceSubmission>>
{
    public const string SlotUnavailable = "MSG70";
    public const string QuantityUnavailable = "MSG71";
    public const string ServiceUnavailable = "MSG75";
    public const string PastDate = "MSG76";
    public async Task<Result<IReadOnlyList<CommercialServiceOption>>> Handle(ListBookableServiceOptions request, CancellationToken token)
    {
        var actor = request.ForProvider ? await ProviderRequestAccess.ActorAsync(db, current, token) : null;
        if (request.ForProvider && actor is null) return Forbidden<IReadOnlyList<CommercialServiceOption>>();

        var services = Services(actor);
        if (request.PoiId.HasValue) services = services.Where(service => service.PoiId == request.PoiId);
        var options = await services.OrderBy(service => service.Name).ThenBy(service => service.Id)
            .Select(service => new CommercialServiceOption(service.Id, service.Name, service.ServiceCategory,
                service.PriceAmount, service.PriceUnit)).ToListAsync(token);
        return Result.Success<IReadOnlyList<CommercialServiceOption>>(options);
    }

    public async Task<Result<IReadOnlyList<CommercialServiceSlot>>> Handle(ListCommercialServiceSlots request, CancellationToken token)
    {
        var actor = request.ForProvider ? await ProviderRequestAccess.ActorAsync(db, current, token) : null;
        if (request.ForProvider && actor is null) return Forbidden<IReadOnlyList<CommercialServiceSlot>>();
        if (!await Services(actor).AnyAsync(service => service.Id == request.ServiceId, token))
            return NotFound<IReadOnlyList<CommercialServiceSlot>>();

        var from = request.From?.ToUniversalTime() ?? clock.UtcNow;
        if (from > DateTimeOffset.MaxValue.AddDays(-30))
            return Invalid<IReadOnlyList<CommercialServiceSlot>>("Choose a valid availability date range.");
        var to = request.To?.ToUniversalTime() ?? from.AddDays(30);
        if (to <= from || to - from > TimeSpan.FromDays(90))
            return Invalid<IReadOnlyList<CommercialServiceSlot>>("Choose a valid availability date range.");

        var slots = await db.ServiceAvailability.AsNoTracking()
            .Where(slot => slot.ServiceId == request.ServiceId && slot.StartsAtUtc >= from && slot.StartsAtUtc < to)
            .Where(slot => request.ForProvider || slot.Quantity > 0)
            .OrderBy(slot => slot.StartsAtUtc)
            .Select(slot => new CommercialServiceSlot(slot.StartsAtUtc, slot.Quantity,
                slot.Quantity - db.ServiceBookings.Where(booking => booking.ServiceId == slot.ServiceId
                    && booking.StartAtUtc == slot.StartsAtUtc && booking.Status == ServiceBooking.Confirmed)
                    .Sum(booking => booking.Quantity)))
            .ToListAsync(token);
        return Result.Success<IReadOnlyList<CommercialServiceSlot>>(slots.Select(slot =>
            slot with { RemainingQuantity = Math.Max(0, slot.RemainingQuantity) }).ToList());
    }

    public async Task<Result<CommercialServiceSlot>> Handle(PublishCommercialServiceSlot request, CancellationToken token)
    {
        var startsAt = request.StartsAt.ToUniversalTime();
        if (startsAt <= clock.UtcNow || startsAt > DateTimeOffset.MaxValue.AddDays(-5) || request.Quantity < 0)
            return Invalid<CommercialServiceSlot>("Choose a future time and a nonnegative quantity.");

        return await db.ExecuteInTransactionAsync(async ct =>
        {
            db.ClearTrackedEntities();
            await requestLock.AcquireAvailabilityAsync(request.ServiceId, startsAt, ct);
            var actor = await ProviderRequestAccess.ActorAsync(db, current, ct);
            if (actor is null) return Forbidden<CommercialServiceSlot>();
            var service = await db.ExecuteWithSerializableReadsAsync(
                readToken => Services(actor).SingleOrDefaultAsync(item => item.Id == request.ServiceId, readToken), ct);
            if (service is null) return NotFound<CommercialServiceSlot>();
            if (service.Capacity is int capacity && request.Quantity > capacity)
                return Invalid<CommercialServiceSlot>("The published quantity exceeds the service capacity.");
            var confirmed = await CountConfirmedAsync(db, service.Id, startsAt, ct);
            if (request.Quantity < confirmed) return Conflict<CommercialServiceSlot>("This time already has confirmed requests.");

            var slot = await db.ServiceAvailability.SingleOrDefaultAsync(
                item => item.ServiceId == service.Id && item.StartsAtUtc == startsAt, ct);
            if (slot is null) db.ServiceAvailability.Add(new ServiceAvailability(service.Id, startsAt, request.Quantity));
            else slot.SetQuantity(request.Quantity);
            await db.SaveChangesAsync(ct);
            return Result.Success(new CommercialServiceSlot(startsAt, request.Quantity, request.Quantity - confirmed));
        }, token);
    }

    public async Task<Result<CommercialServiceSubmission>> Handle(SubmitCommercialServiceBooking request, CancellationToken token)
    {
        if (current.UserId is not long travelerId || current.Role != nameof(UserRole.Traveler))
            return Forbidden<CommercialServiceSubmission>();
        var body = request.Body with
        {
            StartsAt = request.Body.StartsAt.ToUniversalTime(),
            ContactName = request.Body.ContactName?.Trim(),
            ContactPhone = request.Body.ContactPhone?.Trim(),
            ContactEmail = request.Body.ContactEmail?.Trim().ToLowerInvariant(),
            SpecialRequest = string.IsNullOrWhiteSpace(request.Body.SpecialRequest) ? null : request.Body.SpecialRequest.Trim(),
        };
        if (request.Key == Guid.Empty || body.ServiceId <= 0 || body.Quantity <= 0 || body.ItineraryId <= 0
            || string.IsNullOrWhiteSpace(body.ContactName) || body.ContactName.Length > 150
            || string.IsNullOrWhiteSpace(body.ContactPhone) || body.ContactPhone.Length > 20 || !new PhoneAttribute().IsValid(body.ContactPhone)
            || string.IsNullOrWhiteSpace(body.ContactEmail) || body.ContactEmail.Length > 256 || !new EmailAddressAttribute().IsValid(body.ContactEmail)
            || body.SpecialRequest?.Length > ServiceBooking.SpecialRequestMaxLength)
            return Invalid<CommercialServiceSubmission>("Enter the required booking and contact details.");

        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body)));
        return await db.ExecuteInTransactionAsync(async ct =>
        {
            db.ClearTrackedEntities();
            await requestLock.AcquireOperationAsync(travelerId, request.Key, ct);
            if (!await db.Users.AnyAsync(user => user.Id == travelerId && user.Role == UserRole.Traveler
                && user.Status == AccountStatus.Active && !user.MustChangePassword, ct))
                return Forbidden<CommercialServiceSubmission>();
            var previous = await db.ServiceBookings.AsNoTracking().SingleOrDefaultAsync(
                item => item.TravelerUserId == travelerId && item.SubmitKey == request.Key, ct);
            if (previous is not null)
                return previous.SubmitHash == hash ? Result.Success(Submitted(previous))
                    : Conflict<CommercialServiceSubmission>("The idempotency key was used for a different request.");

            var now = clock.UtcNow;
            if (body.StartsAt <= now)
                return Result.Failure<CommercialServiceSubmission>(PastDate, "Choose a future time.");
            await requestLock.AcquireAvailabilityAsync(body.ServiceId, body.StartsAt, ct);
            var service = await db.ExecuteWithSerializableReadsAsync(
                readToken => Services(null).SingleOrDefaultAsync(item => item.Id == body.ServiceId, readToken), ct);
            if (service is null)
                return Result.Failure<CommercialServiceSubmission>(ServiceUnavailable, "The service is no longer open for booking.");
            if (body.ItineraryId is long itineraryId && !await db.Itineraries.AnyAsync(
                item => item.Id == itineraryId && item.TravelerUserId == travelerId, ct))
                return NotFound<CommercialServiceSubmission>();

            var slot = await db.ServiceAvailability.AsNoTracking().SingleOrDefaultAsync(
                item => item.ServiceId == service.Id && item.StartsAtUtc == body.StartsAt, ct);
            if (slot is null || slot.Quantity == 0)
                return Result.Failure<CommercialServiceSubmission>(SlotUnavailable, "The selected time is unavailable.");
            var confirmed = await CountConfirmedAsync(db, service.Id, body.StartsAt, ct);
            if (body.Quantity > slot.Quantity - confirmed)
                return Result.Failure<CommercialServiceSubmission>(QuantityUnavailable, "The requested quantity exceeds the remaining availability.");
            var estimatedAmount = service.PriceAmount * body.Quantity;
            if (estimatedAmount > ServiceBooking.MaximumAmount)
                return Invalid<CommercialServiceSubmission>("The estimated amount exceeds the supported limit.");

            var booking = ServiceBooking.CreatePendingRequest(travelerId, service.Id, body.StartsAt, null,
                body.Quantity, estimatedAmount, now, body.ItineraryId, service.Name, body.SpecialRequest);
            booking.SetSubmission(request.Key, hash, body.ContactName!, body.ContactPhone!, body.ContactEmail!);
            db.ServiceBookings.Add(booking);
            await db.SaveChangesAsync(ct);
            return Result.Success(Submitted(booking));
        }, token);
    }

    public async Task<Result<IReadOnlyList<TravelerServiceBooking>>> Handle(ListTravelerServiceBookings request, CancellationToken token)
    {
        if (current.UserId is not long travelerId || current.Role != nameof(UserRole.Traveler)
            || !await IsActiveTravelerAsync(travelerId, token))
            return Forbidden<IReadOnlyList<TravelerServiceBooking>>();
        if (request.BeforeId <= 0) return Invalid<IReadOnlyList<TravelerServiceBooking>>("Invalid request cursor.");
        var query = TravelerRequests(travelerId).AsNoTracking();
        if (request.BeforeId is long beforeId) query = query.Where(item => item.Id < beforeId);
        var result = await query.OrderByDescending(item => item.Id).Take(20)
            .Select(item => new TravelerServiceBooking(item.Id, "SR-" + item.Id, item.Service.Name,
                item.Status, item.StartAtUtc, item.Quantity, item.TotalPrice)).ToListAsync(token);
        return Result.Success<IReadOnlyList<TravelerServiceBooking>>(result);
    }

    public async Task<Result<CommercialServiceSubmission>> Handle(CancelTravelerServiceBooking request, CancellationToken token)
    {
        if (current.UserId is not long travelerId || current.Role != nameof(UserRole.Traveler))
            return Forbidden<CommercialServiceSubmission>();

        return await db.ExecuteInTransactionAsync(async ct =>
        {
            db.ClearTrackedEntities();
            await requestLock.AcquireBookingAsync(request.BookingId, ct);
            if (!await IsActiveTravelerAsync(travelerId, ct))
                return Forbidden<CommercialServiceSubmission>();
            var booking = await TravelerRequests(travelerId).SingleOrDefaultAsync(item => item.Id == request.BookingId, ct);
            if (booking is null) return NotFound<CommercialServiceSubmission>();
            if (booking.Status == ServiceBooking.Cancelled) return Result.Success(Receipt(booking, booking.Status));

            var now = clock.UtcNow;
            if (booking.TryCancel(now))
            {
                ProviderRequestEvents.Record(db, booking, travelerId, ServiceBooking.PendingConfirmation, now);
                await db.SaveChangesAsync(ct);
                return Result.Success(Receipt(booking, booking.Status));
            }
            if (booking.TryExpire(now))
            {
                ProviderRequestEvents.Record(db, booking, null, ServiceBooking.PendingConfirmation, now);
                await db.SaveChangesAsync(ct);
            }
            return Conflict<CommercialServiceSubmission>("The request is no longer awaiting confirmation.");
        }, token);
    }

    private IQueryable<ServiceBooking> TravelerRequests(long travelerId) =>
        db.ServiceBookings.Where(item => item.TravelerUserId == travelerId && item.RequestSubmittedAtUtc != null);

    private Task<bool> IsActiveTravelerAsync(long travelerId, CancellationToken token) =>
        db.Users.AnyAsync(user => user.Id == travelerId && user.Role == UserRole.Traveler
            && user.Status == AccountStatus.Active && !user.MustChangePassword, token);

    private IQueryable<CommercialService> Services(long? providerUserId) =>
        db.CommercialServices.AsNoTracking().Where(service =>
            db.ProviderPoiLinks.Any(link => link.PointOfInterestId == service.PoiId
                && link.Provider.Status == ServiceProvider.StatusActive
                && link.Provider.User != null && link.Provider.User.Role == UserRole.ServiceProvider
                && link.Provider.User.Status == AccountStatus.Active && !link.Provider.User.MustChangePassword
                && (!providerUserId.HasValue || link.Provider.UserId == providerUserId)
                && link.Poi.Status == PointOfInterestStatus.Active)
            && (providerUserId.HasValue || service.AvailabilityStatus == CommercialService.AvailabilityAvailable));

    internal static Task<int> CountConfirmedAsync(IApplicationDbContext db, long serviceId, DateTimeOffset startsAt, CancellationToken token) =>
        db.ServiceBookings.Where(booking => booking.ServiceId == serviceId && booking.StartAtUtc == startsAt
            && booking.Status == ServiceBooking.Confirmed).SumAsync(booking => booking.Quantity, token);

    private static CommercialServiceSubmission Submitted(ServiceBooking item) =>
        Receipt(item, ServiceBooking.PendingConfirmation);

    private static CommercialServiceSubmission Receipt(ServiceBooking item, string status) =>
        new(item.Id, $"SR-{item.Id}", status, item.TotalPrice);

    private static Result<T> Forbidden<T>() => Result.Failure<T>(ProviderRequestErrors.Forbidden, "This account cannot perform the operation.");
    private static Result<T> NotFound<T>() => Result.Failure<T>(ProviderRequestErrors.NotFound, "The service or itinerary was not found.");
    private static Result<T> Invalid<T>(string message) => Result.Failure<T>(ProviderRequestErrors.InvalidInput, message);
    private static Result<T> Conflict<T>(string message) => Result.Failure<T>(ProviderRequestErrors.InvalidState, message);
}
