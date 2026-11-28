using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.DirectRefunds.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.DirectRefunds.Evidence;

public sealed record DownloadDirectRefundEvidenceQuery(long CaseId, long EvidenceId)
    : IRequest<Result<DirectRefundEvidenceDownload>>;

public sealed record DirectRefundEvidenceDownload(byte[] Bytes, string ContentType, string FileName);

public sealed class DownloadDirectRefundEvidenceQueryHandler(
    IApplicationDbContext dbContext, ICurrentUserService currentUser, IDirectRefundEvidenceStorage storage)
    : IRequestHandler<DownloadDirectRefundEvidenceQuery, Result<DirectRefundEvidenceDownload>>
{
    public async Task<Result<DirectRefundEvidenceDownload>> Handle(
        DownloadDirectRefundEvidenceQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || currentUser.Role != nameof(UserRole.Administrator))
            return Result.Failure<DirectRefundEvidenceDownload>(DirectRefundErrorCodes.AccessDenied, "Administrator access is required.");

        var evidence = await dbContext.DirectRefundEvidences.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == request.EvidenceId && item.CaseId == request.CaseId, cancellationToken);
        if (evidence is null)
            return Result.Failure<DirectRefundEvidenceDownload>(DirectRefundErrorCodes.NotFound, "Evidence not found for this case.");
        if (!DirectRefundEvidence.IsAllowedContentType(evidence.ContentType))
            return Result.Failure<DirectRefundEvidenceDownload>(DirectRefundErrorCodes.EvidenceInvalid, "The evidence file type is not supported.");

        var bytes = await storage.ReadAsync(evidence.StorageObjectKey, cancellationToken);
        if (bytes is null || bytes.LongLength != evidence.ByteLength || bytes.LongLength > DirectRefundEvidence.MaxFileSizeBytes)
            return Result.Failure<DirectRefundEvidenceDownload>(DirectRefundErrorCodes.StorageUnavailable, "Evidence is temporarily unavailable.");
        return Result.Success(new DirectRefundEvidenceDownload(bytes,
            evidence.ContentType == "image/jpg" ? "image/jpeg" : evidence.ContentType, evidence.OriginalFileName));
    }
}