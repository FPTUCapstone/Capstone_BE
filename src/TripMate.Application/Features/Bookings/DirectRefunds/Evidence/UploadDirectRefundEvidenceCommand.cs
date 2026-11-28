using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.DirectRefunds.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Bookings.DirectRefunds.Evidence;

public sealed record UploadDirectRefundEvidenceCommand(
    long CaseId,
    string FileName,
    string ContentType,
    byte[] Bytes) : IRequest<Result<UploadDirectRefundEvidenceResponse>>;

public sealed record UploadDirectRefundEvidenceResponse(long EvidenceId);

public sealed class UploadDirectRefundEvidenceCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDirectRefundEvidenceStorage storage,
    IDateTimeProvider clock)
    : IRequestHandler<UploadDirectRefundEvidenceCommand, Result<UploadDirectRefundEvidenceResponse>>
{
    public async Task<Result<UploadDirectRefundEvidenceResponse>> Handle(
        UploadDirectRefundEvidenceCommand request, CancellationToken cancellationToken)
    {
        if (!DirectRefundEvidence.IsAllowedContentType(request.ContentType))
            return Result.Failure<UploadDirectRefundEvidenceResponse>(
                DirectRefundErrorCodes.EvidenceInvalid,
                $"Content type '{request.ContentType}' is not allowed. Supported: PDF, JPEG, PNG.");

        if (request.Bytes.Length == 0 || request.Bytes.LongLength > DirectRefundEvidence.MaxFileSizeBytes)
            return Result.Failure<UploadDirectRefundEvidenceResponse>(
                DirectRefundErrorCodes.EvidenceInvalid,
                $"File size must be between 1 byte and {DirectRefundEvidence.MaxFileSizeBytes / 1024 / 1024} MB.");

        return await dbContext.ExecuteInTransactionAsync(async ct =>
        {
            var (directCase, _, operatorUserId) =
                await DirectRefundOwnershipResolver.ResolveAsync(dbContext, request.CaseId, ct);

            if (directCase is null)
                return Result.Failure<UploadDirectRefundEvidenceResponse>(
                    DirectRefundErrorCodes.NotFound, "Direct refund case not found.");

            if (currentUser.UserId != operatorUserId)
                return Result.Failure<UploadDirectRefundEvidenceResponse>(
                    DirectRefundErrorCodes.AccessDenied, "Access denied.");

            if (directCase.Status != DirectRefundCase.StatusAwaitingTransfer)
                return Result.Failure<UploadDirectRefundEvidenceResponse>(
                    DirectRefundErrorCodes.InvalidState,
                    "Evidence can only be uploaded when status is AwaitingTransfer.");

            int existingCount = await dbContext.DirectRefundEvidences.AsNoTracking()
                .CountAsync(e => e.CaseId == request.CaseId, ct);

            if (existingCount >= DirectRefundEvidence.MaxFilesPerReport)
                return Result.Failure<UploadDirectRefundEvidenceResponse>(
                    DirectRefundErrorCodes.TooManyEvidences,
                    $"At most {DirectRefundEvidence.MaxFilesPerReport} evidence files are allowed per case.");

            var upload = new DirectRefundEvidenceUpload(request.FileName, request.ContentType, request.Bytes);
            var stored = await storage.StoreAsync(upload, ct);
            if (stored is null)
                return Result.Failure<UploadDirectRefundEvidenceResponse>(
                    DirectRefundErrorCodes.EvidenceInvalid, "Evidence file could not be stored.");

            var evidence = DirectRefundEvidence.Create(
                request.CaseId,
                request.FileName,
                request.ContentType,
                stored.ByteLength,
                stored.StorageObjectKey,
                stored.Checksum,
                clock.UtcNow);

            dbContext.DirectRefundEvidences.Add(evidence);
            await dbContext.SaveChangesAsync(ct);

            return Result.Success(new UploadDirectRefundEvidenceResponse(evidence.Id));
        }, cancellationToken);
    }
}