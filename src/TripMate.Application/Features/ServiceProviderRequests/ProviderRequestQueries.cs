using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.ServiceProviderRequests;

internal static class ProviderRequestAccess
{
    public static async Task<long?> ActorAsync(IApplicationDbContext db, ICurrentUserService current,
        CancellationToken token)
    {
        if (current.UserId is not long id || id <= 0 || current.Role != nameof(UserRole.ServiceProvider)) return null;
        if (!await IsActiveUserAsync(db, id, token)) return null;
        return await db.ServiceProviders.AnyAsync(provider => provider.UserId == id
            && provider.Status == ServiceProvider.StatusActive, token) ? id : null;
    }

    public static Task<bool> IsActiveUserAsync(IApplicationDbContext db, long actorId, CancellationToken token) =>
        db.Users.AnyAsync(user => user.Id == actorId && user.Role == UserRole.ServiceProvider
            && user.Status == AccountStatus.Active && !user.MustChangePassword, token);

    public static IQueryable<ServiceBooking> Owned(IApplicationDbContext db, long actorId) =>
        db.ServiceBookings.Where(booking => booking.RequestSubmittedAtUtc != null
            && db.ProviderPoiLinks.Any(link => link.PointOfInterestId == booking.Service.PoiId
                && link.Provider.UserId == actorId && link.Provider.Status == ServiceProvider.StatusActive));
}

public sealed class ListServiceProviderRequestsHandler(IApplicationDbContext db, ICurrentUserService current)
    : IRequestHandler<ListServiceProviderRequests, Result<ProviderRequestPage>>
{
    public async Task<Result<ProviderRequestPage>> Handle(ListServiceProviderRequests request, CancellationToken token)
    {
        var actor = await ProviderRequestAccess.ActorAsync(db, current, token);
        if (actor is null) return Result.Failure<ProviderRequestPage>(ProviderRequestErrors.Forbidden,
            "An active Service Provider account is required.");
        if (!(await new ListServiceProviderRequestsValidator().ValidateAsync(request, token)).IsValid)
            return Result.Failure<ProviderRequestPage>(ProviderRequestErrors.InvalidInput, "The request filters are invalid.");

        var bookings = ProviderRequestAccess.Owned(db, actor.Value).AsNoTracking();
        if (request.Status is not null) bookings = bookings.Where(booking => booking.Status == request.Status);
        if (request.RequestedDateFrom is DateOnly from)
        {
            var fromUtc = from == DateOnly.MinValue ? DateTimeOffset.MinValue
                : new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), ServiceBooking.VietnamOffset).ToUniversalTime();
            bookings = bookings.Where(booking => booking.StartAtUtc >= fromUtc);
        }
        if (request.RequestedDateTo is DateOnly to)
        {
            var untilUtc = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), ServiceBooking.VietnamOffset).ToUniversalTime();
            bookings = bookings.Where(booking => booking.StartAtUtc < untilUtc);
        }

        var rows = bookings.Include(booking => booking.Service).Join(db.Users.AsNoTracking(),
            booking => booking.TravelerUserId, traveler => traveler.Id, (booking, traveler) => new { Booking = booking, Traveler = traveler });
        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var keyword = request.Keyword.Trim();
            var code = keyword.StartsWith("SR-", StringComparison.OrdinalIgnoreCase) ? keyword[3..] : keyword;
            long.TryParse(code, out var bookingId);
            rows = rows.Where(row => row.Booking.Id == bookingId || row.Booking.Service.Name.Contains(keyword)
                || (row.Booking.ServiceOption != null && row.Booking.ServiceOption.Contains(keyword))
                || row.Traveler.FullName.Contains(keyword));
        }

        var total = await rows.CountAsync(token);
        var page = await rows.OrderBy(row => row.Booking.Status == ServiceBooking.PendingConfirmation ? 0 : 1)
            .ThenBy(row => row.Booking.StartAtUtc).ThenBy(row => row.Booking.Id)
            .Skip((request.PageNumber - 1) * request.PageSize).Take(request.PageSize).ToListAsync(token);
        return Result.Success(new ProviderRequestPage(page.Select(row =>
            ProviderRequestDetail.From(row.Booking, row.Traveler).ToSummary()).ToArray(), total,
            request.PageNumber, request.PageSize));
    }
}

public sealed class GetServiceProviderRequestHandler(IApplicationDbContext db, ICurrentUserService current)
    : IRequestHandler<GetServiceProviderRequest, Result<ProviderRequestDetail>>
{
    public async Task<Result<ProviderRequestDetail>> Handle(GetServiceProviderRequest request, CancellationToken token)
    {
        var actor = await ProviderRequestAccess.ActorAsync(db, current, token);
        if (actor is null) return Result.Failure<ProviderRequestDetail>(ProviderRequestErrors.Forbidden,
            "An active Service Provider account is required.");
        var booking = await ProviderRequestAccess.Owned(db, actor.Value).AsNoTracking()
            .Include(candidate => candidate.Service).SingleOrDefaultAsync(candidate => candidate.Id == request.Id, token);
        if (booking is null) return Result.Failure<ProviderRequestDetail>(ProviderRequestErrors.NotFound,
            "The service booking request could not be found.");
        var traveler = await db.Users.AsNoTracking().SingleAsync(user => user.Id == booking.TravelerUserId, token);
        return Result.Success(ProviderRequestDetail.From(booking, traveler));
    }
}