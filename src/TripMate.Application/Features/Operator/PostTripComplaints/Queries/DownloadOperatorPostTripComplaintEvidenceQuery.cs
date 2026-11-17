using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.PostTripComplaints.Common;
using TripMate.Application.Features.Traveler.PostTripComplaints.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.PostTripComplaints.Queries;

public sealed record DownloadOperatorPostTripComplaintEvidenceQuery(long ComplaintId, long EvidenceId)
    : IRequest<Result<OperatorComplaintEvidenceDownloadResponse>>;

public sealed record OperatorComplaintEvidenceDownloadResponse(byte[]? Bytes, Uri? TemporaryUrl,
    string ContentType, string FileName);

public sealed class DownloadOperatorPostTripComplaintEvidenceQueryHandler(
    IApplicationDbContext dbContext, ICurrentUserService currentUser, IComplaintEvidenceStorage storage,
    IDateTimeProvider clock)
    : IRequestHandler<DownloadOperatorPostTripComplaintEvidenceQuery,
        Result<OperatorComplaintEvidenceDownloadResponse>>
{
    public async Task<Result<OperatorComplaintEvidenceDownloadResponse>> Handle(
        DownloadOperatorPostTripComplaintEvidenceQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorUserId || currentUser.Role != nameof(UserRole.TourOperator))
            return Result.Failure<OperatorComplaintEvidenceDownloadResponse>(
                OperatorPostTripComplaintErrorCodes.Unauthorized, "Tour Operator access is required.");

        var evidence = await (from item in dbContext.ComplaintEvidence.AsNoTracking()
                              join complaint in dbContext.PostTripComplaints.AsNoTracking() on item.ComplaintId equals complaint.Id
                              join booking in dbContext.Bookings.AsNoTracking() on complaint.BookingId equals booking.Id
                              join schedule in dbContext.TourSchedules.AsNoTracking() on booking.TourScheduleId equals schedule.Id
                              join tour in dbContext.Tours.AsNoTracking() on schedule.TourId equals tour.Id
                              where item.Id == request.EvidenceId && complaint.Id == request.ComplaintId &&
                                    tour.OperatorUserId == operatorUserId
                              select item).SingleOrDefaultAsync(cancellationToken);
        if (evidence is null)
            return Result.Failure<OperatorComplaintEvidenceDownloadResponse>(
                OperatorPostTripComplaintErrorCodes.NotFound, "Evidence could not be found.");

        Uri? temporaryUrl = storage.CreateTemporaryDownloadUrl(evidence.StorageObjectKey, evidence.ContentType,
            clock.UtcNow.AddMinutes(5));
        if (temporaryUrl is not null)
            return Result.Success(new OperatorComplaintEvidenceDownloadResponse(null, temporaryUrl,
                evidence.ContentType, evidence.OriginalFileName));

        byte[]? bytes = await storage.ReadAsync(evidence.StorageObjectKey, cancellationToken);
        return bytes is null
            ? Result.Failure<OperatorComplaintEvidenceDownloadResponse>(
                PostTripComplaintErrorCodes.StorageUnavailable, "Evidence is temporarily unavailable.")
            : Result.Success(new OperatorComplaintEvidenceDownloadResponse(bytes, null, evidence.ContentType,
                evidence.OriginalFileName));
    }
}