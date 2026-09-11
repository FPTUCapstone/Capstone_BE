using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.TourOperatorApplications.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.TourOperatorApplications.Reject;

public class RejectOperatorApplicationCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<RejectOperatorApplicationCommand, Result<RejectOperatorApplicationResponseDto>>
{
    public async Task<Result<RejectOperatorApplicationResponseDto>> Handle(
        RejectOperatorApplicationCommand request,
        CancellationToken cancellationToken)
    {
        // 1. Authorization check
        if (currentUserService.UserId is null || currentUserService.Role != "Administrator")
        {
            return Result.Failure<RejectOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.Forbidden,
                "Caller is not authorized to reject applications.");
        }

        var adminId = currentUserService.UserId.Value;

        // 2. Input validation
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Result.Failure<RejectOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.RejectionReasonRequired,
                "Rejection reason is required.");
        }

        var trimmedReason = request.Reason.Trim();
        if (trimmedReason.Length > 1000)
        {
            return Result.Failure<RejectOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.RejectionReasonTooLong,
                "Rejection reason must not exceed 1000 characters.");
        }

        // 3. Fetch target User (tracking enabled for state update)
        var user = await dbContext.Users
            .FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);

        if (user is null)
        {
            return Result.Failure<RejectOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.NotFound,
                "Tour Operator application not found.");
        }

        if (user.Role != UserRole.TourOperator)
        {
            return Result.Failure<RejectOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.WrongRole,
                "Target user is not a Tour Operator.");
        }

        if (user.Status != AccountStatus.PendingApproval)
        {
            return Result.Failure<RejectOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.NotPending,
                "Application is no longer pending approval.");
        }

        // 4. Fetch OperatorProfile with Documents (tracking enabled)
        var profile = await dbContext.OperatorProfiles
            .Include(p => p.Documents)
            .FirstOrDefaultAsync(p => p.UserId == request.UserId, cancellationToken);

        if (profile is null)
        {
            return Result.Failure<RejectOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.NotFound,
                "Operator profile not found.");
        }

        if (profile.ApprovalStatus != OperatorApprovalStatus.PendingApproval)
        {
            return Result.Failure<RejectOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.NotPending,
                "Application is no longer pending approval.");
        }

        var now = dateTimeProvider.UtcNow;

        // 5. Record before-snapshot for AuditLog
        var beforeSnapshot = JsonSerializer.Serialize(new
        {
            UserStatus = user.Status.ToString(),
            ApprovalStatus = profile.ApprovalStatus.ToString(),
            DocumentStatuses = profile.Documents.Select(d => new { d.Id, d.DocumentType, Status = d.Status.ToString() }),
        });

        // 6. Update User & Profile state
        user.Status = AccountStatus.Rejected;
        user.UpdatedAtUtc = now;

        profile.ApprovalStatus = OperatorApprovalStatus.Rejected;
        profile.RejectionReason = trimmedReason;
        profile.ReviewedBy = adminId;
        profile.ReviewedAtUtc = now;
        profile.UpdatedAtUtc = now;

        // 7. Update submitted document statuses to Rejected
        foreach (var doc in profile.Documents.Where(d => d.Status == DocumentStatus.Submitted))
        {
            doc.Status = DocumentStatus.Rejected;
        }

        // 8. Record after-snapshot for AuditLog
        var afterSnapshot = JsonSerializer.Serialize(new
        {
            UserStatus = user.Status.ToString(),
            ApprovalStatus = profile.ApprovalStatus.ToString(),
            RejectionReason = trimmedReason,
            DocumentStatuses = profile.Documents.Select(d => new { d.Id, d.DocumentType, Status = d.Status.ToString() }),
        });

        // 9. Create AuditLog row
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = adminId,
            ActionType = "RejectOperatorApplication",
            AffectedEntity = "OperatorProfile",
            AffectedEntityId = user.Id,
            BeforeData = beforeSnapshot,
            AfterData = afterSnapshot,
            CreatedAtUtc = now,
        });

        // 10. Create Notification row
        var notificationBody = $"Tour Operator \"{profile.CompanyName}\" application was rejected. Reason: {trimmedReason}";

        dbContext.Notifications.Add(new Notification
        {
            User = user,
            Channel = NotificationChannel.Email,
            Type = "OperatorApplicationRejected",
            Title = "Tour Operator Application Rejected",
            Body = notificationBody,
            RelatedEntityType = "OperatorProfile",
            RelatedEntityId = user.Id,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = now,
        });

        // 11. Single-transaction commit with concurrency protection
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<RejectOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.NotPending,
                "Application is no longer pending approval.");
        }

        // 12. Resolve confirmation message (MSG116 resolved from dbContext.Messages)
        var msgRow = await dbContext.Messages
            .FirstOrDefaultAsync(m => m.MessageCode == TourOperatorApplicationMessages.CodeMSG116, cancellationToken);

        var responseMessage = !string.IsNullOrWhiteSpace(msgRow?.ContentTemplate)
            ? msgRow.ContentTemplate
            : TourOperatorApplicationMessages.DefaultRejectSuccess;

        return Result.Success(new RejectOperatorApplicationResponseDto(
            user.Id,
            user.Status,
            profile.ApprovalStatus,
            trimmedReason,
            adminId,
            now,
            responseMessage));
    }
}
