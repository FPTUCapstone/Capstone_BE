using System.Security.Cryptography;
using System.Text.Json;

using MediatR;
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.PostTripComplaints.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.PostTripComplaints.Submit;

public sealed class SubmitPostTripComplaintCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    IComplaintEvidenceStorage evidenceStorage)
    : IRequestHandler<SubmitPostTripComplaintCommand, Result<SubmitPostTripComplaintResponse>>
{
    private const long MaximumEvidenceBytes = 10L * 1024 * 1024;
    private const long MaximumTotalEvidenceBytes = 30L * 1024 * 1024;
    private const int OperatorResponseCalendarDays = 7;

    public async Task<Result<SubmitPostTripComplaintResponse>> Handle(
        SubmitPostTripComplaintCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long travelerId || currentUser.Role != nameof(UserRole.Traveler))
            return Result.Failure<SubmitPostTripComplaintResponse>(PostTripComplaintErrorCodes.Unauthorized,
                "Traveler access is required.");

        Result validation = ValidateEvidence(request.Evidence);
        if (validation.IsFailure)
            return Result.Failure<SubmitPostTripComplaintResponse>(validation.ErrorCode!, validation.ErrorMessage!);

        string fingerprint = ComputeFingerprint(request);
        PostTripComplaintSubmissionOperation? priorOperation = await dbContext.PostTripComplaintSubmissionOperations
            .Include(item => item.Complaint)
            .SingleOrDefaultAsync(item => item.TravelerUserId == travelerId && item.IdempotencyKey == request.IdempotencyKey,
                cancellationToken);
        if (priorOperation is not null)
            return ReplayExisting(priorOperation, fingerprint);

        DateTimeOffset now = clock.UtcNow;
        Booking? booking = await dbContext.Bookings.Include(item => item.TourSchedule).ThenInclude(item => item.Tour)
            .SingleOrDefaultAsync(item => item.Id == request.BookingId, cancellationToken);
        if (booking is null || booking.TravelerUserId != travelerId)
            return Result.Failure<SubmitPostTripComplaintResponse>(PostTripComplaintErrorCodes.NotFound,
                "The booking could not be found.");
        if (!IsEligible(booking, now))
            return Result.Failure<SubmitPostTripComplaintResponse>(PostTripComplaintErrorCodes.Ineligible,
                "The complaint submission window has closed.");

        PostTripComplaint? existing = await dbContext.PostTripComplaints.AsNoTracking()
            .SingleOrDefaultAsync(item => item.BookingId == booking.Id, cancellationToken);
        if (existing is not null)
            return Result.Success(ToResponse(existing, isReplay: true));

        var stored = new List<StoredComplaintEvidence>();
        try
        {
            foreach (ComplaintEvidenceUpload upload in request.Evidence)
            {
                StoredComplaintEvidence? value = await evidenceStorage.StoreAsync(upload, cancellationToken);
                if (value is null) return await CleanupFailure(stored, cancellationToken);
                stored.Add(value);
            }

            Result<SubmitPostTripComplaintResponse> persisted = await dbContext.ExecuteInSerializableTransactionAsync(async token =>
            {
                PostTripComplaintSubmissionOperation? existingOperation = await dbContext.PostTripComplaintSubmissionOperations
                    .Include(item => item.Complaint)
                    .SingleOrDefaultAsync(item => item.TravelerUserId == travelerId && item.IdempotencyKey == request.IdempotencyKey,
                        token);
                if (existingOperation is not null)
                    return ReplayExisting(existingOperation, fingerprint);

                Booking? lockedBooking = await dbContext.Bookings.Include(item => item.TourSchedule).ThenInclude(item => item.Tour)
                    .SingleOrDefaultAsync(item => item.Id == request.BookingId, token);
                if (lockedBooking is null || lockedBooking.TravelerUserId != travelerId)
                    return Result.Failure<SubmitPostTripComplaintResponse>(PostTripComplaintErrorCodes.NotFound,
                        "The booking could not be found.");
                DateTimeOffset transactionNow = clock.UtcNow;
                if (!IsEligible(lockedBooking, transactionNow))
                    return Result.Failure<SubmitPostTripComplaintResponse>(PostTripComplaintErrorCodes.Ineligible,
                        "The complaint submission window has closed.");

                PostTripComplaint? concurrent = await dbContext.PostTripComplaints
                    .SingleOrDefaultAsync(item => item.BookingId == lockedBooking.Id, token);
                if (concurrent is not null) return Result.Success(ToResponse(concurrent, isReplay: true));

                decimal verifiedPaid = await dbContext.PaymentTransactions.AsNoTracking()
                    .Where(item => item.BookingId == lockedBooking.Id && item.Status == PaymentTransactionStatus.Success)
                    .SumAsync(item => (decimal?)item.Amount, token) ?? 0m;
                // Snapshot the refund ceiling at submission. A later payment or refund state
                // change must not alter what UC-69/UC-75 can decide for this complaint.
                decimal committedRefunds = await dbContext.Refunds.AsNoTracking()
                    .Where(item => item.BookingId == lockedBooking.Id && item.Status != RefundStatus.Rejected)
                    .SumAsync(item => (decimal?)item.Amount, token) ?? 0m;
                decimal refundable = Math.Max(0m, verifiedPaid - committedRefunds);
                var complaint = PostTripComplaint.Create(lockedBooking.Id, travelerId, request.ReasonCategory,
                    request.ReasonDetail, transactionNow, transactionNow.AddDays(OperatorResponseCalendarDays), refundable);
                dbContext.PostTripComplaints.Add(complaint);
                await dbContext.SaveChangesAsync(token);

                for (var index = 0; index < stored.Count; index++)
                {
                    ComplaintEvidenceUpload upload = request.Evidence[index];
                    StoredComplaintEvidence storage = stored[index];
                    dbContext.ComplaintEvidence.Add(ComplaintEvidence.Create(complaint.Id, upload.FileName,
                        upload.ContentType, storage.ByteLength, storage.StorageObjectKey, storage.Checksum, transactionNow));
                }
                dbContext.PostTripComplaintSubmissionOperations.Add(PostTripComplaintSubmissionOperation.Create(
                    travelerId, request.IdempotencyKey, fingerprint, complaint, transactionNow));
                dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(travelerId,
                    "PostTripComplaintSubmitted", "PostTripComplaint", complaint.Id, transactionNow, AuditOutcome.Success,
                    afterData: JsonSerializer.Serialize(new { complaint.BookingId, complaint.Status })));
                dbContext.Notifications.Add(new Notification
                {
                    UserId = lockedBooking.TourSchedule.Tour.OperatorUserId,
                    Channel = NotificationChannel.InApp,
                    Type = "PostTripComplaintSubmitted",
                    Title = "New post-trip complaint",
                    Body = "A traveler submitted a post-trip complaint for one of your bookings.",
                    RelatedEntityType = "PostTripComplaint",
                    RelatedEntityId = complaint.Id,
                    CreatedAtUtc = transactionNow,
                });
                await dbContext.SaveChangesAsync(token);
                return Result.Success(ToResponse(complaint, isReplay: false));
            }, cancellationToken);
            if (persisted.IsFailure || persisted.Value.IsReplay)
                await Cleanup(stored, cancellationToken);
            return persisted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await Cleanup(stored, cancellationToken);
            return Result.Failure<SubmitPostTripComplaintResponse>(PostTripComplaintErrorCodes.StorageUnavailable,
                "The complaint could not be stored. Please try again.");
        }

        async Task<Result<SubmitPostTripComplaintResponse>> CleanupFailure(
            IReadOnlyList<StoredComplaintEvidence> values, CancellationToken token)
        {
            await Cleanup(values, token);
            return Result.Failure<SubmitPostTripComplaintResponse>(PostTripComplaintErrorCodes.StorageUnavailable,
                "The complaint evidence could not be stored. Please try again.");
        }
    }

    private static bool IsEligible(Booking booking, DateTimeOffset now) =>
        booking.Status is BookingStatus.Confirmed or BookingStatus.Completed &&
        now >= booking.TourSchedule.EndAtUtc &&
        now <= booking.TourSchedule.EndAtUtc.AddDays(3);

    private static SubmitPostTripComplaintResponse ToResponse(PostTripComplaint complaint, bool isReplay) =>
        new(complaint.Id, complaint.BookingId, complaint.Status.ToString(), complaint.SubmittedAtUtc,
            complaint.ResponseDeadlineUtc, isReplay);

    private static Result<SubmitPostTripComplaintResponse> ReplayExisting(
        PostTripComplaintSubmissionOperation operation, string fingerprint)
    {
        if (!string.Equals(operation.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
            return Result.Failure<SubmitPostTripComplaintResponse>(PostTripComplaintErrorCodes.IdempotencyKeyPayloadMismatch,
                "The Idempotency-Key was already used with different complaint data.");
        return operation.Complaint is null
            ? Result.Failure<SubmitPostTripComplaintResponse>(PostTripComplaintErrorCodes.IdempotencyKeyPayloadMismatch,
                "The previous complaint request did not complete.")
            : Result.Success(ToResponse(operation.Complaint, isReplay: true));
    }

    private static string ComputeFingerprint(SubmitPostTripComplaintCommand request)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.BookingId,
            request.ReasonCategory,
            ReasonDetail = request.ReasonDetail.Trim(),
            Evidence = request.Evidence.Select(item => new
            {
                FileName = item.FileName.Trim(),
                ContentType = item.ContentType.Trim().ToLowerInvariant(),
                Checksum = Convert.ToHexString(SHA256.HashData(item.Bytes)),
            }),
        });
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static Result ValidateEvidence(IReadOnlyList<ComplaintEvidenceUpload> uploads)
    {
        if (uploads.Count > 5) return Result.Failure(PostTripComplaintErrorCodes.EvidenceLimit, "At most five evidence files are allowed.");
        long total = 0;
        foreach (ComplaintEvidenceUpload upload in uploads)
        {
            if (upload.Bytes.Length == 0 || upload.Bytes.LongLength > MaximumEvidenceBytes) return Result.Failure(PostTripComplaintErrorCodes.EvidenceLimit, "An evidence file exceeds the permitted size.");
            total += upload.Bytes.LongLength;
            if (total > MaximumTotalEvidenceBytes) return Result.Failure(PostTripComplaintErrorCodes.EvidenceLimit, "Evidence exceeds the permitted total size.");
            if (!IsAllowed(upload.ContentType, upload.Bytes)) return Result.Failure(PostTripComplaintErrorCodes.EvidenceType, "An evidence file type is not allowed.");
        }
        return Result.Success();
    }

    private static bool IsAllowed(string contentType, byte[] bytes) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
        "image/png" => bytes.Length >= 8 && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/webp" => bytes.Length >= 12 && bytes.Take(4).SequenceEqual("RIFF"u8.ToArray()) && bytes.Skip(8).Take(4).SequenceEqual("WEBP"u8.ToArray()),
        "application/pdf" => bytes.Length >= 5 && bytes.Take(5).SequenceEqual("%PDF-"u8.ToArray()),
        _ => false,
    };

    private async Task Cleanup(IEnumerable<StoredComplaintEvidence> stored, CancellationToken cancellationToken)
    {
        foreach (StoredComplaintEvidence value in stored)
        {
            try { await evidenceStorage.DeleteAsync(value.StorageObjectKey, cancellationToken); }
            catch (Exception) { /* Best-effort cleanup; the failed request remains auditable in logs. */ }
        }
    }
}
