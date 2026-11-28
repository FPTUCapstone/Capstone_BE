using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.CommissionStatements;

public sealed record IssueDirectCommissionStatementCommand(long TourOperatorUserId, string Period)
    : IRequest<Result<IssueDirectCommissionStatementResponse>>;
public sealed record IssueDirectCommissionStatementResponse(long StatementId, long CommissionAmountVnd);

public sealed class IssueDirectCommissionStatementCommandHandler(IApplicationDbContext db, ICurrentUserService user, IDateTimeProvider clock)
    : IRequestHandler<IssueDirectCommissionStatementCommand, Result<IssueDirectCommissionStatementResponse>>
{
    private sealed record BookingRow(long Id, BookingStatus Status, decimal Amount, DateTimeOffset Start, DateTimeOffset End);
    private sealed record RefundRow(long Id, long BookingId, string SourceType, decimal Amount, bool Completed, DateTimeOffset? CompletedAt);
    private sealed record Line(long BookingId, string SourceType, long SourceId, long PaymentId, long Base, decimal Rate, long Fee, DateTimeOffset At);

    public async Task<Result<IssueDirectCommissionStatementResponse>> Handle(IssueDirectCommissionStatementCommand request, CancellationToken ct)
    {
        if (!DirectCommissionAccess.Allowed(user, true)) return Fail("commission.forbidden", "Administration access required.");
        if (request.TourOperatorUserId <= 0 || !DirectCommissionAccess.ValidPeriod(request.Period))
            return Fail("commission.invalid_period", "Select a valid operator and monthly period.");
        var date = DateOnly.ParseExact(request.Period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (date.Year == 9999 && date.Month == 12) return Fail("commission.invalid_period", "Period is outside the supported range.");
        var periodEnd = Start(date.AddMonths(1));
        var now = clock.UtcNow;
        if (periodEnd > now) return Fail("commission.invalid_period", "Only closed Vietnam calendar months may be issued.");
        try
        {
            return await db.ExecuteInSerializableTransactionAsync(async token =>
            {
                await db.LockDirectCommissionIssuerAsync(request.TourOperatorUserId, token);
                var statements = await db.DirectCommissionStatements.AsNoTracking()
                    .Where(s => s.TourOperatorUserId == request.TourOperatorUserId).ToListAsync(token);
                if (statements.Any(s => s.Period == request.Period)) return Fail("commission.duplicate_period", "A statement for this month already exists.");
                var latest = statements.OrderByDescending(s => s.Period, StringComparer.Ordinal).FirstOrDefault();
                if (latest is not null && string.CompareOrdinal(request.Period, latest.Period) <= 0)
                    return Fail("commission.invalid_period", "Issue months in chronological order; existing periods are immutable.");
                var ratePercent = await db.OperatorProfiles.AsNoTracking().Where(p => p.UserId == request.TourOperatorUserId)
                    .Select(p => (decimal?)p.CommissionRate).SingleOrDefaultAsync(token);
                if (ratePercent is null or <= 0 or > 100) return Fail("commission.invalid_operator", "The operator must have a valid commission rate.");
                decimal rate = ratePercent.Value / 100m;
                var statementIds = statements.Select(s => s.Id).ToArray();
                var calculations = await db.DirectCommissionCalculations.AsNoTracking().Where(c => statementIds.Contains(c.StatementId)).ToListAsync(token);
                long brought = latest is null ? 0 : calculations.SingleOrDefault(c => c.StatementId == latest.Id)?.CreditCarriedForwardVnd ?? 0;
                var calculationIds = calculations.Select(c => c.StatementId).ToHashSet();
                var legacy = statements.Where(s => !calculationIds.Contains(s.Id)).OrderByDescending(s => s.Period, StringComparer.Ordinal).FirstOrDefault();
                DateTimeOffset? baseline = legacy is null ? null : Start(DateOnly.ParseExact(legacy.Period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).AddMonths(1));
                var ledger = await db.DirectCommissionLedgerEntries.AsNoTracking().Where(e => statementIds.Contains(e.StatementId)).ToListAsync(token);
                var debits = ledger.Where(e => e.SourceType == DirectCommissionLedgerEntry.BookingRevenue).ToDictionary(e => e.BookingId);
                var billedBookingIds = debits.Keys.ToArray();
                var rows = await (from b in db.Bookings.AsNoTracking()
                                  join s in db.TourSchedules.AsNoTracking() on b.TourScheduleId equals s.Id
                                  join t in db.Tours.AsNoTracking() on s.TourId equals t.Id
                                  where t.OperatorUserId == request.TourOperatorUserId && b.PaymentFlow == PaymentFlow.OperatorDirect && s.EndAtUtc < periodEnd &&
                                      (billedBookingIds.Contains(b.Id) ||
                                          ((b.Status == BookingStatus.Completed || b.Status == BookingStatus.Confirmed) &&
                                              s.Status != TourScheduleStatus.Cancelled && s.Status != TourScheduleStatus.EmergencyStopped))
                                  select new BookingRow(b.Id, b.Status, b.TotalAmount, s.StartAtUtc, s.EndAtUtc)).ToListAsync(token);
                var ids = rows.Select(b => b.Id).ToArray();
                var payments = await db.PaymentTransactions.AsNoTracking().Where(p => ids.Contains(p.BookingId) &&
                    p.TransactionType == PaymentTransaction.TypePayment && p.Status == PaymentTransactionStatus.Success &&
                    p.Currency == PaymentTransaction.DefaultCurrency).ToListAsync(token);
                var direct = await db.DirectRefundCases.AsNoTracking().Where(r => ids.Contains(r.BookingId))
                    .Select(r => new RefundRow(r.Id, r.BookingId, DirectCommissionLedgerEntry.DirectRefundCredit, r.RefundAmountVnd,
                        r.Status == DirectRefundCase.StatusCompleted, r.CompletedAtUtc)).ToListAsync(token);
                var oldRefunds = await db.Refunds.AsNoTracking().Where(r => ids.Contains(r.BookingId) && r.Status != RefundStatus.Rejected)
                    .Select(r => new RefundRow(r.Id, r.BookingId, DirectCommissionLedgerEntry.LegacyRefundCredit, r.Amount,
                        r.Status == RefundStatus.Processed, r.ProcessedAtUtc)).ToListAsync(token);
                var refunds = direct.Concat(oldRefunds).GroupBy(r => r.BookingId).ToDictionary(g => g.Key, g => g.ToList());
                var complaints = await db.PostTripComplaints.AsNoTracking().Where(c => ids.Contains(c.BookingId)).ToListAsync(token);
                var complaintIds = complaints.Select(c => c.Id).ToArray();
                var resolutions = await db.ComplaintStaffReviewEvents.AsNoTracking().Where(e => complaintIds.Contains(e.ComplaintId) &&
                    (e.Action == ComplaintStaffReviewEvent.Reject || e.Action == ComplaintStaffReviewEvent.RefundFull || e.Action == ComplaintStaffReviewEvent.RefundPartial))
                    .Select(e => new { e.ComplaintId, At = e.CreatedAtUtc }).ToListAsync(token);
                var accepted = await db.PostTripComplaintNegotiationEvents.AsNoTracking().Where(e => complaintIds.Contains(e.ComplaintId) && e.EventType == PostTripComplaintNegotiationEventType.Accepted)
                    .Select(e => new { e.ComplaintId, At = e.CreatedAtUtc }).ToListAsync(token);
                var resolvedAt = resolutions.Concat(accepted).GroupBy(e => e.ComplaintId).ToDictionary(g => g.Key, g => g.Max(e => e.At));
                List<Line> lines = [];
                long allocatedRevenue = 0, allocatedFee = 0;
                int held = 0, ambiguous = 0;
                foreach (var booking in rows.OrderBy(b => b.Id))
                {
                    debits.TryGetValue(booking.Id, out var debit);
                    var sourceRefunds = refunds.GetValueOrDefault(booking.Id) ?? [];
                    var pendingRefunds = sourceRefunds.Where(r => !ledger.Any(e => e.SourceType == r.SourceType && e.SourceId == r.Id)).ToList();
                    if (debit is not null && pendingRefunds.Count == 0) continue;
                    if (debit is null && baseline is { } cutover && booking.Start < cutover) { ambiguous++; continue; }
                    var payment = debit is not null ? payments.SingleOrDefault(p => p.Id == debit.PaymentTransactionId)
                        : payments.Where(p => p.BookingId == booking.Id && p.Amount == booking.Amount).OrderByDescending(p => p.SucceededAtUtc ?? p.CreatedAtUtc).ThenByDescending(p => p.Id).FirstOrDefault();
                    if (payment is null || !Whole(payment.Amount) || payment.Amount != booking.Amount) continue;
                    var effective = new[] { booking.End, payment.SucceededAtUtc ?? payment.CreatedAtUtc }.Max();
                    if (effective >= periodEnd) continue;
                    var bookingComplaints = complaints.Where(c => c.BookingId == booking.Id).ToList();
                    bool complaintPending = bookingComplaints.Any(c => c.Status is not (PostTripComplaintStatus.Approved or PostTripComplaintStatus.Rejected) ||
                        (resolvedAt.GetValueOrDefault(c.Id, c.RespondedAtUtc ?? c.SubmittedAtUtc) >= periodEnd));
                    if (complaintPending || sourceRefunds.Any(r => !r.Completed || r.CompletedAt is null || r.CompletedAt >= periodEnd)) { held++; continue; }
                    if (sourceRefunds.Any(r => !Whole(r.Amount)) || sourceRefunds.Sum(r => r.Amount) > payment.Amount) { held++; continue; }
                    decimal originalRate = debit?.CommissionRate ?? rate;
                    long revenue = debit?.BaseAmountVnd ?? (long)payment.Amount;
                    long originalFee;
                    if (debit is not null) originalFee = debit.CommissionAmountVnd;
                    else
                    {
                        allocatedRevenue = checked(allocatedRevenue + revenue);
                        long cumulativeFee = Round(allocatedRevenue * rate);
                        originalFee = cumulativeFee - allocatedFee;
                        allocatedFee = cumulativeFee;
                    }
                    long creditedBase = -ledger.Where(e => e.BookingId == booking.Id && e.SourceType != DirectCommissionLedgerEntry.BookingRevenue).Sum(e => e.BaseAmountVnd);
                    long creditedFee = -ledger.Where(e => e.BookingId == booking.Id && e.SourceType != DirectCommissionLedgerEntry.BookingRevenue).Sum(e => e.CommissionAmountVnd);
                    if (debit is null) lines.Add(new(booking.Id, DirectCommissionLedgerEntry.BookingRevenue, booking.Id, payment.Id, revenue, rate, originalFee, effective));
                    foreach (var refund in pendingRefunds.OrderBy(r => r.CompletedAt).ThenBy(r => r.SourceType, StringComparer.Ordinal).ThenBy(r => r.Id))
                    {
                        long cumulativeBase = checked(creditedBase + (long)refund.Amount);
                        long targetCredit = Math.Min(originalFee, Round(cumulativeBase * originalRate));
                        long credit = Math.Max(0, targetCredit - creditedFee);
                        lines.Add(new(booking.Id, refund.SourceType, refund.Id, payment.Id, -(long)refund.Amount, originalRate, -credit, refund.CompletedAt!.Value));
                        creditedBase = cumulativeBase; creditedFee += credit;
                    }
                }
                if (lines.Count == 0 && brought == 0 && held == 0 && ambiguous == 0)
                    return Fail("commission.no_eligible_base", "No unbilled eligible revenue, adjustments, credits or held cases for this period.");
                long gross = lines.Where(l => l.SourceType == DirectCommissionLedgerEntry.BookingRevenue).Sum(l => l.Base);
                long refundsVnd = -lines.Where(l => l.SourceType != DirectCommissionLedgerEntry.BookingRevenue).Sum(l => l.Base);
                long debitFee = lines.Where(l => l.SourceType == DirectCommissionLedgerEntry.BookingRevenue).Sum(l => l.Fee);
                long refundFee = -lines.Where(l => l.SourceType != DirectCommissionLedgerEntry.BookingRevenue).Sum(l => l.Fee);
                long due = Math.Max(0, checked(debitFee - refundFee - brought));
                var issueDate = DateOnly.FromDateTime(now.ToOffset(DirectCommissionAccess.VietnamOffset).DateTime);
                var statement = DirectCommissionStatement.Issue($"DCO-{request.TourOperatorUserId:D6}-{request.Period}", request.TourOperatorUserId,
                    request.Period, Math.Max(0, gross - refundsVnd), rate, due, issueDate, now);
                db.DirectCommissionStatements.Add(statement);
                db.DirectCommissionCalculations.Add(DirectCommissionCalculation.Create(statement, gross, refundsVnd, debitFee, refundFee, brought, held, ambiguous, now));
                foreach (var line in lines) db.DirectCommissionLedgerEntries.Add(DirectCommissionLedgerEntry.Create(statement, line.BookingId,
                    line.SourceType, line.SourceId, line.PaymentId, line.Base, line.Rate, line.Fee, line.At, now));
                db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(user.UserId, "DirectCommissionStatementIssued", "DirectCommissionStatement", null,
                    now, AuditOutcome.Success, afterData: System.Text.Json.JsonSerializer.Serialize(new { request.TourOperatorUserId, request.Period, Amount = due, Lines = lines.Count, Held = held, AmbiguousLegacy = ambiguous })));
                await db.SaveChangesAsync(token);
                return Result.Success(new IssueDirectCommissionStatementResponse(statement.Id, statement.CommissionAmountVnd));
            }, ct);
        }
        catch (DbUpdateException) { return Fail("commission.payment_conflict", "Another statement was issued concurrently. Refresh before retrying."); }
    }
    private static bool Whole(decimal amount) => amount > 0 && amount == decimal.Truncate(amount) && amount <= long.MaxValue;
    private static long Round(decimal value) => checked((long)Math.Round(value, MidpointRounding.AwayFromZero));
    private static DateTimeOffset Start(DateOnly date) => new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, DirectCommissionAccess.VietnamOffset).ToUniversalTime();
    private static Result<IssueDirectCommissionStatementResponse> Fail(string code, string message) => Result.Failure<IssueDirectCommissionStatementResponse>(code, message);
}