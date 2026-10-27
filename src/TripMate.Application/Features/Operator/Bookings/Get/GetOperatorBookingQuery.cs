using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Bookings.List;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Bookings.Get;

/// <summary>UC-40 detail: one booking on a tour owned by the signed-in Tour Operator. Read-only.</summary>
public sealed record GetOperatorBookingQuery(long BookingId) : IRequest<Result<OperatorBookingDetailDto>>;

public sealed record OperatorBookingDetailScheduleDto(
    string ScheduleId,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    string? MeetingPoint);

public sealed record OperatorBookingDetailBookerDto(
    string UserId,
    string FullName,
    string? Email,
    string? PhoneNumber);

/// <summary>Participant whitelist: name and type only. ID documents, phone, email and nationality never leave the server.</summary>
public sealed record OperatorBookingParticipantDto(string FullName, ParticipantType ParticipantType);

public sealed record OperatorBookingTicketDto(
    string TicketCode,
    TicketStatus Status,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

/// <summary>Read-only projection of the verified payment (BR-77). Gateway payloads and checkout data are not exposed.</summary>
public sealed record OperatorBookingPaymentDto(
    PaymentGateway Gateway,
    string? GatewayTransactionRef,
    PaymentTransactionStatus Status,
    decimal Amount,
    DateTimeOffset? SucceededAtUtc);

public sealed record OperatorBookingCouponDto(string Code, string Name, decimal DiscountAmount);

public sealed record OperatorBookingDetailDto(
    string BookingId,
    string BookingCode,
    BookingStatus Status,
    PaymentStatus PaymentStatus,
    DateTimeOffset BookedAtUtc,
    DateTimeOffset? CancelledAtUtc,
    string? CancelReason,
    OperatorBookingTourDto Tour,
    OperatorBookingDetailScheduleDto Schedule,
    OperatorBookingDetailBookerDto Booker,
    int Quantity,
    IReadOnlyList<OperatorBookingParticipantDto> Participants,
    OperatorBookingTicketDto? Ticket,
    string CheckInStatus,
    DateTimeOffset? CheckedInAtUtc,
    OperatorBookingPaymentDto? Payment,
    decimal PaidAmount,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TotalAmount,
    OperatorBookingCouponDto? Coupon,
    bool CanCancel,
    string? CancelBlockedReason);
