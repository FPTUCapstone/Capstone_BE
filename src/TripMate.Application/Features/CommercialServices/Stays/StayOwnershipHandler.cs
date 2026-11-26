using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.ServiceProviderRequests;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.CommercialServices.Stays;

public sealed record StayOwnershipView(long Id, long PoiId, string FacilityName, string Applicant, string? Email,
    string Evidence, string Status, string? ReviewNote);
public sealed record RequestStayOwnership(long PoiId, string? Evidence) : IRequest<Result<StayOwnershipView>>;
public sealed record ListStayOwnershipClaims(bool Staff, long? BeforeId) : IRequest<Result<IReadOnlyList<StayOwnershipView>>>;
public sealed record ReviewStayOwnership(long Id, bool Approved, string? Note) : IRequest<Result<StayOwnershipView>>;

public sealed class StayOwnershipHandler(IApplicationDbContext db, ICurrentUserService currentUser,
    IDateTimeProvider clock, IServiceProviderRequestLock requestLock)
    : IRequestHandler<RequestStayOwnership, Result<StayOwnershipView>>,
      IRequestHandler<ListStayOwnershipClaims, Result<IReadOnlyList<StayOwnershipView>>>,
      IRequestHandler<ReviewStayOwnership, Result<StayOwnershipView>>
{
    public async Task<Result<StayOwnershipView>> Handle(RequestStayOwnership request, CancellationToken token)
    {
        if (currentUser.UserId is not long id) return Result.Failure<StayOwnershipView>("Stay.Forbidden", "Vui lòng đăng nhập.");
        if (request.PoiId <= 0 || request.Evidence?.Trim() is not { Length: > 0 and <= 1000 } evidence)
            return Result.Failure<StayOwnershipView>("Stay.InvalidInput", "Vui lòng nhập thông tin chứng minh quyền quản lý.");

        return await db.ExecuteInTransactionAsync(async ct =>
        {
            db.ClearTrackedEntities();
            await requestLock.AcquireBookingAsync(request.PoiId, ct);
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
            if (user is null || !CanApply(user))
                return Result.Failure<StayOwnershipView>("Stay.Forbidden", "Cần xác minh email của tài khoản đại diện.");
            var accommodationIds = PlanningPoiEligibility.AccommodationPoiIds(db);
            if (!await db.PointsOfInterest.AnyAsync(item => item.Id == request.PoiId
                && item.Status == PointOfInterestStatus.Active && accommodationIds.Contains(item.Id), ct))
                return Result.Failure<StayOwnershipView>("Stay.NotFound", "Không tìm thấy cơ sở lưu trú.");
            if (await db.ProviderPoiLinks.AnyAsync(item => item.PointOfInterestId == request.PoiId, ct))
                return Result.Failure<StayOwnershipView>("Stay.Conflict", "Cơ sở đã có tài khoản phụ trách.");

            var existing = await db.StayOwnershipClaims.AsNoTracking().SingleOrDefaultAsync(item =>
                item.ApplicantId == id && item.PoiId == request.PoiId && item.Status == StayOwnershipClaim.Pending, ct);
            if (existing is not null) return Result.Success(await FindView(existing.Id, ct));

            var claim = new StayOwnershipClaim(request.PoiId, id, evidence, clock.UtcNow);
            db.StayOwnershipClaims.Add(claim);
            await db.SaveChangesAsync(ct);
            return Result.Success(await FindView(claim.Id, ct));
        }, token);
    }

    public async Task<Result<IReadOnlyList<StayOwnershipView>>> Handle(ListStayOwnershipClaims request, CancellationToken token)
    {
        if (currentUser.UserId is not long actorId)
            return Result.Failure<IReadOnlyList<StayOwnershipView>>("Stay.Forbidden", "Vui lòng đăng nhập.");
        var claims = db.StayOwnershipClaims.AsNoTracking();
        claims = request.Staff ? claims.Where(item => item.Status == StayOwnershipClaim.Pending)
            : claims.Where(item => item.ApplicantId == actorId);
        if (request.BeforeId is long beforeId) claims = claims.Where(item => item.Id < beforeId);
        return Result.Success<IReadOnlyList<StayOwnershipView>>(await Views(claims.OrderByDescending(item => item.Id)
            .Take(30)).ToListAsync(token));
    }

    public async Task<Result<StayOwnershipView>> Handle(ReviewStayOwnership request, CancellationToken token)
    {
        if (currentUser.UserId is not long staffId) return Result.Failure<StayOwnershipView>("Stay.Forbidden", "Vui lòng đăng nhập.");
        if (request.Note?.Trim() is not { Length: > 0 and <= 1000 } note)
            return Result.Failure<StayOwnershipView>("Stay.InvalidInput", "Cần ghi căn cứ xác minh hoặc lý do từ chối.");

        return await db.ExecuteInTransactionAsync(async ct =>
        {
            db.ClearTrackedEntities();
            if (!await db.Users.AnyAsync(item => item.Id == staffId && item.Role == UserRole.Staff
                && item.Status == AccountStatus.Active && !item.MustChangePassword, ct))
                return Result.Failure<StayOwnershipView>("Stay.Forbidden", "Tài khoản không có quyền duyệt.");
            var target = await db.StayOwnershipClaims.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.Id, ct);
            if (target is null) return Result.Failure<StayOwnershipView>("Stay.NotFound", "Không tìm thấy hồ sơ.");

            await requestLock.AcquireOperationAsync(target.ApplicantId, new Guid("bde8cf89-5f32-4c13-996b-24427cf72d35"), ct);
            await requestLock.AcquireBookingAsync(target.PoiId, ct);
            var claim = await db.StayOwnershipClaims.SingleAsync(item => item.Id == request.Id, ct);
            if (claim.Status != StayOwnershipClaim.Pending)
                return Result.Failure<StayOwnershipView>("Stay.Conflict", "Hồ sơ đã được xử lý.");

            if (request.Approved)
            {
                var user = await db.Users.SingleAsync(item => item.Id == claim.ApplicantId, ct);
                var poi = await db.PointsOfInterest.SingleAsync(item => item.Id == claim.PoiId, ct);
                if (!CanApply(user) || poi.Status != PointOfInterestStatus.Active
                    || await db.ProviderPoiLinks.AnyAsync(item => item.PointOfInterestId == claim.PoiId, ct))
                    return Result.Failure<StayOwnershipView>("Stay.Conflict", "Tài khoản hoặc quyền quản lý đã thay đổi.");
                var provider = await db.ServiceProviders.SingleOrDefaultAsync(item => item.UserId == user.Id, ct);
                if (provider is null)
                {
                    if (user.Role != UserRole.Traveler)
                        return Result.Failure<StayOwnershipView>("Stay.Conflict", "Tài khoản chưa có hồ sơ đối tác hợp lệ.");
                    user.BecomeServiceProvider(clock.UtcNow);
                    provider = ServiceProvider.Create(poi.Name[..Math.Min(poi.Name.Length, ServiceProvider.NameMaxLength)],
                        CommercialService.CategoryHotel, ServiceProvider.StatusActive);
                    provider.AssignAccount(user);
                    db.ServiceProviders.Add(provider);
                }
                else if (provider.ServiceCategory != CommercialService.CategoryHotel || provider.Status != ServiceProvider.StatusActive)
                    return Result.Failure<StayOwnershipView>("Stay.Conflict", "Tài khoản chưa có quyền quản lý lưu trú.");

                db.ProviderPoiLinks.Add(ProviderPoiLink.Create(provider, poi));
            }
            claim.Review(staffId, request.Approved, note);
            await db.SaveChangesAsync(ct);
            return Result.Success(await FindView(claim.Id, ct));
        }, token);
    }

    private static bool CanApply(User user) => user.Status == AccountStatus.Active && !user.MustChangePassword
        && user.EmailVerifiedAtUtc.HasValue && user.Role is UserRole.Traveler or UserRole.ServiceProvider;

    private IQueryable<StayOwnershipView> Views(IQueryable<StayOwnershipClaim> claims) =>
        from claim in claims
        join poi in db.PointsOfInterest.AsNoTracking() on claim.PoiId equals poi.Id
        join user in db.Users.AsNoTracking() on claim.ApplicantId equals user.Id
        select new StayOwnershipView(claim.Id, claim.PoiId, poi.Name, user.FullName, user.Email, claim.Evidence,
            claim.Status, claim.ReviewNote);

    private Task<StayOwnershipView> FindView(long id, CancellationToken token) =>
        Views(db.StayOwnershipClaims.AsNoTracking().Where(item => item.Id == id)).SingleAsync(token);
}
