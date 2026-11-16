using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.PostTripComplaints.Common;

namespace TripMate.Application.Features.Traveler.PostTripComplaints.DownloadEvidence;

public sealed record DownloadPostTripComplaintEvidenceQuery(long BookingId, long EvidenceId) : IRequest<Result<ComplaintEvidenceDownloadResponse>>;
public sealed record ComplaintEvidenceDownloadResponse(byte[]? Bytes, Uri? TemporaryUrl, string ContentType, string FileName);

public sealed class DownloadPostTripComplaintEvidenceQueryHandler(IApplicationDbContext dbContext,
    ICurrentUserService currentUser, IComplaintEvidenceStorage storage, IDateTimeProvider clock)
    : IRequestHandler<DownloadPostTripComplaintEvidenceQuery, Result<ComplaintEvidenceDownloadResponse>>
{
    public async Task<Result<ComplaintEvidenceDownloadResponse>> Handle(DownloadPostTripComplaintEvidenceQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long travelerId)
            return Result.Failure<ComplaintEvidenceDownloadResponse>(PostTripComplaintErrorCodes.Unauthorized, "Traveler access is required.");
        var evidence = await (from item in dbContext.ComplaintEvidence.AsNoTracking()
                              join complaint in dbContext.PostTripComplaints.AsNoTracking() on item.ComplaintId equals complaint.Id
                              where item.Id == request.EvidenceId && complaint.BookingId == request.BookingId && complaint.TravelerUserId == travelerId
                              select item).SingleOrDefaultAsync(cancellationToken);
        if (evidence is null) return Result.Failure<ComplaintEvidenceDownloadResponse>(PostTripComplaintErrorCodes.NotFound, "Evidence could not be found.");
        Uri? temporaryUrl = storage.CreateTemporaryDownloadUrl(evidence.StorageObjectKey, evidence.ContentType,
            clock.UtcNow.AddMinutes(5));
        if (temporaryUrl is not null)
            return Result.Success(new ComplaintEvidenceDownloadResponse(null, temporaryUrl, evidence.ContentType, evidence.OriginalFileName));
        byte[]? bytes = await storage.ReadAsync(evidence.StorageObjectKey, cancellationToken);
        return bytes is null
            ? Result.Failure<ComplaintEvidenceDownloadResponse>(PostTripComplaintErrorCodes.StorageUnavailable, "Evidence is temporarily unavailable.")
            : Result.Success(new ComplaintEvidenceDownloadResponse(bytes, null, evidence.ContentType, evidence.OriginalFileName));
    }
}
