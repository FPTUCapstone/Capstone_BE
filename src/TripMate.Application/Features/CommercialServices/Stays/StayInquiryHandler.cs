using System.Security.Cryptography;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.ServiceProviderRequests;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.CommercialServices.Stays;

public sealed record StayFacility(long PoiId, string Name, string? Address, decimal Latitude, decimal Longitude,
    string? Description, bool Published, bool AcceptingRequests);
public sealed record StayInquiryView(long Id, long PoiId, string FacilityName, long? ItineraryId, DateOnly Arrival,
    DateOnly Departure, int Guests, string ContactName, string Contact, string? Note, string Status, string? Reply);
public sealed record StayInquiryBody(long PoiId, DateOnly Arrival, DateOnly Departure, int Guests,
    string? ContactName, string? Contact, string? Note, long? ItineraryId);
public sealed record ListStayFacilities(long? ItineraryId, string? Query, long? BeforeId, bool Provider, long? PoiId = null)
    : IRequest<Result<IReadOnlyList<StayFacility>>>;
public sealed record PublishStayFacility(long PoiId, string? Description, bool Published, bool AcceptingRequests)
    : IRequest<Result<StayFacility>>;
public sealed record SubmitStayInquiry(StayInquiryBody Body, Guid Key) : IRequest<Result<StayInquiryView>>;
public sealed record ListStayInquiries(bool Provider, long? ItineraryId, long? BeforeId)
    : IRequest<Result<IReadOnlyList<StayInquiryView>>>;
public sealed record GetStayInquiry(long Id, bool Provider) : IRequest<Result<StayInquiryView>>;
public sealed record ReplyToStayInquiry(long Id, bool Declined, string? Reply) : IRequest<Result<StayInquiryView>>;

