using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.UserAccounts;

internal static class UserAccountAccess
{
    public static async Task<UserRole?> ActorRoleAsync(IApplicationDbContext db, ICurrentUserService current, CancellationToken ct)
    {
        if (current.UserId is not > 0 || !Enum.TryParse<UserRole>(current.Role, out var claimedRole)
            || !claimedRole.IsAdministrationRole()) return null;
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == current.UserId, ct);
        return actor is { Status: AccountStatus.Active } && actor.Role == claimedRole ? actor.Role : null;
    }

    public static IQueryable<User> Scope(IApplicationDbContext db, UserRole role) =>
        role == UserRole.Staff
            ? db.Users.Where(user => user.Role == UserRole.Traveler || user.Role == UserRole.TourOperator
                || user.Role == UserRole.ServiceProvider)
            : db.Users.Where(user => user.Role == UserRole.Staff);

    public static readonly Expression<Func<User, UserAccountDto>> Projection = user => new UserAccountDto(
        user.Id.ToString(), user.FullName, user.Email, user.Role.ToString(), user.Status.ToString(),
        user.CreatedAtUtc.UtcDateTime, user.LastLoginAtUtc.HasValue ? user.LastLoginAtUtc.Value.UtcDateTime : null);

    public static UserAccountDto Map(User user) => new(user.Id.ToString(CultureInfo.InvariantCulture),
        user.FullName, user.Email, user.Role.ToString(), user.Status.ToString(), user.CreatedAtUtc.UtcDateTime,
        user.LastLoginAtUtc?.UtcDateTime);
}

public sealed class ListUserAccountsQueryHandler(IApplicationDbContext db, ICurrentUserService current)
    : IRequestHandler<ListUserAccountsQuery, Result<UserAccountPageDto>>
{
    public async Task<Result<UserAccountPageDto>> Handle(ListUserAccountsQuery request, CancellationToken ct)
    {
        var role = await UserAccountAccess.ActorRoleAsync(db, current, ct);
        if (role is null) return Result.Failure<UserAccountPageDto>(UserAccountErrors.Forbidden, "Access denied.");
        if (request.Page < 1 || request.PageSize is < 1 or > 100 || request.Search?.Length > 256
            || (request.RegisteredFrom.HasValue && request.RegisteredTo.HasValue && request.RegisteredFrom > request.RegisteredTo))
            return Result.Failure<UserAccountPageDto>(UserAccountErrors.InvalidRequest, "Invalid account filters.");
        var query = UserAccountAccess.Scope(db, role.Value).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            if (!Enum.TryParse<UserRole>(request.Role, out var targetRole) || !Enum.IsDefined(targetRole))
                return Result.Failure<UserAccountPageDto>(UserAccountErrors.InvalidRequest, "Invalid role filter.");
            query = query.Where(user => user.Role == targetRole);
        }
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (!Enum.TryParse<AccountStatus>(request.Status, out var status) || !Enum.IsDefined(status))
                return Result.Failure<UserAccountPageDto>(UserAccountErrors.InvalidRequest, "Invalid status filter.");
            query = query.Where(user => user.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(user => user.FullName.Contains(search) || (user.Email != null && user.Email.Contains(search)));
        }
        if (request.RegisteredFrom.HasValue) query = query.Where(user => user.CreatedAtUtc >= request.RegisteredFrom.Value);
        if (request.RegisteredTo.HasValue) query = query.Where(user => user.CreatedAtUtc <= request.RegisteredTo.Value);
        var count = await query.CountAsync(ct);
        var offset = ((long)request.Page - 1) * request.PageSize;
        var items = offset > int.MaxValue ? [] : await query.OrderByDescending(user => user.CreatedAtUtc)
            .ThenByDescending(user => user.Id).Skip((int)offset).Take(request.PageSize).Select(UserAccountAccess.Projection).ToListAsync(ct);
        return Result.Success(new UserAccountPageDto(request.Page, request.PageSize, count,
            (int)Math.Ceiling(count / (double)request.PageSize), items));
    }
}

public sealed class GetUserAccountQueryHandler(IApplicationDbContext db, ICurrentUserService current)
    : IRequestHandler<GetUserAccountQuery, Result<UserAccountDetailDto>>
{
    public async Task<Result<UserAccountDetailDto>> Handle(GetUserAccountQuery request, CancellationToken ct)
    {
        var role = await UserAccountAccess.ActorRoleAsync(db, current, ct);
        if (role is null) return Result.Failure<UserAccountDetailDto>(UserAccountErrors.Forbidden, "Access denied.");
        var account = await UserAccountAccess.Scope(db, role.Value).AsNoTracking()
            .Where(user => user.Id == request.UserId).Select(UserAccountAccess.Projection).SingleOrDefaultAsync(ct);
        if (account is null) return Result.Failure<UserAccountDetailDto>(UserAccountErrors.NotFound, "The account is unavailable.");
        var history = await db.AuditLogs.AsNoTracking().Where(row => row.AffectedEntity == AuditEntityTypes.User
                && row.AffectedEntityId == request.UserId && row.Result == AuditOutcome.Success
                && (row.ActionType == AuditActionTypes.UserAccountLocked || row.ActionType == AuditActionTypes.UserAccountUnlocked))
            .OrderByDescending(row => row.CreatedAtUtc).ThenByDescending(row => row.Id)
            .Select(row => new AccountModerationDto(row.ActionType, row.Reason, row.CreatedAtUtc.UtcDateTime)).ToListAsync(ct);
        var bookingCount = await db.Bookings.AsNoTracking().CountAsync(booking => booking.TravelerUserId == request.UserId, ct);
        return Result.Success(new UserAccountDetailDto(account, bookingCount, history));
    }
}

