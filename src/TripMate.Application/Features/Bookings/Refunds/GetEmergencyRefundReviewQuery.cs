using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Refunds;

public sealed record EmergencyRefundReviewBooking(long BookingId, string BookingCode, decimal BookingAmount,
    string PaymentStatus, string? RefundStatus);

public sealed record GetEmergencyRefundReviewQuery(long ScheduleId)
    : IRequest<Result<IReadOnlyList<EmergencyRefundReviewBooking>>>;

public sealed class GetEmergencyRefundReviewQueryHandler(IApplicationDbContext db)
    : IRequestHandler<GetEmergencyRefundReviewQuery, Result<IReadOnlyList<EmergencyRefundReviewBooking>>>
{
    public async Task<Result<IReadOnlyList<EmergencyRefundReviewBooking>>> Handle(
        GetEmergencyRefundReviewQuery request, CancellationToken token)
    {
        var audit = await db.AuditLogs.AsNoTracking()
            .Where(log => log.AffectedEntityId == request.ScheduleId && log.AffectedEntity == AuditEntityTypes.TourSchedule
                && log.Result == AuditOutcome.Success
                && (log.ActionType == AuditActionTypes.TourEmergencyCancelled || log.ActionType == AuditActionTypes.TourEmergencyStopped))
            .OrderByDescending(log => log.Id)
            .Select(log => log.AfterData)
            .FirstOrDefaultAsync(token);
        if (audit is null) return Result.Success<IReadOnlyList<EmergencyRefundReviewBooking>>([]);

        using var document = JsonDocument.Parse(audit);
        if (!document.RootElement.TryGetProperty("RefundHandoff", out var handoff)
            || handoff.ValueKind != JsonValueKind.Object
            || !handoff.TryGetProperty("AdminReviewBookingIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return Result.Success<IReadOnlyList<EmergencyRefundReviewBooking>>([]);

        var reviewIds = ids.EnumerateArray().Select(item => item.GetInt64()).ToArray();
        var bookings = await db.Bookings.AsNoTracking()
            .Where(booking => booking.TourScheduleId == request.ScheduleId && reviewIds.Contains(booking.Id))
            .OrderBy(booking => booking.Id)
            .Select(booking => new
            {
                booking.Id,
                booking.BookingCode,
                booking.TotalAmount,
                booking.PaymentStatus,
                RefundStatus = db.Refunds.Where(refund => refund.BookingId == booking.Id)
                    .OrderByDescending(refund => refund.Id).Select(refund => (RefundStatus?)refund.Status).FirstOrDefault()
            })
            .ToListAsync(token);
        return Result.Success<IReadOnlyList<EmergencyRefundReviewBooking>>(
            bookings.Select(booking => new EmergencyRefundReviewBooking(booking.Id, booking.BookingCode,
                booking.TotalAmount, booking.PaymentStatus.ToString(), booking.RefundStatus?.ToString())).ToList());
    }
}