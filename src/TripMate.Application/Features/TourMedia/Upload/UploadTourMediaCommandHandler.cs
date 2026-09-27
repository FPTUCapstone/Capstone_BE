using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TourMedia.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using DomainTourMedia = TripMate.Domain.Entities.TourMedia;

namespace TripMate.Application.Features.TourMedia.Upload;

public sealed class UploadTourMediaCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider,
    ITourMediaImageInspector imageInspector,
    ITourMediaStorage mediaStorage,
    ITourMediaUploadLock uploadLock)
    : IRequestHandler<UploadTourMediaCommand, Result<TourMediaDto>>
{
    public const int MaximumActiveImages = 10;

    public async Task<Result<TourMediaDto>> Handle(
        UploadTourMediaCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        TourMediaImageInspectionResult inspection = await imageInspector.InspectAsync(
            command.Image,
            cancellationToken);
        if (!inspection.IsAccepted)
        {
            return Result.Failure<TourMediaDto>(
                TourMediaErrorCodes.InvalidRequest,
                "The uploaded image is invalid.");
        }

        var canonical = CanonicalUpload.From(command, inspection.Image!);
        ClaimResult claim = await ClaimAsync(canonical, cancellationToken);
        if (claim.Failure is not null)
        {
            return claim.Failure;
        }

        if (claim.Replay is not null)
        {
            return Result.Success(claim.Replay);
        }

        TourMediaStorageUploadResult providerResult = await mediaStorage.UploadAsync(
            new TourMediaStorageUpload(
                claim.CloudinaryPublicId!,
                canonical.Image.ContentType,
                canonical.Image.Bytes),
            cancellationToken);
        if (!providerResult.IsSuccess)
        {
            return Result.Failure<TourMediaDto>(
                providerResult.FailureKind == TourMediaStorageFailureKind.Transient
                    ? TourMediaErrorCodes.ProviderUnavailable
                    : TourMediaErrorCodes.ProviderRejected,
                "The media storage service could not accept the image.");
        }

        try
        {
            // Provider I/O happens after the claim transaction. Another request with
            // the same idempotency key may have completed the operation meanwhile;
            // discard claim-time tracked entities so completion observes that state.
            dbContext.ClearTrackedEntities();
            CompletionResult completion = await CompleteAsync(
                canonical,
                claim.CloudinaryPublicId!,
                providerResult.DeliveryUrl!,
                cancellationToken);
            if (completion.Failure is not null)
            {
                await CompensateAsync(claim.CloudinaryPublicId!, cancellationToken);
                return completion.Failure;
            }

            return Result.Success(completion.Media!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            dbContext.ClearTrackedEntities();
            await CompensateAsync(claim.CloudinaryPublicId!, cancellationToken);
            return Result.Failure<TourMediaDto>(
                TourMediaErrorCodes.PersistenceFailed,
                "The image could not be saved.");
        }
    }

    private async Task<ClaimResult> ClaimAsync(
        CanonicalUpload canonical,
        CancellationToken cancellationToken) =>
        await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            await uploadLock.AcquireOperationAsync(
                canonical.TourId,
                canonical.CurrentUserId,
                canonical.IdempotencyKey,
                transactionCancellationToken);

            AuthorizationResult authorization = await AuthorizeAsync(
                canonical.TourId,
                canonical.CurrentUserId,
                transactionCancellationToken);
            if (authorization.Failure is not null)
            {
                return ClaimResult.FromFailure(authorization.Failure);
            }

            TourMediaUploadOperation? existing = await dbContext.TourMediaUploadOperations
                .SingleOrDefaultAsync(operation =>
                    operation.ActorUserId == canonical.CurrentUserId &&
                    operation.TourId == canonical.TourId &&
                    operation.IdempotencyKey == canonical.IdempotencyKey,
                    transactionCancellationToken);
            if (existing is not null)
            {
                if (!string.Equals(
                        existing.PayloadFingerprint,
                        canonical.PayloadFingerprint,
                        StringComparison.Ordinal))
                {
                    return ClaimResult.FromFailure(Result.Failure<TourMediaDto>(
                        TourMediaErrorCodes.IdempotencyKeyPayloadMismatch,
                        "The Idempotency-Key was already used with different request data."));
                }

                if (existing.Status == TourMediaUploadOperationStatus.Completed)
                {
                    DomainTourMedia? media = await dbContext.TourMedia
                        .AsNoTracking()
                        .SingleOrDefaultAsync(
                            candidate => candidate.Id == existing.TourMediaId,
                            transactionCancellationToken);
                    return media is null
                        ? ClaimResult.FromFailure(Result.Failure<TourMediaDto>(
                            TourMediaErrorCodes.PersistenceFailed,
                            "The previous image upload could not be recovered."))
                        : ClaimResult.FromReplay(ToDto(media));
                }

                return ClaimResult.FromUpload(existing.CloudinaryPublicId);
            }

            Result<TourMediaDto>? changeFailure = await ValidateMaterialChangeAsync(
                authorization.Tour!,
                canonical.IsPrimary,
                transactionCancellationToken);
            if (changeFailure is not null)
            {
                return ClaimResult.FromFailure(changeFailure);
            }

            string publicId = mediaStorage.AllocatePublicId(canonical.TourId);
            var operation = TourMediaUploadOperation.Create(
                authorization.Actor!,
                authorization.Tour!,
                canonical.IdempotencyKey,
                canonical.PayloadFingerprint,
                publicId,
                dateTimeProvider.UtcNow);
            dbContext.TourMediaUploadOperations.Add(operation);
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return ClaimResult.FromUpload(publicId);
        }, cancellationToken);

    private async Task<CompletionResult> CompleteAsync(
        CanonicalUpload canonical,
        string publicId,
        Uri deliveryUrl,
        CancellationToken cancellationToken) =>
        await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            await uploadLock.AcquireTourAsync(canonical.TourId, transactionCancellationToken);

            AuthorizationResult authorization = await AuthorizeAsync(
                canonical.TourId,
                canonical.CurrentUserId,
                transactionCancellationToken);
            if (authorization.Failure is not null)
            {
                return CompletionResult.FromFailure(authorization.Failure);
            }

            Result<TourMediaDto>? changeFailure = await ValidateMaterialChangeAsync(
                authorization.Tour!,
                canonical.IsPrimary,
                transactionCancellationToken);
            if (changeFailure is not null)
            {
                return CompletionResult.FromFailure(changeFailure);
            }

            TourMediaUploadOperation? operation = await dbContext.TourMediaUploadOperations
                .SingleOrDefaultAsync(candidate =>
                    candidate.ActorUserId == canonical.CurrentUserId &&
                    candidate.TourId == canonical.TourId &&
                    candidate.IdempotencyKey == canonical.IdempotencyKey,
                    transactionCancellationToken);
            if (operation is null ||
                !string.Equals(operation.CloudinaryPublicId, publicId, StringComparison.Ordinal) ||
                !string.Equals(operation.PayloadFingerprint, canonical.PayloadFingerprint, StringComparison.Ordinal))
            {
                return CompletionResult.FromFailure(Result.Failure<TourMediaDto>(
                    TourMediaErrorCodes.PersistenceFailed,
                    "The image upload operation could not be completed."));
            }

            if (operation.Status == TourMediaUploadOperationStatus.Completed)
            {
                DomainTourMedia? replay = await dbContext.TourMedia
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        candidate => candidate.Id == operation.TourMediaId,
                        transactionCancellationToken);
                return replay is null
                    ? CompletionResult.FromFailure(Result.Failure<TourMediaDto>(
                        TourMediaErrorCodes.PersistenceFailed,
                        "The previous image upload could not be recovered."))
                    : CompletionResult.FromMedia(ToDto(replay));
            }

            if (operation.Status != TourMediaUploadOperationStatus.Pending)
            {
                return CompletionResult.FromFailure(Result.Failure<TourMediaDto>(
                    TourMediaErrorCodes.PersistenceFailed,
                    "The image upload operation is not in a completable state."));
            }

            int nextSortOrder = await dbContext.TourMedia
                .Where(media =>
                    media.TourId == canonical.TourId &&
                    media.LifecycleStatus == TourMediaLifecycleStatus.Active)
                .Select(media => (int?)media.SortOrder)
                .MaxAsync(transactionCancellationToken) ?? 0;
            nextSortOrder = checked(nextSortOrder + 1);

            var media = DomainTourMedia.Create(
                authorization.Tour!,
                publicId,
                deliveryUrl.AbsoluteUri,
                canonical.Caption,
                canonical.AltText,
                nextSortOrder,
                canonical.IsPrimary,
                dateTimeProvider.UtcNow);
            dbContext.TourMedia.Add(media);
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            operation.MarkProviderUploaded(dateTimeProvider.UtcNow);
            operation.Complete(media.Id, dateTimeProvider.UtcNow);

            var audit = AuditLog.CreateRecordedOutcome(
                canonical.CurrentUserId,
                AuditActionTypes.TourMediaUpload,
                AuditEntityTypes.TourMedia,
                media.Id,
                dateTimeProvider.UtcNow,
                AuditOutcome.Success,
                afterData: JsonSerializer.Serialize(new
                {
                    media.TourId,
                    SortOrder = media.SortOrder,
                    media.IsPrimary,
                }));
            dbContext.AuditLogs.Add(audit);
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            return CompletionResult.FromMedia(ToDto(media));
        }, cancellationToken);

    private async Task<AuthorizationResult> AuthorizeAsync(
        long tourId,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        User? actor = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == actorUserId &&
                user.Role == UserRole.TourOperator &&
                user.Status == AccountStatus.Active,
            cancellationToken);
        if (actor is null || !await dbContext.OperatorProfiles.AnyAsync(
                profile => profile.UserId == actorUserId &&
                    profile.ApprovalStatus == OperatorApprovalStatus.Approved,
                cancellationToken))
        {
            return AuthorizationResult.FromFailure(Result.Failure<TourMediaDto>(
                TourMediaErrorCodes.OperatorAccessRequired,
                "An active approved Tour Operator account is required."));
        }

        Tour? tour = await dbContext.Tours.SingleOrDefaultAsync(
            candidate => candidate.Id == tourId && candidate.OperatorUserId == actorUserId,
            cancellationToken);
        return tour is null
            ? AuthorizationResult.FromFailure(Result.Failure<TourMediaDto>(
                TourMediaErrorCodes.TourNotFound,
                "The tour is unavailable."))
            : AuthorizationResult.FromAuthorized(actor, tour);
    }

    private async Task<Result<TourMediaDto>?> ValidateMaterialChangeAsync(
        Tour tour,
        bool requestedPrimary,
        CancellationToken cancellationToken)
    {
        if (tour.Status is not (TourStatus.Draft or TourStatus.Rejected))
        {
            return Result.Failure<TourMediaDto>(
                TourMediaErrorCodes.TourMediaChangeLocked,
                "The tour cannot be changed while it is under review or approved.");
        }

        int activeCount = await dbContext.TourMedia.CountAsync(
            media => media.TourId == tour.Id &&
                media.LifecycleStatus == TourMediaLifecycleStatus.Active,
            cancellationToken);
        if (activeCount >= MaximumActiveImages)
        {
            return Result.Failure<TourMediaDto>(
                TourMediaErrorCodes.ActiveImageLimitReached,
                "A tour can have at most ten active images.");
        }

        if (requestedPrimary && await dbContext.TourMedia.AnyAsync(
                media => media.TourId == tour.Id &&
                    media.LifecycleStatus == TourMediaLifecycleStatus.Active &&
                    media.IsPrimary,
                cancellationToken))
        {
            return Result.Failure<TourMediaDto>(
                TourMediaErrorCodes.ActivePrimaryAlreadyExists,
                "Choose the primary image through the reorder operation.");
        }

        return null;
    }

    private async Task CompensateAsync(string publicId, CancellationToken cancellationToken)
    {
        bool cleanupConfirmed;
        try
        {
            TourMediaStorageDeleteResult result = await mediaStorage.DestroyAsync(
                publicId,
                cancellationToken);
            cleanupConfirmed = result.Outcome is TourMediaStorageDeleteOutcome.Deleted or
                TourMediaStorageDeleteOutcome.AlreadyAbsent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            cleanupConfirmed = false;
        }

        if (cleanupConfirmed)
        {
            return;
        }

        try
        {
            dbContext.ClearTrackedEntities();
            bool alreadyQueued = await dbContext.TourMediaCleanupOutbox.AnyAsync(
                item => item.CloudinaryPublicId == publicId,
                cancellationToken);
            if (!alreadyQueued)
            {
                dbContext.TourMediaCleanupOutbox.Add(
                    TourMediaCleanupOutboxItem.CreateForOrphanedProviderAsset(
                        publicId,
                        dateTimeProvider.UtcNow));
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A caller never receives internal cleanup/provider details.
        }
    }

    private static TourMediaDto ToDto(DomainTourMedia media) => TourMediaProjection.ToDto(media);

    private sealed record CanonicalUpload(
        long TourId,
        long CurrentUserId,
        Guid IdempotencyKey,
        InspectedTourMediaImage Image,
        string? Caption,
        string AltText,
        bool IsPrimary,
        string PayloadFingerprint)
    {
        public static CanonicalUpload From(
            UploadTourMediaCommand command,
            InspectedTourMediaImage image)
        {
            string? caption = NormalizeOptional(command.Caption);
            string altText = command.AltText.Trim();
            return new CanonicalUpload(
                command.TourId,
                command.CurrentUserId,
                command.IdempotencyKey,
                image,
                caption,
                altText,
                command.IsPrimary,
                ComputeFingerprint(image.Bytes, caption, altText, command.IsPrimary));
        }

        private static string ComputeFingerprint(
            byte[] imageBytes,
            string? caption,
            string altText,
            bool isPrimary)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(imageBytes);
            AppendString(hash, caption ?? string.Empty);
            AppendString(hash, altText);
            hash.AppendData(isPrimary ? [1] : [0]);
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private static void AppendString(IncrementalHash hash, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        private static string? NormalizeOptional(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record AuthorizationResult(User? Actor, Tour? Tour, Result<TourMediaDto>? Failure)
    {
        public static AuthorizationResult FromAuthorized(User actor, Tour tour) => new(actor, tour, null);

        public static AuthorizationResult FromFailure(Result<TourMediaDto> failure) => new(null, null, failure);
    }

    private sealed record ClaimResult(string? CloudinaryPublicId, TourMediaDto? Replay, Result<TourMediaDto>? Failure)
    {
        public static ClaimResult FromUpload(string cloudinaryPublicId) => new(cloudinaryPublicId, null, null);

        public static ClaimResult FromReplay(TourMediaDto replay) => new(null, replay, null);

        public static ClaimResult FromFailure(Result<TourMediaDto> failure) => new(null, null, failure);
    }

    private sealed record CompletionResult(TourMediaDto? Media, Result<TourMediaDto>? Failure)
    {
        public static CompletionResult FromMedia(TourMediaDto media) => new(media, null);

        public static CompletionResult FromFailure(Result<TourMediaDto> failure) => new(null, failure);
    }
}