using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Emergency.Common;

/// <summary>
/// Hands the paid bookings of a committed UC-70 decision (SRS UC-66) to the existing refund pipeline,
/// inside the decision's transaction. A cancellation before departure refunds 100% of each verified,
/// not yet refunded payment (BR-136); everything else that was paid goes to Administrator review.
/// It never calls a payment gateway and never changes payment or payout state.
/// </summary>
public static class EmergencyRefundHandoff
{
    public const string WeatherCancellationRefundReason =
        "Tour bị hủy trước giờ khởi hành do thời tiết nguy hiểm. Hoàn 100% số tiền đã thanh toán.";
    public const string OperatorCancellationRefundReason =
        "Tour bị đơn vị điều hành hủy trước giờ khởi hành. Hoàn 100% số tiền đã thanh toán.";

    private const string KeyScope = "TripMate.UC70.RefundHandoff";
    private const int NotificationBodyMaxLength = 1000; // dbo.Notifications.body

    /// <summary>
    /// The refund's cancellation-operation key: fixed by the UC-70 command and the booking, so a replay
    /// or a retried transaction always names the same operation and can never add a second refund.
    /// </summary>
    public static Guid DeriveIdempotencyKey(Guid commandId, long bookingId) =>
        new(Digest(commandId, bookingId).AsSpan(0, 16));

    public static async Task<EmergencyRefundHandoffResult> ApplyAsync(
        IApplicationDbContext dbContext,
        long operatorUserId,
        Guid commandId,
        TourSchedule schedule,
        IReadOnlyCollection<Booking> bookings,
        bool isPreStart,
        EmergencyTourReason reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var submitted = new List<(Booking Booking, PaymentTransaction Payment, BookingCancellationOperation Operation)>();
        var review = new List<(Booking Booking, string Cause)>();

        foreach (Booking booking in bookings.OrderBy(candidate => candidate.Id))
        {
            if (booking.PaymentStatus == PaymentStatus.Unpaid)
                continue;
            if (!isPreStart)
            {
                review.Add((booking, "phần chưa thực hiện của tour"));
                continue;
            }

            bool alreadyRefunded = booking.PaymentStatus != PaymentStatus.Paid
                || await dbContext.Refunds.AnyAsync(refund => refund.BookingId == booking.Id, cancellationToken);
            if (alreadyRefunded)
            {
                review.Add((booking, "đã có yêu cầu hoàn tiền trước đó"));
                continue;
            }

            PaymentTransaction? payment = await BookingCancellationPaymentVerifier
                .FindSuccessfulPaymentAsync(dbContext, booking, cancellationToken);
            if (payment is null)
            {
                review.Add((booking, "chưa xác minh được giao dịch thanh toán"));
                continue;
            }

            var operation = BookingCancellationOperation.Create(booking.Id, operatorUserId,
                BookingCancellationActor.TourOperator, DeriveIdempotencyKey(commandId, booking.Id),
                Convert.ToHexString(Digest(commandId, booking.Id)).ToLowerInvariant(),
                booking.TotalAmount, nowUtc);
            dbContext.BookingCancellationOperations.Add(operation);
            submitted.Add((booking, payment, operation));
        }

        if (submitted.Count > 0)
        {
            // A refund names its operation by id, which exists only after this save.
            await dbContext.SaveChangesAsync(cancellationToken);
            string refundReason = reason == EmergencyTourReason.WeatherEmergency
                ? WeatherCancellationRefundReason
                : OperatorCancellationRefundReason;
            foreach (var (booking, payment, operation) in submitted)
            {
                dbContext.Refunds.Add(Refund.CreatePending(booking.Id, payment.Id, operation.Id,
                    booking.TotalAmount, refundReason, RefundInitiator.TourOperator, nowUtc));
            }
        }

        if (review.Count > 0)
        {
            await AdministratorInAppNotices.AddAsync(dbContext,
                Notification.EmergencyRefundReviewRequiredType,
                Notification.EmergencyRefundReviewTitle,
                ComposeReviewBody(schedule, isPreStart, reason, review),
                AuditEntityTypes.TourSchedule, schedule.Id, nowUtc, cancellationToken);
        }

        return new EmergencyRefundHandoffResult(
            [.. submitted.Select(item => new EmergencySubmittedRefund(item.Booking.Id, item.Booking.TotalAmount))],
            [.. review.Select(item => item.Booking.Id)]);
    }

