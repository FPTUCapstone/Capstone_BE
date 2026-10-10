using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.RegisterOperator;
using TripMate.Application.Features.Operator.Application.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Application.Resubmit;

public sealed class ResubmitOperatorApplicationCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    IOperatorDocumentStorage storage,
    IOperatorDocumentCleanupJournal cleanup,
    IOperatorRegistrationConstraintClassifier constraints,
    ILogger<ResubmitOperatorApplicationCommandHandler> logger)
    : IRequestHandler<ResubmitOperatorApplicationCommand, Result<ResubmitOperatorApplicationResponse>>
{
    private static readonly TimeSpan CleanupReservationGracePeriod = TimeSpan.FromMinutes(30);

    public async Task<Result<ResubmitOperatorApplicationResponse>> Handle(
        ResubmitOperatorApplicationCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long userId || currentUser.Role != "TourOperator")
        {
            return Result.Failure<ResubmitOperatorApplicationResponse>(
                OperatorApplicationErrorCodes.Forbidden,
                "Tour Operator access is required.");
        }

        var uploaded = new List<UploadedDocument>();
        try
        {
            var preflight = await db.OperatorProfiles.AsNoTracking()
                .Where(profile => profile.UserId == userId)
                .Select(profile => new
                {
                    profile.ApprovalStatus,
                    UserStatus = profile.User.Status,
                    HasBusinessLicense = profile.Documents.Any(document =>
                        document.DocumentType == OperatorDocumentType.BusinessLicense),
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (preflight is null)
            {
                return Result.Failure<ResubmitOperatorApplicationResponse>(
                    OperatorApplicationErrorCodes.NotFound,
                    "Tour Operator application not found.");
            }

            if (preflight.UserStatus != AccountStatus.Rejected ||
                preflight.ApprovalStatus != OperatorApprovalStatus.Rejected)
            {
                return NotRejected();
            }

            if (request.BusinessLicenseDocument is null && !preflight.HasBusinessLicense)
            {
                return Result.Failure<ResubmitOperatorApplicationResponse>(
                    OperatorApplicationErrorCodes.MissingBusinessLicense,
                    OperatorRegistrationMessages.BusinessLicenseDocumentRequired);
            }

            if (request.BusinessLicenseDocument is not null)
            {
                uploaded.Add(await UploadAsync(
                    request.BusinessLicenseDocument,
                    OperatorDocumentType.BusinessLicense,
                    cancellationToken));
            }

            if (request.SupportingDocuments is not null)
            {
                foreach (var document in request.SupportingDocuments)
                {
                    uploaded.Add(await UploadAsync(document, OperatorDocumentType.Other, cancellationToken));
                }
            }

            var result = await db.ExecuteInSerializableTransactionAsync(async transactionToken =>
            {
                var user = await db.Users.AsNoTracking()
                    .FirstOrDefaultAsync(candidate => candidate.Id == userId, transactionToken)
                    ?? throw new ApplicationMissingException();
                var beforeProfile = await db.OperatorProfiles.AsNoTracking()
                    .Include(profile => profile.Documents)
                    .FirstOrDefaultAsync(profile => profile.UserId == userId, transactionToken)
                    ?? throw new ApplicationMissingException();

                if (user.Status != AccountStatus.Rejected ||
                    beforeProfile.ApprovalStatus != OperatorApprovalStatus.Rejected)
                {
                    throw new ApplicationStateConflictException();
                }

                var taxCode = request.TaxCode.Trim();
                var licence = request.BusinessLicenseNo.Trim();
                var conflicts = await db.OperatorProfiles.AsNoTracking()
                    .Where(profile => profile.UserId != userId &&
                        (profile.TaxCode == taxCode || profile.BusinessLicenseNo == licence))
                    .Select(profile => new
                    {
                        HasTaxConflict = profile.TaxCode == taxCode,
                        HasLicenceConflict = profile.BusinessLicenseNo == licence,
                    })
                    .ToListAsync(transactionToken);

                if (conflicts.Count > 0)
                {
                    var taxConflict = conflicts.Any(c => c.HasTaxConflict);
                    var licenceConflict = conflicts.Any(c => c.HasLicenceConflict);
                    throw new DuplicateIdentifierException(taxConflict, licenceConflict);
                }

                var now = clock.UtcNow;
                var beforeSnapshot = JsonSerializer.Serialize(new
                {
                    UserStatus = user.Status.ToString(),
                    ApprovalStatus = beforeProfile.ApprovalStatus.ToString(),
                    beforeProfile.RejectionReason,
                    beforeProfile.ReviewedBy,
                    beforeProfile.ReviewedAtUtc,
                    beforeProfile.CompanyName,
                    beforeProfile.TaxCode,
                    beforeProfile.BusinessLicenseNo,
                    beforeProfile.ContactAddress,
                    beforeProfile.ContactPhone,
                    ContactPerson = user.FullName,
                    Documents = beforeProfile.Documents.Select(document => new
                    {
                        document.Id,
                        DocumentType = document.DocumentType.ToString(),
                        Status = document.Status.ToString(),
                    }),
                });

                var profileAffected = await db.OperatorProfiles
                    .Where(profile => profile.UserId == userId &&
                        profile.ApprovalStatus == OperatorApprovalStatus.Rejected)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(profile => profile.CompanyName, request.CompanyName.Trim())
                        .SetProperty(profile => profile.TaxCode, taxCode)
                        .SetProperty(profile => profile.BusinessLicenseNo, licence)
                        .SetProperty(profile => profile.ContactAddress,
                            string.IsNullOrWhiteSpace(request.BusinessAddress)
                                ? null : request.BusinessAddress.Trim())
                        .SetProperty(profile => profile.ContactPhone,
                            string.IsNullOrWhiteSpace(request.ContactPhone)
                                ? null : request.ContactPhone.Trim())
                        .SetProperty(profile => profile.ApprovalStatus,
                            OperatorApprovalStatus.PendingApproval)
                        .SetProperty(profile => profile.RejectionReason, (string?)null)
                        .SetProperty(profile => profile.ReviewedBy, (long?)null)
                        .SetProperty(profile => profile.ReviewedAtUtc, (DateTimeOffset?)null)
                        .SetProperty(profile => profile.UpdatedAtUtc, now),
                        transactionToken);

                var userAffected = await db.Users
                    .Where(candidate => candidate.Id == userId &&
                        candidate.Role == UserRole.TourOperator &&
                        candidate.Status == AccountStatus.Rejected)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(candidate => candidate.FullName, request.ContactPerson.Trim())
                        .SetProperty(candidate => candidate.Status, AccountStatus.PendingApproval)
                        .SetProperty(candidate => candidate.UpdatedAtUtc, now),
                        transactionToken);

                if (profileAffected != 1 || userAffected != 1)
                {
                    throw new ApplicationStateConflictException();
                }

                var profile = await db.OperatorProfiles
                    .Include(candidate => candidate.Documents)
                    .FirstAsync(candidate => candidate.UserId == userId, transactionToken);

                if (request.BusinessLicenseDocument is null)
                {
                    var retainedLicense = profile.Documents
                        .Where(document => document.DocumentType == OperatorDocumentType.BusinessLicense)
                        .OrderByDescending(document => document.UploadedAtUtc)
                        .ThenByDescending(document => document.Id)
                        .FirstOrDefault() ?? throw new MissingBusinessLicenseException();
                    retainedLicense.Status = DocumentStatus.Submitted;
                }

                foreach (var document in uploaded)
                {
                    profile.Documents.Add(new OperatorDocument
                    {
                        OperatorUserId = userId,
                        DocumentType = document.Type,
                        FileUrl = document.StoredReference,
                        Status = DocumentStatus.Submitted,
                        UploadedAtUtc = now,
                    });
                }

                var afterSnapshot = JsonSerializer.Serialize(new
                {
                    UserStatus = AccountStatus.PendingApproval.ToString(),
                    ApprovalStatus = OperatorApprovalStatus.PendingApproval.ToString(),
                    RejectionReason = (string?)null,
                    ReviewedBy = (long?)null,
                    ReviewedAtUtc = (DateTimeOffset?)null,
                    CompanyName = request.CompanyName.Trim(),
                    TaxCode = taxCode,
                    BusinessLicenseNo = licence,
                    ContactAddress = string.IsNullOrWhiteSpace(request.BusinessAddress)
                        ? null : request.BusinessAddress.Trim(),
                    ContactPhone = string.IsNullOrWhiteSpace(request.ContactPhone)
                        ? null : request.ContactPhone.Trim(),
                    ContactPerson = request.ContactPerson.Trim(),
                    Documents = profile.Documents.Select(document => new
                    {
                        document.Id,
                        DocumentType = document.DocumentType.ToString(),
                        Status = document.Status.ToString(),
                    }),
                });

                db.AuditLogs.Add(AuditLog.CreateOperatorApplicationResubmitted(
                    userId, beforeSnapshot, afterSnapshot, now));
                await db.SaveChangesAsync(transactionToken);
                await db.FinalizeOperatorDocumentCleanupReservationsAsync(
                    uploaded.Select(document => document.PublicId).ToArray(), transactionToken);

                var previousCount = await db.AuditLogs.AsNoTracking().CountAsync(
                    audit => audit.ActionType == AuditActionTypes.OperatorApplicationResubmit &&
                        audit.AffectedEntityId == userId,
                    transactionToken);

                return new ResubmitOperatorApplicationResponse(
                    userId,
                    AccountStatus.PendingApproval.ToString(),
                    OperatorApprovalStatus.PendingApproval.ToString(),
                    now,
                    "MSG162",
                    OperatorApplicationMessages.Success,
                    previousCount);
            }, cancellationToken);

            uploaded.Clear();
            return Result.Success(result);
        }
        catch (ApplicationMissingException)
        {
            await CompensateAsync(uploaded);
            return Result.Failure<ResubmitOperatorApplicationResponse>(
                OperatorApplicationErrorCodes.NotFound,
                "Tour Operator application not found.");
        }
        catch (ApplicationStateConflictException)
        {
            db.ClearTrackedEntities();
            await CompensateAsync(uploaded);
            return NotRejected();
        }
        catch (MissingBusinessLicenseException)
        {
            db.ClearTrackedEntities();
            await CompensateAsync(uploaded);
            return Result.Failure<ResubmitOperatorApplicationResponse>(
                OperatorApplicationErrorCodes.MissingBusinessLicense,
                OperatorRegistrationMessages.BusinessLicenseDocumentRequired);
        }
        catch (DuplicateIdentifierException exception)
        {
            await CompensateAsync(uploaded);
            return DuplicateIdentifier(exception.TaxCodeConflict, exception.BusinessLicenseConflict);
        }
        catch (DbUpdateException exception)
        {
            db.ClearTrackedEntities();
            await CompensateAsync(uploaded);
            return constraints.Classify(exception) == OperatorRegistrationConstraint.TaxCodeOrBusinessLicense
                ? DuplicateIdentifier(true, true)
                : LogUnavailable(exception);
        }
        catch (OperationCanceledException)
        {
            db.ClearTrackedEntities();
            await CompensateAsync(uploaded);
            throw;
        }
        catch (Exception exception)
        {
            db.ClearTrackedEntities();
            await CompensateAsync(uploaded);
            return LogUnavailable(exception);
        }
    }

    private async Task<UploadedDocument> UploadAsync(
        OperatorRegistrationDocument document,
        OperatorDocumentType type,
        CancellationToken cancellationToken)
    {
        var publicId = storage.AllocatePublicId();
        if (document.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            publicId += ".pdf";
        }

        await cleanup.ReserveAsync(publicId, document.ContentType,
            clock.UtcNow + CleanupReservationGracePeriod, cancellationToken);
        var result = await storage.UploadAsync(
            new OperatorDocumentStorageUpload(publicId, document.ContentType, document.Bytes),
            cancellationToken);
        if (!result.IsSuccess)
        {
            await ExpediteCleanupAsync(publicId);
            throw new DocumentUploadException();
        }

        return new UploadedDocument(publicId, document.ContentType,
            result.DeliveryUrl!.AbsoluteUri, type);
    }

    private async Task CompensateAsync(List<UploadedDocument> uploaded)
    {
        foreach (var document in uploaded.AsEnumerable().Reverse())
        {
            try
            {
                if (await db.OperatorDocuments.AsNoTracking().AnyAsync(
                        entity => entity.FileUrl == document.StoredReference,
                        CancellationToken.None))
                {
                    await cleanup.CompleteAsync(document.PublicId, CancellationToken.None);
                    continue;
                }

                var deletion = await storage.DeleteAsync(
                    document.PublicId, document.ContentType, CancellationToken.None);
                if (deletion.Outcome is OperatorDocumentStorageDeleteOutcome.Deleted or
                    OperatorDocumentStorageDeleteOutcome.AlreadyAbsent)
                {
                    await cleanup.CompleteAsync(document.PublicId, CancellationToken.None);
                    continue;
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Failed to compensate resubmitted operator document {PublicId}.",
                    document.PublicId);
            }

            await ExpediteCleanupAsync(document.PublicId);
        }

        uploaded.Clear();
    }

    private async Task ExpediteCleanupAsync(string publicId)
    {
        try
        {
            await cleanup.RetryNowAsync(publicId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Could not expedite resubmitted operator document cleanup {PublicId}.", publicId);
        }
    }

    private Result<ResubmitOperatorApplicationResponse> LogUnavailable(Exception exception)
    {
        logger.LogError(exception, "Operator application resubmission failed.");
        return Result.Failure<ResubmitOperatorApplicationResponse>(
            OperatorApplicationErrorCodes.Unavailable,
            OperatorApplicationMessages.Unavailable);
    }

    private static Result<ResubmitOperatorApplicationResponse> NotRejected() =>
        Result.Failure<ResubmitOperatorApplicationResponse>(
            OperatorApplicationErrorCodes.NotRejected,
            OperatorApplicationMessages.NotRejected);

    private static Result<ResubmitOperatorApplicationResponse> DuplicateIdentifier(
        bool taxCodeConflict = true,
        bool businessLicenseConflict = true)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (taxCodeConflict)
        {
            errors["taxCode"] = [OperatorApplicationErrorCodes.DuplicateIdentifier];
        }
        if (businessLicenseConflict)
        {
            errors["businessLicenseNo"] = [OperatorApplicationErrorCodes.DuplicateIdentifier];
        }

        return Result.Failure<ResubmitOperatorApplicationResponse>(
            OperatorApplicationErrorCodes.DuplicateIdentifier,
            OperatorRegistrationMessages.BusinessIdentifierExists,
            new Dictionary<string, object?> { ["errors"] = errors });
    }

    private sealed record UploadedDocument(
        string PublicId,
        string ContentType,
        string StoredReference,
        OperatorDocumentType Type);

    private sealed class ApplicationMissingException : Exception;
    private sealed class ApplicationStateConflictException : Exception;
    private sealed class DuplicateIdentifierException(bool taxCodeConflict, bool businessLicenseConflict) : Exception
    {
        public bool TaxCodeConflict { get; } = taxCodeConflict;
        public bool BusinessLicenseConflict { get; } = businessLicenseConflict;
    }
    private sealed class MissingBusinessLicenseException : Exception;
    private sealed class DocumentUploadException : Exception;
}