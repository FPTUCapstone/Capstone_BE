using System.Security.Cryptography;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.PostTripComplaints.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Complaints.StaffReview;

public static class StaffComplaintEvidenceRules
{
    public const int MaximumFiles = 3;
    public const int MaximumBytes = 5 * 1024 * 1024;
    public static Result Validate(IReadOnlyList<ComplaintEvidenceUpload> files)
    {
        if (files.Count is < 1 or > MaximumFiles) return Result.Failure(StaffComplaintAccess.Invalid, "Supply one to three evidence files.");
        foreach (var f in files)
        {
            string extension = Path.GetExtension(f.FileName).ToLowerInvariant();
            bool signature = f.ContentType switch
            {
                "application/pdf" => extension == ".pdf" && f.Bytes.AsSpan().StartsWith("%PDF-"u8),
                "image/png" => extension == ".png" && f.Bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                "image/jpeg" => extension is ".jpg" or ".jpeg" && f.Bytes.Length > 3 && f.Bytes[0] == 255 && f.Bytes[1] == 216 && f.Bytes[2] == 255,
                _ => false,
            };
            if (!signature || f.Bytes.Length > MaximumBytes || f.FileName.Length > ComplaintEvidence.OriginalFileNameMaxLength)
                return Result.Failure(StaffComplaintAccess.Invalid, "Evidence must be PDF/JPG/PNG, at most 5 MB per file.");
        }
        return Result.Success();
    }
}

public sealed record SubmitStaffComplaintEvidenceCommand(long ComplaintId, Guid OperationId, long? RequestEventId,
    string Reason, IReadOnlyList<ComplaintEvidenceUpload> Files, bool Reconsideration = false) : IRequest<Result<StaffComplaintDetail>>;

