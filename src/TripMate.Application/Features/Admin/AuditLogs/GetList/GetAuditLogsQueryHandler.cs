using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.AuditLogs.Common;

namespace TripMate.Application.Features.Admin.AuditLogs.GetList;

public class GetAuditLogsQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService)
    : IRequestHandler<GetAuditLogsQuery, Result<PaginatedList<AuditLogSummaryDto>>>
{
    private static readonly TimeSpan VietnamUtcOffset = TimeSpan.FromHours(7);

    public async Task<Result<PaginatedList<AuditLogSummaryDto>>> Handle(
        GetAuditLogsQuery request,
        CancellationToken cancellationToken)
    {
        // 1. Authorization check (SRS 3.1.3 / CR-11, locked MSG126)
        if (currentUserService.UserId is null || currentUserService.Role != "Administrator")
        {
            return Result.Failure<PaginatedList<AuditLogSummaryDto>>(
                AuditLogErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        // 3. Base Query with LEFT JOIN to Users (handles system actions & missing users safely)
        var query = dbContext.AuditLogs
            .AsNoTracking()
            .Include(a => a.ActorUser)
            .AsQueryable();

        // 4. Apply ActionType filter
        if (!string.IsNullOrWhiteSpace(request.ActionType))
        {
            query = query.Where(a => a.ActionType == request.ActionType);
        }

        // 5. Apply ActorRole filter
        if (request.ActorRole.HasValue)
        {
            query = query.Where(a => a.ActorUser != null && a.ActorUser.Role == request.ActorRole.Value);
        }

        // 6. Apply AffectedEntity filter
        if (!string.IsNullOrWhiteSpace(request.AffectedEntity))
        {
            query = query.Where(a => a.AffectedEntity == request.AffectedEntity);
        }

        // 7. Apply Date Range filters
        if (request.FromDateUtc.HasValue)
        {
            query = query.Where(a => a.CreatedAtUtc >= request.FromDateUtc.Value);
        }

        if (request.ToDateUtc.HasValue)
        {
            query = query.Where(a => a.CreatedAtUtc <= request.ToDateUtc.Value);
        }

        // 8. Keyword search across Actor (Email, FullName), ActionType, AffectedEntity, and
        //    AffectedEntityId. Numeric keywords match the entity id exactly (sargable — the
        //    approved long.TryParse approach; never converts the column for LIKE).
        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var kw = request.Keyword.Trim();
            var isNumericKeyword = long.TryParse(kw, out var keywordEntityId);

            query = query.Where(a =>
                (a.ActorUser != null && (
                    EF.Functions.Like(a.ActorUser.Email, $"%{kw}%") ||
                    EF.Functions.Like(a.ActorUser.FullName, $"%{kw}%"))) ||
                EF.Functions.Like(a.ActionType, $"%{kw}%") ||
                EF.Functions.Like(a.AffectedEntity, $"%{kw}%") ||
                (isNumericKeyword && a.AffectedEntityId == keywordEntityId));
        }

        // 9. Order descending by timestamp (BR-52 / PC-01), id as tie-breaker so pagination
        //    is deterministic when entries share the same CreatedAtUtc.
        query = query
            .OrderByDescending(a => a.CreatedAtUtc)
            .ThenByDescending(a => a.Id);

        // 10. Execute count & paginated fetch
        var totalCount = await query.CountAsync(cancellationToken);

        var pageNumber = request.PageNumber < 1 ? 1 : request.PageNumber;
        var pageSize = request.PageSize < 1 ? 20 : (request.PageSize > 100 ? 100 : request.PageSize);

        var logs = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // 11. Project DTO with CR-07 timezone conversion (Asia/Ho_Chi_Minh UTC+7)
        var dtoItems = logs.Select(a => new AuditLogSummaryDto(
            a.Id,
            a.ActionType,
            a.ActorUserId,
            a.ActorUser?.Email,
            a.ActorUser?.FullName ?? "System",
            a.ActorUser?.Role,
            a.AffectedEntity,
            a.AffectedEntityId,
            a.IpAddress,
            a.CreatedAtUtc,
            a.CreatedAtUtc.ToOffset(VietnamUtcOffset).ToString("dd/MM/yyyy HH:mm:ss"),
            a.Result?.ToString()
        )).ToList();

        var result = new PaginatedList<AuditLogSummaryDto>(dtoItems, totalCount, pageNumber, pageSize);
        return Result.Success(result);
    }
}