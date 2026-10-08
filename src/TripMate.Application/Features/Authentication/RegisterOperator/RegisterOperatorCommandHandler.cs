using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.RegisterOperator;

public sealed class RegisterOperatorCommandHandler(
    IApplicationDbContext db,
    IFirebaseAuthService firebase,
    IPasswordHasherService passwordHasher,
    IDateTimeProvider clock,
    IOperatorDocumentStorage storage,
    IOperatorDocumentCleanupJournal cleanup,
    IOperatorRegistrationConstraintClassifier constraints,
    ILogger<RegisterOperatorCommandHandler> logger)
    : IRequestHandler<RegisterOperatorCommand, Result<RegisterOperatorResponse>>
{
    private const string UnavailableMessage = "TripMate is temporarily unable to process your request. Please try again.";
    private static readonly TimeSpan CleanupReservationGracePeriod = TimeSpan.FromMinutes(30);
    public async Task<Result<RegisterOperatorResponse>> Handle(
        RegisterOperatorCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FirebaseIdToken))
        {
            return Result.Failure<RegisterOperatorResponse>(AuthErrorCodes.AuthTokenMissing,
                "Firebase ID token is required.");
        }

        FirebaseTokenValidationResult token;
        var uploaded = new List<(string PublicId, string ContentType)>();
        try
        {
            token = await firebase.VerifyIdTokenAsync(request.FirebaseIdToken, cancellationToken);
        }
        catch (FirebaseUnavailableException exception)
        {
            logger.LogWarning(exception, "Firebase unavailable during operator registration.");
            return Unavailable();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Firebase rejected operator registration token.");
            return Result.Failure<RegisterOperatorResponse>(AuthErrorCodes.AuthTokenInvalid,
                "Invalid or expired Firebase authentication token.");
        }

        var email = request.Email.Trim().ToLowerInvariant();
        if (!email.Equals(token.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<RegisterOperatorResponse>(AuthErrorCodes.AuthEmailMismatch,
                "Registration email does not match the email in Firebase token.");
        }

        var taxCode = request.TaxCode.Trim();
        var licence = request.BusinessLicenseNo.Trim();
        try
        {
            var existing = await db.Users
                .Where(user => user.Email == email)
                .Select(user => new { user.Role, user.Status })
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                var pendingApplication = existing.Role == UserRole.TourOperator &&
                    existing.Status == AccountStatus.PendingApproval &&
                    await IsPendingApplicationAsync(email, cancellationToken);
                return pendingApplication
                    ? PendingApplication()
                    : DuplicateEmail();
            }

            if (await db.OperatorProfiles.AnyAsync(profile =>
                    profile.TaxCode == taxCode || profile.BusinessLicenseNo == licence,
                    cancellationToken))
            {
                return DuplicateBusinessIdentifier();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return LogAndReturnUnavailable(exception);
        }

        var now = clock.UtcNow;
        var user = new User
        {
            Email = email,
            FullName = request.ContactPerson.Trim(),
            PasswordHash = passwordHasher.Hash(request.Password),
            Role = UserRole.TourOperator,
            Status = AccountStatus.PendingApproval,
            EmailVerifiedAtUtc = null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var profile = new OperatorProfile
        {
            User = user,
            CompanyName = request.CompanyName.Trim(),
            TaxCode = taxCode,
            BusinessLicenseNo = licence,
            ContactAddress = string.IsNullOrWhiteSpace(request.BusinessAddress)
                ? null : request.BusinessAddress.Trim(),
            ContactPhone = string.IsNullOrWhiteSpace(request.ContactPhone)
                ? null : request.ContactPhone.Trim(),
            ApprovalStatus = OperatorApprovalStatus.PendingApproval,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        var documents = new List<(OperatorRegistrationDocument Document, OperatorDocumentType Type)>
        {
            (request.BusinessLicenseDocument!, OperatorDocumentType.BusinessLicense),
        };
        if (request.SupportingDocuments is not null)
        {
            documents.AddRange(request.SupportingDocuments.Select(document =>
                (document, OperatorDocumentType.Other)));
        }

        try
        {
            foreach (var (document, type) in documents)
            {
                var publicId = storage.AllocatePublicId();
                if (document.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
                {
                    // Cloudinary raw public IDs include the extension. Track that exact ID
                    // so compensation deletes the same object on later failure.
                    publicId += ".pdf";
                }
                // A separate SQL commit must precede provider I/O. If this process stops after
                // the remote write, the worker can recover the durable reservation.
                await cleanup.ReserveAsync(publicId, document.ContentType,
                    clock.UtcNow + CleanupReservationGracePeriod, cancellationToken);
                // Track before calling the provider: an exception may follow a successful remote write.
                uploaded.Add((publicId, document.ContentType));
                var response = await storage.UploadAsync(
                    new OperatorDocumentStorageUpload(publicId, document.ContentType, document.Bytes),
                    cancellationToken);
                if (!response.IsSuccess)
                {
                    await CompensateAsync(uploaded);
                    return Unavailable();
                }

                profile.Documents.Add(new OperatorDocument
                {
                    OperatorProfile = profile,
                    DocumentType = type,
                    FileUrl = response.DeliveryUrl!.AbsoluteUri,
                    Status = DocumentStatus.Submitted,
                    UploadedAtUtc = now,
                });
            }

            var responseValue = await db.ExecuteInTransactionAsync(async transactionToken =>
            {
                db.Users.Add(user);
                db.OperatorProfiles.Add(profile);
                await db.SaveChangesAsync(transactionToken);
                return new RegisterOperatorResponse(
                    user.Id, nameof(OperatorApprovalStatus.PendingApproval), AuthErrorCodes.Msg08);
            }, cancellationToken);
            foreach (var (publicId, _) in uploaded)
            {
                try
                {
                    await cleanup.CompleteAsync(publicId, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    // The registration is committed. The worker checks the persisted
                    // document reference before deleting an expired reservation.
                    logger.LogError(exception,
                        "Could not close committed operator document reservation {PublicId}.", publicId);
                }
            }
            return Result.Success(responseValue);
        }
        catch (DbUpdateException exception)
        {
            db.ClearTrackedEntities();
            await CompensateAsync(uploaded);
            switch (constraints.Classify(exception))
            {
                case OperatorRegistrationConstraint.Email:
                    try
                    {
                        return await IsPendingApplicationAsync(email, cancellationToken)
                            ? PendingApplication()
                            : DuplicateEmail();
                    }
                    catch (Exception lookupException) when (lookupException is not OperationCanceledException)
                    {
                        return LogAndReturnUnavailable(lookupException);
                    }
                case OperatorRegistrationConstraint.TaxCodeOrBusinessLicense:
                    return DuplicateBusinessIdentifier();
                default:
                    return LogAndReturnUnavailable(exception);
            }
        }
        catch (OperationCanceledException)
        {
            db.ClearTrackedEntities();
            await CompensateAsync(uploaded);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ClearTrackedEntities();
            await CompensateAsync(uploaded);
            return LogAndReturnUnavailable(exception);
        }
    }

    private Result<RegisterOperatorResponse> LogAndReturnUnavailable(Exception exception)
    {
        logger.LogError(exception, "Operator registration storage or persistence failed.");
        return Unavailable();
    }

    private Task<bool> IsPendingApplicationAsync(string email, CancellationToken cancellationToken) =>
        db.OperatorProfiles.AnyAsync(profile =>
            profile.User.Email == email &&
            profile.User.Role == UserRole.TourOperator &&
            profile.User.Status == AccountStatus.PendingApproval &&
            profile.ApprovalStatus == OperatorApprovalStatus.PendingApproval,
            cancellationToken);

    private async Task CompensateAsync(List<(string PublicId, string ContentType)> uploaded)
    {
        foreach (var (publicId, contentType) in uploaded.AsEnumerable().Reverse())
        {
            try
            {
                string reference = OperatorDocumentReference.Create(publicId, contentType).AbsoluteUri;
                if (await db.OperatorDocuments.AsNoTracking().AnyAsync(
                        document => document.FileUrl == reference, CancellationToken.None))
                {
                    // A commit acknowledgment may have been lost after SQL committed.
                    // Never compensate a document that now belongs to a saved profile.
                    await cleanup.CompleteAsync(publicId, CancellationToken.None);
                    continue;
                }
            }
            catch (Exception exception)
            {
                // An unavailable DB cannot prove this asset is orphaned. The worker
                // will check again later rather than risking deletion of a live file.
                logger.LogError(exception,
                    "Could not determine operator document ownership for {PublicId}.", publicId);
                await ExpediteCleanupAsync(publicId);
                continue;
            }

            try
            {
                var result = await storage.DeleteAsync(publicId, contentType, CancellationToken.None);
                if (result.Outcome is OperatorDocumentStorageDeleteOutcome.Deleted or
                    OperatorDocumentStorageDeleteOutcome.AlreadyAbsent)
                {
                    await cleanup.CompleteAsync(publicId, CancellationToken.None);
                    continue;
                }
                else
                {
                    logger.LogError("Failed to compensate operator document {PublicId}: {Code}",
                        publicId, result.SafeErrorCode);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to compensate operator document {PublicId}", publicId);
            }
            await ExpediteCleanupAsync(publicId);
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
            // The reservation remains durable and will become due after its grace period.
            logger.LogError(exception, "Could not expedite operator document cleanup {PublicId}", publicId);
        }
    }

    private static Result<RegisterOperatorResponse> Unavailable() =>
        Result.Failure<RegisterOperatorResponse>(AuthErrorCodes.Msg127, UnavailableMessage);

    private static Result<RegisterOperatorResponse> DuplicateEmail() =>
        Result.Failure<RegisterOperatorResponse>(AuthErrorCodes.Msg03,
            "An account with this email already exists. Please sign in or use another email.");

    private static Result<RegisterOperatorResponse> DuplicateBusinessIdentifier() =>
        Result.Failure<RegisterOperatorResponse>(AuthErrorCodes.Msg159,
            OperatorRegistrationMessages.BusinessIdentifierExists);

    private static Result<RegisterOperatorResponse> PendingApplication() =>
        Result.Failure<RegisterOperatorResponse>(AuthErrorCodes.Msg160,
            OperatorRegistrationMessages.PendingApplicationExists);
}