    /// <summary>Recomputes, for a replay, how many refunds the committed decision submitted.</summary>
    public static async Task<int> CountSubmittedAsync(
        IApplicationDbContext dbContext,
        EmergencyTourCancellationOperation decision,
        CancellationToken cancellationToken)
    {
        List<long> bookingIds = await dbContext.Bookings.AsNoTracking()
            .Where(booking => booking.TourScheduleId == decision.TourScheduleId)
            .Select(booking => booking.Id)
            .ToListAsync(cancellationToken);
        List<Guid> keys = [.. bookingIds.Select(bookingId => DeriveIdempotencyKey(decision.CommandId, bookingId))];
        return await dbContext.BookingCancellationOperations.AsNoTracking()
            .CountAsync(operation => operation.ActorUserId == decision.ActorUserId
                && keys.Contains(operation.IdempotencyKey), cancellationToken);
    }

    /// <summary>Whether the committed decision submitted a refund for this booking.</summary>
    public static Task<bool> HasSubmittedRefundAsync(
        IApplicationDbContext dbContext,
        EmergencyTourCancellationOperation decision,
        long bookingId,
        CancellationToken cancellationToken)
    {
        Guid key = DeriveIdempotencyKey(decision.CommandId, bookingId);
        return dbContext.Refunds.AsNoTracking().AnyAsync(refund => refund.BookingId == bookingId
            && dbContext.BookingCancellationOperations.Any(operation =>
                operation.Id == refund.CancellationOperationId
                && operation.ActorUserId == decision.ActorUserId
                && operation.IdempotencyKey == key), cancellationToken);
    }

    private static string ComposeReviewBody(
        TourSchedule schedule,
        bool isPreStart,
        EmergencyTourReason reason,
        IReadOnlyList<(Booking Booking, string Cause)> review)
    {
        string decision = isPreStart
            ? "đã bị hủy trước giờ khởi hành"
            : "đã dừng khẩn cấp khi tour đang diễn ra";
        var body = new StringBuilder(
            $"Lịch khởi hành #{schedule.Id} {decision} ({ReasonLabel(reason)}). "
            + $"{review.Count} booking đã thanh toán cần xem xét hoàn tiền: ");
        const int reserve = 40; // Room for the "và N booking khác" tail.
        for (int index = 0; index < review.Count; index++)
        {
            var (booking, cause) = review[index];
            string line = $"{(index == 0 ? string.Empty : "; ")}{Label(booking)} "
                + $"{FormatAmount(booking.TotalAmount)} ({cause})";
            if (body.Length + line.Length > NotificationBodyMaxLength - reserve)
            {
                body.Append($"; và {review.Count - index} booking khác");
                break;
            }

            body.Append(line);
        }

        return body.Append('.').ToString();
    }

    private static string Label(Booking booking) =>
        string.IsNullOrWhiteSpace(booking.BookingCode) ? $"#{booking.Id}" : booking.BookingCode;

    private static string FormatAmount(decimal amount) =>
        amount.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', '.') + " đ";

    private static string ReasonLabel(EmergencyTourReason reason) => reason switch
    {
        EmergencyTourReason.WeatherEmergency => "thời tiết nguy hiểm",
        EmergencyTourReason.AuthorityDirective => "chỉ đạo của cơ quan chức năng",
        EmergencyTourReason.DestinationUnavailable => "điểm đến không hoạt động",
        EmergencyTourReason.TransportFailure => "sự cố phương tiện",
        EmergencyTourReason.SafetyIncident => "sự cố an toàn",
        _ => "lý do khác",
    };

    private static byte[] Digest(Guid commandId, long bookingId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', KeyScope,
            commandId.ToString("D"), bookingId.ToString(CultureInfo.InvariantCulture))));
}

public sealed record EmergencySubmittedRefund(long BookingId, decimal Amount);

public sealed record EmergencyRefundHandoffResult(
    IReadOnlyList<EmergencySubmittedRefund> SubmittedRefunds,
    IReadOnlyList<long> AdminReviewBookingIds);