public sealed class ModerateUserAccountCommandHandler(IApplicationDbContext db, ICurrentUserService current,
    IDateTimeProvider clock, IAuditFailureRecorder failures)
    : IRequestHandler<ModerateUserAccountCommand, Result<UserAccountDto>>
{
    public async Task<Result<UserAccountDto>> Handle(ModerateUserAccountCommand request, CancellationToken ct)
    {
        var action = request.Lock ? AuditActionTypes.UserAccountLocked : AuditActionTypes.UserAccountUnlocked;
        var role = await UserAccountAccess.ActorRoleAsync(db, current, ct);
        if (role is null) return await Fail(UserAccountErrors.Forbidden, "Access denied.");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 200)
            return await Fail(UserAccountErrors.InvalidRequest, "A reason of at most 200 characters is required.");
        try
        {
            var result = await db.ExecuteInSerializableTransactionAsync(async transactionCt =>
            {
                if (await UserAccountAccess.ActorRoleAsync(db, current, transactionCt) != role)
                    return Result.Failure<UserAccountDto>(UserAccountErrors.Forbidden, "Access denied.");
                var user = await UserAccountAccess.Scope(db, role.Value).SingleOrDefaultAsync(user => user.Id == request.UserId, transactionCt);
                if (user is null) return Result.Failure<UserAccountDto>(UserAccountErrors.NotFound, "The account is unavailable.");
                if (user.Status == AccountStatus.Inactive || request.Lock == (user.Status == AccountStatus.Locked))
                    return Result.Failure<UserAccountDto>(UserAccountErrors.Conflict, "The account state has changed.");
                if (!request.Lock && user.Role == UserRole.TourOperator && await db.TourOperatorDebts.AsNoTracking()
                    .AnyAsync(debt => debt.TourOperatorUserId == user.Id && debt.RecoveredAmount < debt.Amount, transactionCt))
                    return Result.Failure<UserAccountDto>(UserAccountErrors.OutstandingDebt, "The outstanding debt must be recovered before unlocking.");
                if (!request.Lock && user.Role == UserRole.ServiceProvider && user.StatusBeforeLock is null)
                    return Result.Failure<UserAccountDto>(UserAccountErrors.ProviderSettlementUnavailable,
                        "Provider commission settlement must be verified before unlocking.");
                var now = clock.UtcNow.ToUniversalTime();
                var before = user.Status;
                if (request.Lock)
                {
                    user.Lock(request.Reason.Trim(), now);
                    var tokens = await db.RefreshTokens.Where(token => token.UserId == user.Id && token.RevokedAtUtc == null).ToListAsync(transactionCt);
                    foreach (var token in tokens) token.RevokedAtUtc = now;
                }
                else user.Unlock(now);
                db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(current.UserId, action, AuditEntityTypes.User, user.Id,
                    now, AuditOutcome.Success, reason: request.Reason.Trim(),
                    beforeData: JsonSerializer.Serialize(new { Status = before.ToString() }),
                    afterData: JsonSerializer.Serialize(new { Status = user.Status.ToString(), Role = user.Role.ToString() })));
                db.Notifications.Add(new Notification { User = user, Channel = NotificationChannel.InApp,
                    Type = request.Lock ? "AccountLocked" : "AccountUnlocked", Title = "Account access updated",
                    Body = request.Lock ? "Your account has been locked. Contact support for assistance." : "Your account has been unlocked.",
                    Status = NotificationStatus.Pending, CreatedAtUtc = now });
                await db.SaveChangesAsync(transactionCt);
                return Result.Success(UserAccountAccess.Map(user));
            }, ct);
            return result.IsFailure ? await Fail(result.ErrorCode!, result.ErrorMessage!) : result;
        }
        catch (DbUpdateConcurrencyException)
        {
            return await Fail(UserAccountErrors.Conflict, "The account state has changed.");
        }

        async Task<Result<UserAccountDto>> Fail(string code, string message)
        {
            if (current.UserId is > 0) await failures.RecordAsync(new(current.UserId.Value, action, AuditEntityTypes.User,
                request.UserId > 0 ? request.UserId : null, code));
            return Result.Failure<UserAccountDto>(code, message);
        }
    }
}
