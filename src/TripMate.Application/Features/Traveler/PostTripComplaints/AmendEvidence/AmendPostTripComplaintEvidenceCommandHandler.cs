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

namespace TripMate.Application.Features.Traveler.PostTripComplaints.AmendEvidence;

public sealed class AmendPostTripComplaintEvidenceCommandHandler(
    IApplicationDbContext dbContext, ICurrentUserService currentUser,
    IDateTimeProvider clock, IComplaintEvidenceStorage evidenceStorage)
    : IRequestHandler<AmendPostTripComplaintEvidenceCommand, Result<AmendPostTripComplaintEvidenceResponse>>
{
    private const long MaximumEvidenceBytes = 10L * 1024 * 1024;
    private const long MaximumTotalEvidenceBytes = 30L * 1024 * 1024;
    private const int MaximumEvidenceFiles = 5;

    public async Task<Result<AmendPostTripComplaintEvidenceResponse>> Handle(
        AmendPostTripComplaintEvidenceCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long travelerId || currentUser.Role != nameof(UserRole.Traveler))
            return Result.Failure<AmendPostTripComplaintEvidenceResponse>(PostTripComplaintErrorCodes.Unauthorized, "Traveler access is required.");
        Result validation = ValidateUploads(request.Evidence);
        if (validation.IsFailure)
            return Result.Failure<AmendPostTripComplaintEvidenceResponse>(validation.ErrorCode!, validation.ErrorMessage!);

        string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request.Evidence.Select(file => new { file.FileName, file.ContentType, Checksum = Convert.ToHexString(SHA256.HashData(file.Bytes)) }))));
        PostTripComplaintEvidenceAmendmentOperation? replay = await dbContext.PostTripComplaintEvidenceAmendmentOperations
            .SingleOrDefaultAsync(item => item.TravelerUserId == travelerId && item.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (replay is not null)
            return string.Equals(replay.PayloadFingerprint, fingerprint, StringComparison.Ordinal)
                ? await ReplayResponseAsync(replay.ComplaintId, replay.CreatedAtUtc, cancellationToken)
                : Result.Failure<AmendPostTripComplaintEvidenceResponse>(PostTripComplaintErrorCodes.IdempotencyKeyPayloadMismatch, "The Idempotency-Key was already used with different evidence.");

        var stored = new List<StoredComplaintEvidence>();
        try
        {
            foreach (ComplaintEvidenceUpload upload in request.Evidence)
            {
                StoredComplaintEvidence? value = await evidenceStorage.StoreAsync(upload, cancellationToken);
                if (value is null) return await StorageFailureAsync(stored, cancellationToken);
                stored.Add(value);
            }

            Result<AmendPostTripComplaintEvidenceResponse> result = await dbContext.ExecuteInSerializableTransactionAsync(async token =>
            {
                PostTripComplaintEvidenceAmendmentOperation? existing = await dbContext.PostTripComplaintEvidenceAmendmentOperations
                    .SingleOrDefaultAsync(item => item.TravelerUserId == travelerId && item.IdempotencyKey == request.IdempotencyKey, token);
                if (existing is not null)
                    return string.Equals(existing.PayloadFingerprint, fingerprint, StringComparison.Ordinal)
                        ? await ReplayResponseAsync(existing.ComplaintId, existing.CreatedAtUtc, token)
                        : Result.Failure<AmendPostTripComplaintEvidenceResponse>(PostTripComplaintErrorCodes.IdempotencyKeyPayloadMismatch, "The Idempotency-Key was already used with different evidence.");

                PostTripComplaint? complaint = await dbContext.PostTripComplaints.SingleOrDefaultAsync(item => item.BookingId == request.BookingId && item.TravelerUserId == travelerId, token);
                if (complaint is null) return Result.Failure<AmendPostTripComplaintEvidenceResponse>(PostTripComplaintErrorCodes.NotFound, "The complaint could not be found.");
                Booking? booking = await dbContext.Bookings.Include(item => item.TourSchedule).ThenInclude(item => item.Tour)
                    .SingleOrDefaultAsync(item => item.Id == request.BookingId && item.TravelerUserId == travelerId, token);
                if (booking is null) return Result.Failure<AmendPostTripComplaintEvidenceResponse>(PostTripComplaintErrorCodes.NotFound, "The booking could not be found.");
                DateTimeOffset now = clock.UtcNow;
                if (!complaint.CanAppendEvidence(now, booking.TourSchedule.EndAtUtc.AddDays(3)))
                    return Result.Failure<AmendPostTripComplaintEvidenceResponse>(PostTripComplaintErrorCodes.EvidenceAmendmentUnavailable, "Evidence can no longer be amended.");

                var existingEvidence = await dbContext.ComplaintEvidence.Where(item => item.ComplaintId == complaint.Id).ToListAsync(token);
                long existingBytes = existingEvidence.Sum(item => item.ByteLength);
                if (existingEvidence.Count + stored.Count > MaximumEvidenceFiles || existingBytes + stored.Sum(item => item.ByteLength) > MaximumTotalEvidenceBytes)
                    return Result.Failure<AmendPostTripComplaintEvidenceResponse>(PostTripComplaintErrorCodes.EvidenceLimit, "Evidence exceeds the permitted aggregate limit.");

                complaint.MarkEvidenceAmended(now);
                for (int index = 0; index < stored.Count; index++)
                {
                    ComplaintEvidenceUpload upload = request.Evidence[index];
                    StoredComplaintEvidence storage = stored[index];
                    dbContext.ComplaintEvidence.Add(ComplaintEvidence.Create(complaint.Id, upload.FileName, upload.ContentType, storage.ByteLength, storage.StorageObjectKey, storage.Checksum, now));
                }
                dbContext.PostTripComplaintEvidenceAmendmentOperations.Add(PostTripComplaintEvidenceAmendmentOperation.Create(travelerId, request.IdempotencyKey, fingerprint, complaint.Id, now));
                dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(travelerId, "PostTripComplaintEvidenceAmended", "PostTripComplaint", complaint.Id, now, AuditOutcome.Success, afterData: JsonSerializer.Serialize(new { complaint.BookingId, EvidenceAdded = stored.Count })));
                dbContext.Notifications.Add(new Notification { UserId = booking.TourSchedule.Tour.OperatorUserId, Channel = NotificationChannel.InApp, Type = "PostTripComplaintEvidenceAmended", Title = "Complaint evidence updated", Body = "A traveler added evidence to a post-trip complaint.", RelatedEntityType = "PostTripComplaint", RelatedEntityId = complaint.Id, CreatedAtUtc = now });
                await dbContext.SaveChangesAsync(token);
                return Result.Success(new AmendPostTripComplaintEvidenceResponse(complaint.Id, complaint.EvidenceAmendmentUsedAtUtc!.Value, existingEvidence.Count + stored.Count, existingBytes + stored.Sum(item => item.ByteLength), false));
            }, cancellationToken);
            if (result.IsFailure || result.Value.IsReplay) await CleanupAsync(stored, cancellationToken);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await CleanupAsync(stored, cancellationToken);
            return Result.Failure<AmendPostTripComplaintEvidenceResponse>(PostTripComplaintErrorCodes.StorageUnavailable, "The complaint evidence could not be stored.");
        }
    }

    private async Task<Result<AmendPostTripComplaintEvidenceResponse>> ReplayResponseAsync(long complaintId, DateTimeOffset amendedAtUtc, CancellationToken token)
    {
        var totals = await dbContext.ComplaintEvidence.AsNoTracking()
            .Where(item => item.ComplaintId == complaintId)
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Bytes = group.Sum(item => item.ByteLength) })
            .SingleOrDefaultAsync(token);
        return Result.Success(new AmendPostTripComplaintEvidenceResponse(complaintId, amendedAtUtc,
            totals?.Count ?? 0, totals?.Bytes ?? 0, true));
    }

    private static Result ValidateUploads(IReadOnlyList<ComplaintEvidenceUpload> uploads)
    {
        if (uploads.Count is 0 or > MaximumEvidenceFiles)
            return Result.Failure(PostTripComplaintErrorCodes.EvidenceLimit, "One to five evidence files are required.");
        long total = 0;
        foreach (ComplaintEvidenceUpload upload in uploads)
        {
            if (upload.Bytes.Length == 0 || upload.Bytes.LongLength > MaximumEvidenceBytes)
                return Result.Failure(PostTripComplaintErrorCodes.EvidenceLimit, "An evidence file exceeds the permitted size.");
            total += upload.Bytes.LongLength;
            if (total > MaximumTotalEvidenceBytes)
                return Result.Failure(PostTripComplaintErrorCodes.EvidenceLimit, "Evidence exceeds the permitted total size.");
            if (!IsAllowed(upload.ContentType, upload.Bytes))
                return Result.Failure(PostTripComplaintErrorCodes.EvidenceType, "An evidence file type is not allowed.");
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
    private async Task<Result<AmendPostTripComplaintEvidenceResponse>> StorageFailureAsync(IReadOnlyList<StoredComplaintEvidence> values, CancellationToken token) { await CleanupAsync(values, token); return Result.Failure<AmendPostTripComplaintEvidenceResponse>(PostTripComplaintErrorCodes.StorageUnavailable, "The complaint evidence could not be stored."); }
    private async Task CleanupAsync(IEnumerable<StoredComplaintEvidence> values, CancellationToken token) { foreach (StoredComplaintEvidence item in values) { try { await evidenceStorage.DeleteAsync(item.StorageObjectKey, token); } catch (Exception) { } } }
}
