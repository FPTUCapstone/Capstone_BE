using System.Security.Cryptography;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Application.Features.Bookings.DirectRefunds.Create;
using TripMate.Application.Features.Complaints.PartialRefundProposals;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Complaints.StaffReview;

public sealed record DecideStaffComplaintCommand(long ComplaintId, string Action, string Reason, string ContractBasis,
    decimal? Amount, string? TargetParty, string ExpectedVersion, Guid OperationId) : IRequest<Result<StaffComplaintDetail>>;

public sealed class DecideStaffComplaintCommandHandler(IApplicationDbContext db, ICurrentUserService user,
    IDateTimeProvider clock, IComplaintRefundRequestCreator legacyRefunds, IPiiProtector? protector = null)
    : IRequestHandler<DecideStaffComplaintCommand, Result<StaffComplaintDetail>>
{
    public async Task<Result<StaffComplaintDetail>> Handle(DecideStaffComplaintCommand request, CancellationToken ct)
    {
        if (!StaffComplaintAccess.IsStaff(user)) return Fail(StaffComplaintAccess.Forbidden, "Staff access is required.");
        if (request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > ComplaintStaffReviewEvent.TextMaxLength ||
            string.IsNullOrWhiteSpace(request.ContractBasis) || request.ContractBasis.Trim().Length > ComplaintStaffReviewEvent.TextMaxLength ||
            request.Action is not (ComplaintStaffReviewEvent.RequestEvidence or ComplaintStaffReviewEvent.Reject or ComplaintStaffReviewEvent.RefundFull or ComplaintStaffReviewEvent.RefundPartial))
            return Fail(StaffComplaintAccess.Invalid, "An action, reason, contractual basis and operation ID are required.");
        byte[] expected;
        try { expected = Convert.FromBase64String(request.ExpectedVersion); }
        catch (Exception e) when (e is FormatException or ArgumentNullException) { return Fail(StaffComplaintAccess.Invalid, "ExpectedVersion must be base64."); }
        if (expected.Length != 8) return Fail(StaffComplaintAccess.Invalid, "ExpectedVersion must contain a SQL row version.");
        if (request.Action == ComplaintStaffReviewEvent.RequestEvidence && request.TargetParty is not (ComplaintStaffReviewEvent.Traveler or ComplaintStaffReviewEvent.Operator))
            return Fail(StaffComplaintAccess.Invalid, "Select the party required to provide evidence.");
        if (request.Action != ComplaintStaffReviewEvent.RequestEvidence && request.TargetParty is not null ||
            request.Action != ComplaintStaffReviewEvent.RefundPartial && request.Amount is not null)
            return Fail(StaffComplaintAccess.Invalid, "Unexpected action fields.");
        string fingerprint = Fingerprint(new { request.ComplaintId, request.Action, Reason = request.Reason.Trim(), ContractBasis = request.ContractBasis.Trim(), request.Amount, request.TargetParty, request.ExpectedVersion });
        try
        {
            var result = await db.ExecuteInSerializableTransactionAsync(async token =>
            {
                var prior = await db.ComplaintStaffReviewEvents.SingleOrDefaultAsync(x => x.ActorUserId == user.UserId && x.OperationId == request.OperationId, token);
                if (prior is not null) return prior.ComplaintId == request.ComplaintId && prior.PayloadFingerprint == fingerprint
                    ? Result.Success() : Result.Failure(StaffComplaintAccess.Conflict, "Operation ID was used with another payload.");
                var c = await db.PostTripComplaints.SingleOrDefaultAsync(x => x.Id == request.ComplaintId, token);
                if (c is null) return Result.Failure(StaffComplaintAccess.NotFound, "Complaint not found.");
                if (!c.RowVersion.SequenceEqual(expected) || c.Status != PostTripComplaintStatus.AwaitingStaffReview)
                    return Result.Failure(StaffComplaintAccess.Conflict, "Refresh the complaint before making a decision.");
                decimal? amount = null;
                bool refund = request.Action is ComplaintStaffReviewEvent.RefundFull or ComplaintStaffReviewEvent.RefundPartial;
                if (refund)
                {
                    var booking = await db.Bookings.SingleAsync(b => b.Id == c.BookingId, token);
                    var payment = await BookingCancellationPaymentVerifier.FindSuccessfulPaymentAsync(db, booking, token);
                    if (payment is null || payment.Amount <= 0 || payment.Amount != decimal.Truncate(payment.Amount))
                        return Result.Failure(StaffComplaintAccess.Conflict, "A verified whole-VND payment is required.");
                    decimal directReserved = await db.DirectRefundCases.Where(x => x.BookingId == c.BookingId)
                        .SumAsync(x => (decimal?)x.RefundAmountVnd, token) ?? 0;
                    decimal legacyReserved = await db.Refunds.Where(x => x.BookingId == c.BookingId && x.Status != RefundStatus.Rejected)
                        .SumAsync(x => (decimal?)x.Amount, token) ?? 0;
                    decimal available = Math.Min(c.RefundableAmount, payment.Amount - directReserved - legacyReserved);
                    amount = request.Action == ComplaintStaffReviewEvent.RefundFull ? available : request.Amount;
                    if (amount is not > 0 || amount > available || amount != decimal.Truncate(amount.Value) ||
                        request.Action == ComplaintStaffReviewEvent.RefundPartial && amount >= available)
                        return Result.Failure(StaffComplaintAccess.Invalid, "Refund must be whole VND and within the verified available cap; partial refund must be below the cap.");
                    if (await db.DirectRefundCases.AnyAsync(x => x.BookingId == c.BookingId && x.SourceType == DirectRefundCase.SourcePostTripComplaint, token) ||
                        await db.Refunds.AnyAsync(x => x.ComplaintId == c.Id, token))
                        return Result.Failure(StaffComplaintAccess.Conflict, "A refund obligation already exists for this complaint.");
                    if (booking.PaymentFlow == PaymentFlow.OperatorDirect)
                    {
                        var created = await DirectRefundCaseCreator.CreateAsync(db, c.BookingId, DirectRefundCase.SourcePostTripComplaint,
                            (long)amount.Value, (long)payment.Amount, clock.UtcNow, c.Id, token, protector);
                        if (created.IsFailure) return Result.Failure(created.ErrorCode!, created.ErrorMessage!);
                    }
                    else await legacyRefunds.CreateForAcceptedComplaintAsync(c.Id, c.BookingId, amount.Value, clock.UtcNow, token);
                }
                if (request.Action == ComplaintStaffReviewEvent.RequestEvidence) c.RequestStaffEvidence();
                else c.ResolveByStaff(refund, amount);
                var review = ComplaintStaffReviewEvent.Create(c.Id, user.UserId!.Value, request.OperationId, fingerprint,
                    request.Action, request.Reason, request.ContractBasis, amount, request.TargetParty, clock.UtcNow);
                db.ComplaintStaffReviewEvents.Add(review);
                db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(user.UserId, "ComplaintStaffDecision", ComplaintStaffReviewEvent.AuditEntity,
                    c.Id, clock.UtcNow, AuditOutcome.Success, request.Reason.Trim(), afterData: JsonSerializer.Serialize(new { request.Action, request.ContractBasis, Amount = amount, request.OperationId })));
                await NotifyAsync(db, c, request.Action, clock.UtcNow, token);
                await db.SaveChangesAsync(token);
                return Result.Success();
            }, ct);
            return result.IsFailure ? Fail(result.ErrorCode!, result.ErrorMessage!) : await new GetStaffComplaintQueryHandler(db, user).Handle(new(request.ComplaintId), ct);
        }
        catch (DbUpdateConcurrencyException) { return Fail(StaffComplaintAccess.Conflict, "The complaint changed concurrently. Refresh and retry."); }
    }

    internal static string Fingerprint(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    internal static async Task NotifyAsync(IApplicationDbContext db, PostTripComplaint c, string action, DateTimeOffset now, CancellationToken ct)
    {
        var operatorId = await (from b in db.Bookings
                                join s in db.TourSchedules on b.TourScheduleId equals s.Id
                                join t in db.Tours on s.TourId equals t.Id
                                where b.Id == c.BookingId
                                select t.OperatorUserId).SingleAsync(ct);
        foreach (long recipient in new[] { c.TravelerUserId, operatorId }.Distinct())
            db.Notifications.Add(new Notification
            {
                UserId = recipient,
                Channel = NotificationChannel.InApp,
                Type = "ComplaintStaffReview",
                Title = "Complaint review updated",
                Body = $"Staff review: {action}.",
                RelatedEntityType = ComplaintStaffReviewEvent.AuditEntity,
                RelatedEntityId = c.Id,
                CreatedAtUtc = now
            });
    }
    private static Result<StaffComplaintDetail> Fail(string code, string message) => Result.Failure<StaffComplaintDetail>(code, message);
}