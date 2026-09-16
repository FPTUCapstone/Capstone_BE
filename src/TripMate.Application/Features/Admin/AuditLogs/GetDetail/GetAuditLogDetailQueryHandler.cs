using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.AuditLogs.Common;

namespace TripMate.Application.Features.Admin.AuditLogs.GetDetail;

public class GetAuditLogDetailQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService)
    : IRequestHandler<GetAuditLogDetailQuery, Result<AuditLogDetailDto>>
{
    private static readonly TimeSpan VietnamUtcOffset = TimeSpan.FromHours(7);

    public async Task<Result<AuditLogDetailDto>> Handle(
        GetAuditLogDetailQuery request,
        CancellationToken cancellationToken)
    {
        // 1. Authorization check (BR-115 / MSG126)
        if (currentUserService.UserId is null || currentUserService.Role != "Administrator")
        {
            return Result.Failure<AuditLogDetailDto>(
                AuditLogErrorCodes.Forbidden,
                "Access denied. Administrator role required.");
        }

        // 2. Fetch single audit log with ActorUser included
        var auditLog = await dbContext.AuditLogs
            .AsNoTracking()
            .Include(a => a.ActorUser)
            .FirstOrDefaultAsync(a => a.Id == request.Id, cancellationToken);

        // 3. Not found handling (MSG129)
        if (auditLog == null)
        {
            return Result.Failure<AuditLogDetailDto>(
                AuditLogErrorCodes.NotFound,
                "System audit log entry not found.");
        }

        // 4. Map DTO with CR-07 timezone conversion (Asia/Ho_Chi_Minh UTC+7)
        var dto = new AuditLogDetailDto(
            auditLog.Id,
            auditLog.ActionType,
            auditLog.ActorUserId,
            auditLog.ActorUser?.Email,
            auditLog.ActorUser?.FullName ?? "System",
            auditLog.ActorUser?.Role,
            auditLog.AffectedEntity,
            auditLog.AffectedEntityId,
            auditLog.BeforeData,
            auditLog.AfterData,
            auditLog.IpAddress,
            auditLog.CreatedAtUtc,
            auditLog.CreatedAtUtc.ToOffset(VietnamUtcOffset).ToString("dd/MM/yyyy HH:mm:ss")
        );

        return Result.Success(dto);
    }
}