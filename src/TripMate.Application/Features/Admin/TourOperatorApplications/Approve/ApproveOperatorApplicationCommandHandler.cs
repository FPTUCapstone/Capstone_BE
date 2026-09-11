using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.TourOperatorApplications.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.TourOperatorApplications.Approve;

public class ApproveOperatorApplicationCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<ApproveOperatorApplicationCommand, Result<ApproveOperatorApplicationResponseDto>>
{
    public async Task<Result<ApproveOperatorApplicationResponseDto>> Handle(
        ApproveOperatorApplicationCommand request,
        CancellationToken cancellationToken)
    {
        // 1. Authorization check
        if (currentUserService.UserId is null || currentUserService.Role != "Administrator")
        {
            return Result.Failure<ApproveOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.Forbidden,
                "Caller is not authorized to approve applications.");
        }

        var adminId = currentUserService.UserId.Value;

        // 2. Fetch target User (tracking enabled for state update)
        var user = await dbContext.Users
            .FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);

        if (user is null)
        {
            return Result.Failure<ApproveOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.NotFound,
                "Tour Operator application not found.");
        }

        if (user.Role != UserRole.TourOperator)
        {
            return Result.Failure<ApproveOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.WrongRole,
                "Target user is not a Tour Operator.");
        }

        if (user.Status != AccountStatus.PendingApproval)
        {
            return Result.Failure<ApproveOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.NotPending,
                "Application is no longer pending approval.");
        }

        // 3. Fetch OperatorProfile with Documents (tracking enabled)
        var profile = await dbContext.OperatorProfiles
            .Include(p => p.Documents)
            .FirstOrDefaultAsync(p => p.UserId == request.UserId, cancellationToken);

        if (profile is null)
        {
            return Result.Failure<ApproveOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.NotFound,
                "Operator profile not found.");
        }

        if (profile.ApprovalStatus != OperatorApprovalStatus.PendingApproval)
        {
            return Result.Failure<ApproveOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.NotPending,
                "Application is no longer pending approval.");
        }

        // 4. Validate profile mandatory fields
        if (string.IsNullOrWhiteSpace(profile.CompanyName) ||
            string.IsNullOrWhiteSpace(profile.TaxCode) ||
            string.IsNullOrWhiteSpace(profile.BusinessLicenseNo))
        {
            return Result.Failure<ApproveOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.Incomplete,
                "Required profile fields (Company Name, Tax Code, Business License) are incomplete.");
        }

        // 5. Validate mandatory document: only BusinessLicense is required
        //    (TaxCode text field is validated above; a separate TaxCode document upload is not
        //     required — per SRS §3.2.2 and Vietnamese business law, MST is embedded in the
        //     Business License / ĐKKD and is publicly verifiable via government registries.)
        var hasBusinessLicense = profile.Documents.Any(d =>
            d.DocumentType == OperatorDocumentType.BusinessLicense &&
            d.Status != DocumentStatus.Rejected);

        if (!hasBusinessLicense)
        {
            return Result.Failure<ApproveOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.DocumentInvalid,
                "The mandatory BusinessLicense document must be present and not rejected.");
        }

        var now = dateTimeProvider.UtcNow;

        // 6. Record before-snapshot for AuditLog
        var beforeSnapshot = JsonSerializer.Serialize(new
        {
            UserStatus = user.Status.ToString(),
            ApprovalStatus = profile.ApprovalStatus.ToString(),
            DocumentStatuses = profile.Documents.Select(d => new { d.Id, d.DocumentType, Status = d.Status.ToString() }),
        });

        // 7. Update User & Profile state
        user.Status = AccountStatus.Active;
        user.UpdatedAtUtc = now;

        profile.ApprovalStatus = OperatorApprovalStatus.Approved;
        profile.ReviewedBy = adminId;
        profile.ReviewedAtUtc = now;
        profile.RejectionReason = null;
        profile.UpdatedAtUtc = now;

        // 8. Update document statuses
        foreach (var doc in profile.Documents.Where(d => d.Status == DocumentStatus.Submitted))
        {
            doc.Status = DocumentStatus.Approved;
        }

        // 9. Record after-snapshot for AuditLog
        var afterSnapshot = JsonSerializer.Serialize(new
        {
            UserStatus = user.Status.ToString(),
            ApprovalStatus = profile.ApprovalStatus.ToString(),
            DocumentStatuses = profile.Documents.Select(d => new { d.Id, d.DocumentType, Status = d.Status.ToString() }),
        });

        // 10. Create AuditLog row
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = adminId,
            ActionType = "ApproveOperatorApplication",
            AffectedEntity = "OperatorProfile",
            AffectedEntityId = user.Id,
            BeforeData = beforeSnapshot,
            AfterData = afterSnapshot,
            CreatedAtUtc = now,
        });

        // 11. Create Notification row (MSG114 content resolved from dbContext.Messages)
        var msgRow = await dbContext.Messages
            .FirstOrDefaultAsync(m => m.MessageCode == TourOperatorApplicationMessages.CodeMSG114, cancellationToken);

        var messageText = TourOperatorApplicationMessages.FormatApproveSuccess(msgRow?.ContentTemplate, profile.CompanyName);

        dbContext.Notifications.Add(new Notification
        {
            User = user,
            Channel = NotificationChannel.Email,
            Type = "OperatorApplicationApproved",
            Title = "Tour Operator Application Approved",
            Body = messageText,
            RelatedEntityType = "OperatorProfile",
            RelatedEntityId = user.Id,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = now,
        });

        // 12. Single-transaction commit with concurrency protection
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ApproveOperatorApplicationResponseDto>(
                TourOperatorApplicationErrorCodes.NotPending,
                "Application is no longer pending approval.");
        }

        return Result.Success(new ApproveOperatorApplicationResponseDto(
            user.Id,
            user.Status,
            profile.ApprovalStatus,
            adminId,
            now,
            messageText));
    }
}