public sealed class SubmitStaffComplaintEvidenceCommandHandler(IApplicationDbContext db, ICurrentUserService user,
    IDateTimeProvider clock, IComplaintEvidenceStorage storage) : IRequestHandler<SubmitStaffComplaintEvidenceCommand, Result<StaffComplaintDetail>>
{
    public async Task<Result<StaffComplaintDetail>> Handle(SubmitStaffComplaintEvidenceCommand request, CancellationToken ct)
    {
        if (user.UserId is not > 0 || user.Role is not (nameof(UserRole.Traveler) or nameof(UserRole.TourOperator)))
            return Fail(StaffComplaintAccess.Forbidden, "Party access is required.");
        var valid = StaffComplaintEvidenceRules.Validate(request.Files);
        if (valid.IsFailure) return Fail(valid.ErrorCode!, valid.ErrorMessage!);
        if (request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > ComplaintStaffReviewEvent.TextMaxLength)
            return Fail(StaffComplaintAccess.Invalid, "A reason and operation ID are required.");
        string fingerprint = DecideStaffComplaintCommandHandler.Fingerprint(new
        {
            request.ComplaintId,
            request.RequestEventId,
            request.Reconsideration,
            Reason = request.Reason.Trim(),
            Files = request.Files.Select(f => new { f.FileName, f.ContentType, Hash = Convert.ToHexString(SHA256.HashData(f.Bytes)) })
        });
        var c = await db.PostTripComplaints.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ComplaintId, ct);
        if (c is null) return Fail(StaffComplaintAccess.NotFound, "Complaint not found.");
        if (!await StaffComplaintAccess.OwnsAsync(db, user, c, ct)) return Fail(StaffComplaintAccess.Forbidden, "This complaint belongs to another account.");
        var replay = await db.ComplaintStaffReviewEvents.AsNoTracking().SingleOrDefaultAsync(x => x.ActorUserId == user.UserId && x.OperationId == request.OperationId, ct);
        if (replay is not null) return replay.PayloadFingerprint == fingerprint
            ? await Detail(request.ComplaintId, ct) : Fail(StaffComplaintAccess.Conflict, "Operation ID payload mismatch.");
        if (await StaffComplaintAccess.HasCompletedRefundAsync(db, c.Id, c.BookingId, ct))
            return Fail(StaffComplaintAccess.Conflict, "The refund is completed. This complaint is read-only.");
        List<StoredComplaintEvidence> stored = [];
        try
        {
            foreach (var file in request.Files)
            {
                var item = await storage.StoreAsync(file with { FileName = Path.GetFileName(file.FileName) }, ct);
                if (item is null) return Fail(StaffComplaintAccess.Unavailable, "Evidence storage is unavailable.");
                stored.Add(item);
            }
            var result = await db.ExecuteInSerializableTransactionAsync(async token =>
            {
                var duplicate = await db.ComplaintStaffReviewEvents.SingleOrDefaultAsync(x => x.ActorUserId == user.UserId && x.OperationId == request.OperationId, token);
                if (duplicate is not null) return duplicate.PayloadFingerprint == fingerprint
                    ? Result.Success() : Result.Failure(StaffComplaintAccess.Conflict, "Operation ID payload mismatch.");
                var complaint = await db.PostTripComplaints.SingleAsync(x => x.Id == request.ComplaintId, token);
                if (await StaffComplaintAccess.HasCompletedRefundAsync(db, complaint.Id, complaint.BookingId, token))
                    return Result.Failure(StaffComplaintAccess.Conflict, "The refund is completed. This complaint is read-only.");
                var events = await db.ComplaintStaffReviewEvents.Where(x => x.ComplaintId == complaint.Id).OrderBy(x => x.Id).ToListAsync(token);
                string party = user.Role == nameof(UserRole.Traveler) ? ComplaintStaffReviewEvent.Traveler : ComplaintStaffReviewEvent.Operator;
                if (request.Reconsideration)
                {
                    if (complaint.Status is not (PostTripComplaintStatus.Approved or PostTripComplaintStatus.Rejected) ||
                        !events.Any(e => e.Action is ComplaintStaffReviewEvent.Reject or ComplaintStaffReviewEvent.RefundFull or ComplaintStaffReviewEvent.RefundPartial))
                        return Result.Failure(StaffComplaintAccess.Conflict, "A final Staff decision is required before requesting reconsideration.");
                    var hashes = await db.ComplaintEvidence.Where(e => e.ComplaintId == complaint.Id).Select(e => e.Checksum).ToListAsync(token);
                    if (stored.All(f => hashes.Contains(f.Checksum))) return Result.Failure(StaffComplaintAccess.Invalid, "Reconsideration requires new evidence.");
                }
                else
                {
                    var last = events.LastOrDefault(e => e.Action == ComplaintStaffReviewEvent.RequestEvidence);
                    if (complaint.Status != PostTripComplaintStatus.AwaitingStaffEvidence || last is null || last.Id != request.RequestEventId ||
                        last.TargetParty != party || last.DueAtUtc <= clock.UtcNow)
                        return Result.Failure(StaffComplaintAccess.Conflict, "No current evidence request is open for this account.");
                    complaint.ReceiveStaffEvidence();
                }
                var review = ComplaintStaffReviewEvent.Create(complaint.Id, user.UserId!.Value, request.OperationId, fingerprint,
                    request.Reconsideration ? ComplaintStaffReviewEvent.ReconsiderationRequested : ComplaintStaffReviewEvent.EvidenceSubmitted,
                    request.Reason, "", null, party, clock.UtcNow, request.Reconsideration ? null : request.RequestEventId);
                db.ComplaintStaffReviewEvents.Add(review);
                for (int index = 0; index < stored.Count; index++)
                {
                    var file = request.Files[index]; var value = stored[index];
                    var evidence = ComplaintEvidence.Create(complaint.Id, Path.GetFileName(file.FileName), file.ContentType, value.ByteLength,
                        value.StorageObjectKey, value.Checksum, clock.UtcNow);
                    evidence.AssociateStaffReview(review);
                    db.ComplaintEvidence.Add(evidence);
                }
                db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(user.UserId, "ComplaintStaffEvidence", ComplaintStaffReviewEvent.AuditEntity,
                    complaint.Id, clock.UtcNow, AuditOutcome.Success, request.Reason.Trim(), afterData: review.Action));
                await db.SaveChangesAsync(token);
                return Result.Success();
            }, ct);
            return result.IsFailure ? Fail(result.ErrorCode!, result.ErrorMessage!) : await Detail(request.ComplaintId, ct);
        }
        finally
        {
            // A commit can succeed before its acknowledgement fails. The execution strategy may
            // replay the callback, so callback success/failure cannot determine blob ownership.
            // Delete only files the database confirms are not referenced by committed evidence.
            foreach (var file in stored)
            {
                try
                {
                    bool referenced = await db.ComplaintEvidence.AsNoTracking().AnyAsync(e => e.StorageObjectKey == file.StorageObjectKey, CancellationToken.None);
                    if (!referenced) await storage.DeleteAsync(file.StorageObjectKey, CancellationToken.None);
                }
                catch (Exception)
                {
                    // Prefer a private orphan over deleting committed evidence if verification
                    // or cleanup is unavailable. Never replace the original submission result.
                }
            }
        }
    }
    private Task<Result<StaffComplaintDetail>> Detail(long id, CancellationToken ct) => new GetStaffComplaintQueryHandler(db, user).Handle(new(id, true), ct);
    private static Result<StaffComplaintDetail> Fail(string code, string message) => Result.Failure<StaffComplaintDetail>(code, message);
}

public sealed record DownloadStaffComplaintEvidenceQuery(long ComplaintId, long EvidenceId, bool PartyView = false) : IRequest<Result<StaffComplaintEvidenceDownload>>;
public sealed record StaffComplaintEvidenceDownload(byte[] Bytes, string ContentType, string FileName);
public sealed class DownloadStaffComplaintEvidenceQueryHandler(IApplicationDbContext db, ICurrentUserService user, IComplaintEvidenceStorage storage)
    : IRequestHandler<DownloadStaffComplaintEvidenceQuery, Result<StaffComplaintEvidenceDownload>>
{
    public async Task<Result<StaffComplaintEvidenceDownload>> Handle(DownloadStaffComplaintEvidenceQuery request, CancellationToken ct)
    {
        var access = await new GetStaffComplaintQueryHandler(db, user).Handle(new(request.ComplaintId, request.PartyView), ct);
        if (access.IsFailure) return Result.Failure<StaffComplaintEvidenceDownload>(access.ErrorCode!, access.ErrorMessage!);
        var evidence = await db.ComplaintEvidence.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.EvidenceId && x.ComplaintId == request.ComplaintId, ct);
        if (evidence is null) return Result.Failure<StaffComplaintEvidenceDownload>(StaffComplaintAccess.NotFound, "Evidence not found.");
        var bytes = await storage.ReadAsync(evidence.StorageObjectKey, ct);
        return bytes is null ? Result.Failure<StaffComplaintEvidenceDownload>(StaffComplaintAccess.Unavailable, "Evidence could not be read.")
            : Result.Success(new StaffComplaintEvidenceDownload(bytes, evidence.ContentType, Path.GetFileName(evidence.OriginalFileName)));
    }
}