public sealed class StayInquiryHandler(IApplicationDbContext db, ICurrentUserService currentUser,
    IDateTimeProvider clock, IServiceProviderRequestLock requestLock)
    : IRequestHandler<ListStayFacilities, Result<IReadOnlyList<StayFacility>>>,
      IRequestHandler<PublishStayFacility, Result<StayFacility>>,
      IRequestHandler<SubmitStayInquiry, Result<StayInquiryView>>,
      IRequestHandler<ListStayInquiries, Result<IReadOnlyList<StayInquiryView>>>,
      IRequestHandler<GetStayInquiry, Result<StayInquiryView>>,
      IRequestHandler<ReplyToStayInquiry, Result<StayInquiryView>>
{
    private const string InquiryEntity = "StayInquiry";
    private const int PageSize = 30;

    private IQueryable<ProviderPoiLink> HotelOperators => db.ProviderPoiLinks.Where(link =>
        link.Provider.ServiceCategory == CommercialService.CategoryHotel
        && link.Provider.Status == ServiceProvider.StatusActive
        && link.Provider.User != null && link.Provider.User.Role == UserRole.ServiceProvider
        && link.Provider.User.Status == AccountStatus.Active && !link.Provider.User.MustChangePassword
        && link.Poi.Status == PointOfInterestStatus.Active);

    private IQueryable<long> OwnedPoiIds(long actorId) =>
        HotelOperators.Where(link => link.Provider.UserId == actorId).Select(link => link.PointOfInterestId);

    public async Task<Result<IReadOnlyList<StayFacility>>> Handle(ListStayFacilities request, CancellationToken token)
    {
        var facilities = db.PointsOfInterest.AsNoTracking().Where(poi => poi.Status == PointOfInterestStatus.Active);
        if (request.Provider)
        {
            if (currentUser.UserId is not long actorId) return Forbidden<IReadOnlyList<StayFacility>>();
            facilities = facilities.Where(poi => OwnedPoiIds(actorId).Contains(poi.Id));
        }
        else
        {
            var accommodations = PlanningPoiEligibility.AccommodationPoiIds(db);
            facilities = facilities.Where(poi => db.StayListings.Any(listing => listing.PoiId == poi.Id && listing.Published)
                || (accommodations.Contains(poi.Id) && !db.StayListings.Any(listing => listing.PoiId == poi.Id)));
        }

        if (request.PoiId is long poiId) facilities = facilities.Where(poi => poi.Id == poiId);
        if (request.BeforeId is long beforeId) facilities = facilities.Where(poi => poi.Id < beforeId);
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var term = request.Query.Trim();
            if (term.Length > 150) return Invalid<IReadOnlyList<StayFacility>>("Từ khóa quá dài.");
            facilities = facilities.Where(poi => poi.Name.Contains(term) || (poi.Address != null && poi.Address.Contains(term)));
        }

        var query = from poi in facilities
                    join listing in db.StayListings.AsNoTracking() on poi.Id equals listing.PoiId into matches
                    from listing in matches.DefaultIfEmpty()
                    select new
                    {
                        PoiId = poi.Id, poi.Name, poi.Address, poi.Latitude, poi.Longitude,
                        Description = listing != null ? listing.Description ?? poi.Description : poi.Description,
                        Published = listing != null && listing.Published,
                        AcceptingRequests = listing != null && listing.AcceptingRequests
                            && HotelOperators.Any(link => link.PointOfInterestId == poi.Id),
                    };

        if (request.ItineraryId is long itineraryId)
        {
            if (currentUser.UserId is not long travelerId) return Forbidden<IReadOnlyList<StayFacility>>();
            var itinerary = await db.Itineraries.AsNoTracking().Where(item => item.Id == itineraryId && item.TravelerUserId == travelerId)
                .Select(item => new
                {
                    Latitude = item.SchedulingRequest == null ? (decimal?)null : item.SchedulingRequest.ExplorationLatitude,
                    Longitude = item.SchedulingRequest == null ? (decimal?)null : item.SchedulingRequest.ExplorationLongitude,
                }).SingleOrDefaultAsync(token);
            if (itinerary is null) return NotFound<IReadOnlyList<StayFacility>>();
            if (itinerary.Latitude is not decimal latitude || itinerary.Longitude is not decimal longitude)
                return Result.Success<IReadOnlyList<StayFacility>>([]);
            const decimal searchDegrees = 0.18m;
            query = query.Where(item => item.Latitude >= latitude - searchDegrees && item.Latitude <= latitude + searchDegrees
                && item.Longitude >= longitude - searchDegrees && item.Longitude <= longitude + searchDegrees);
            query = query
                .OrderByDescending(item => item.AcceptingRequests)
                .ThenBy(item => (item.Latitude - latitude) * (item.Latitude - latitude)
                    + (item.Longitude - longitude) * (item.Longitude - longitude))
                .ThenBy(item => item.PoiId).Take(8);
        }
        else query = query.OrderByDescending(item => item.PoiId).Take(PageSize);

        return Result.Success<IReadOnlyList<StayFacility>>(await query.Select(item => new StayFacility(item.PoiId, item.Name,
            item.Address, item.Latitude, item.Longitude, item.Description, item.Published, item.AcceptingRequests)).ToListAsync(token));
    }

    public async Task<Result<StayFacility>> Handle(PublishStayFacility request, CancellationToken token)
    {
        if (currentUser.UserId is not long actorId) return Forbidden<StayFacility>();
        if (request.Description?.Trim().Length > 2000) return Invalid<StayFacility>("Mô tả vượt quá 2.000 ký tự.");
        return await db.ExecuteInTransactionAsync(async ct =>
        {
            db.ClearTrackedEntities();
            await requestLock.AcquireBookingAsync(request.PoiId, ct);
            var accommodations = PlanningPoiEligibility.AccommodationPoiIds(db);
            var poi = await db.PointsOfInterest.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == request.PoiId && OwnedPoiIds(actorId).Contains(item.Id)
                    && accommodations.Contains(item.Id), ct);
            if (poi is null) return NotFound<StayFacility>();
            var listing = await db.StayListings.SingleOrDefaultAsync(item => item.PoiId == request.PoiId, ct);
            if (listing is null)
            {
                listing = new StayListing(request.PoiId);
                db.StayListings.Add(listing);
            }

            listing.Update(request.Description, request.Published, request.AcceptingRequests);
            await db.SaveChangesAsync(ct);
            return Result.Success(new StayFacility(poi.Id, poi.Name, poi.Address, poi.Latitude, poi.Longitude,
                listing.Description ?? poi.Description, listing.Published, listing.AcceptingRequests));
        }, token);
    }

    public async Task<Result<StayInquiryView>> Handle(SubmitStayInquiry request, CancellationToken token)
    {
        if (currentUser.UserId is not long travelerId) return Forbidden<StayInquiryView>();
        var body = request.Body with
        {
            Contact = request.Body.Contact?.Trim(),
            ContactName = request.Body.ContactName?.Trim(),
            Note = string.IsNullOrWhiteSpace(request.Body.Note) ? null : request.Body.Note.Trim(),
        };
        if (body.PoiId <= 0 || body.ItineraryId <= 0 || body.Guests <= 0 || body.Departure <= body.Arrival
            || body.ContactName is not { Length: > 0 and <= 150 } || body.Contact is not { Length: > 0 and <= 254 }
            || body.Note?.Length > 1000 || request.Key == Guid.Empty)
            return Invalid<StayInquiryView>("Kiểm tra ngày lưu trú, số khách và thông tin liên hệ.");

        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body)));
        return await db.ExecuteInTransactionAsync(async ct =>
        {
            db.ClearTrackedEntities();
            await requestLock.AcquireOperationAsync(travelerId, request.Key, ct);
            if (!await db.Users.AnyAsync(user => user.Id == travelerId && user.Role == UserRole.Traveler
                && user.Status == AccountStatus.Active && !user.MustChangePassword, ct))
                return Forbidden<StayInquiryView>();

            var existing = await db.StayInquiries.AsNoTracking().SingleOrDefaultAsync(item =>
                item.TravelerId == travelerId && item.SubmitKey == request.Key, ct);
            if (existing is not null)
                return existing.SubmitHash == hash
                    ? Result.Success(await FindView(existing.Id, ct))
                    : Conflict<StayInquiryView>("Khóa gửi lại đã dùng cho yêu cầu khác.");

            var now = clock.UtcNow;
            if (body.Arrival < DateOnly.FromDateTime(now.ToOffset(ServiceBooking.VietnamOffset).DateTime))
                return Invalid<StayInquiryView>("Ngày nhận phòng phải từ hôm nay.");
            if (body.ItineraryId is long itineraryId && !await db.Itineraries.AnyAsync(item =>
                item.Id == itineraryId && item.TravelerUserId == travelerId, ct))
                return NotFound<StayInquiryView>();

            await requestLock.AcquireBookingAsync(body.PoiId, ct);
            var providerId = await HotelOperators.Where(link => link.PointOfInterestId == body.PoiId)
                .Select(link => link.Provider.UserId).SingleOrDefaultAsync(ct);
            if (providerId is null || !await db.StayListings.AnyAsync(item =>
                item.PoiId == body.PoiId && item.Published && item.AcceptingRequests, ct))
                return Conflict<StayInquiryView>("Cơ sở hiện chưa nhận yêu cầu.");

            var inquiry = new StayInquiry(body.PoiId, travelerId, body.ItineraryId, body.Arrival, body.Departure,
                body.Guests, body.ContactName!, body.Contact!, body.Note, request.Key, hash, now);
            db.StayInquiries.Add(inquiry);
            await db.SaveChangesAsync(ct);
            AddNotice(providerId.Value, inquiry.Id, "StayInquirySent", "Yêu cầu lưu trú mới",
                "Mở yêu cầu để xem thông tin và phản hồi.", now);
            await db.SaveChangesAsync(ct);
            return Result.Success(await FindView(inquiry.Id, ct));
        }, token);
    }

    public async Task<Result<IReadOnlyList<StayInquiryView>>> Handle(ListStayInquiries request, CancellationToken token)
    {
        if (currentUser.UserId is not long actorId) return Forbidden<IReadOnlyList<StayInquiryView>>();
        var inquiries = db.StayInquiries.AsNoTracking();
        inquiries = request.Provider ? inquiries.Where(item => OwnedPoiIds(actorId).Contains(item.PoiId))
            : inquiries.Where(item => item.TravelerId == actorId);
        if (request.ItineraryId is long itineraryId) inquiries = inquiries.Where(item => item.ItineraryId == itineraryId);
        if (request.BeforeId is long beforeId) inquiries = inquiries.Where(item => item.Id < beforeId);
        return Result.Success<IReadOnlyList<StayInquiryView>>(await Views(inquiries.OrderByDescending(item => item.Id)
            .Take(PageSize)).ToListAsync(token));
    }

    public async Task<Result<StayInquiryView>> Handle(ReplyToStayInquiry request, CancellationToken token)
    {
        if (currentUser.UserId is not long actorId) return Forbidden<StayInquiryView>();
        if (request.Reply?.Trim() is not { Length: > 0 and <= 1000 } reply)
            return Invalid<StayInquiryView>("Vui lòng nhập phản hồi tối đa 1.000 ký tự.");
        return await db.ExecuteInTransactionAsync(async ct =>
        {
            db.ClearTrackedEntities();
            await requestLock.AcquireBookingAsync(request.Id, ct);
            var inquiry = await db.StayInquiries.AsTracking().SingleOrDefaultAsync(item =>
                item.Id == request.Id && OwnedPoiIds(actorId).Contains(item.PoiId), ct);
            if (inquiry is null) return NotFound<StayInquiryView>();
            if (!inquiry.TryReply(request.Declined, reply))
                return inquiry.Reply == reply && inquiry.Status == (request.Declined ? StayInquiry.Declined : StayInquiry.Replied)
                    ? Result.Success(await FindView(inquiry.Id, ct))
                    : Conflict<StayInquiryView>("Yêu cầu đã được xử lý.");
            AddNotice(inquiry.TravelerId, inquiry.Id, "StayInquiryResponded", "Phản hồi yêu cầu lưu trú",
                "Cơ sở đã cập nhật yêu cầu của bạn.", clock.UtcNow);
            await db.SaveChangesAsync(ct);
            return Result.Success(await FindView(inquiry.Id, ct));
        }, token);
    }

    public async Task<Result<StayInquiryView>> Handle(GetStayInquiry request, CancellationToken token)
    {
        if (currentUser.UserId is not long actorId) return Forbidden<StayInquiryView>();
        var inquiries = db.StayInquiries.AsNoTracking().Where(item => item.Id == request.Id);
        inquiries = request.Provider ? inquiries.Where(item => OwnedPoiIds(actorId).Contains(item.PoiId))
            : inquiries.Where(item => item.TravelerId == actorId);
        var result = await Views(inquiries).SingleOrDefaultAsync(token);
        return result is null ? NotFound<StayInquiryView>() : Result.Success(result);
    }

    private IQueryable<StayInquiryView> Views(IQueryable<StayInquiry> inquiries) =>
        from inquiry in inquiries
        join poi in db.PointsOfInterest.AsNoTracking() on inquiry.PoiId equals poi.Id
        select new StayInquiryView(inquiry.Id, inquiry.PoiId, poi.Name, inquiry.ItineraryId, inquiry.Arrival, inquiry.Departure,
            inquiry.Guests, inquiry.ContactName, inquiry.Contact, inquiry.Note, inquiry.Status, inquiry.Reply);

    private Task<StayInquiryView> FindView(long id, CancellationToken ct) =>
        Views(db.StayInquiries.AsNoTracking().Where(item => item.Id == id)).SingleAsync(ct);

    private void AddNotice(long userId, long id, string type, string title, string body, DateTimeOffset now) =>
        db.Notifications.Add(new Notification { UserId = userId, Channel = NotificationChannel.InApp, Type = type,
            RelatedEntityType = InquiryEntity, RelatedEntityId = id, Title = title, Body = body, CreatedAtUtc = now });

    private static Result<T> Invalid<T>(string text) => Result.Failure<T>("Stay.InvalidInput", text);
    private static Result<T> Conflict<T>(string text) => Result.Failure<T>("Stay.Conflict", text);
    private static Result<T> Forbidden<T>() => Result.Failure<T>("Stay.Forbidden", "Tài khoản không có quyền thực hiện thao tác.");
    private static Result<T> NotFound<T>() => Result.Failure<T>("Stay.NotFound", "Không tìm thấy dữ liệu.");
}
