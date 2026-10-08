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
    IOperatorRegistrationConstraintClassifier constraints,
    ILogger<RegisterOperatorCommandHandler> logger)
    : IRequestHandler<RegisterOperatorCommand, Result<RegisterOperatorResponse>>
{
    private const string UnavailableMessage = "TripMate is temporarily unable to process your request. Please try again.";
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
                var result = await storage.DeleteAsync(publicId, contentType, CancellationToken.None);
                if (result.Outcome is OperatorDocumentStorageDeleteOutcome.TransientFailure or
                    OperatorDocumentStorageDeleteOutcome.PermanentFailure)
                {
                    logger.LogError("Failed to compensate operator document {PublicId}: {Code}",
                        publicId, result.SafeErrorCode);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to compensate operator document {PublicId}", publicId);
            }
        }

        uploaded.Clear();
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