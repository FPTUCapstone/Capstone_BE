using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Bookings.List;

/// <summary>
/// UC-40 list: customer bookings placed on the signed-in Tour Operator's own tours. Read-only.
/// Departure dates are Vietnam-time calendar days (CR-07), both bounds inclusive.
/// </summary>
public sealed record GetOperatorBookingsQuery(
    long? TourId = null,
    long? ScheduleId = null,
    BookingStatus? Status = null,
    PaymentStatus? PaymentStatus = null,
    DateOnly? DepartureFrom = null,
    DateOnly? DepartureTo = null,
    string? Keyword = null,
    int PageNumber = 1,
    int PageSize = GetOperatorBookingsQuery.DefaultPageSize)
    : IRequest<Result<OperatorBookingsResponseDto>>
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;
    public const int MaximumKeywordLength = 200;
}

public sealed record OperatorBookingTourDto(string TourId, string Title);

public sealed record OperatorBookingScheduleDto(
    string ScheduleId,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc);

public sealed record OperatorBookingBookerDto(string UserId, string FullName, string? Email);

public sealed record OperatorBookingListItemDto(
    string BookingId,
    string BookingCode,
    OperatorBookingTourDto Tour,
    OperatorBookingScheduleDto Schedule,
    OperatorBookingBookerDto Booker,
    int Quantity,
    decimal TotalAmount,
    decimal DiscountAmount,
    BookingStatus Status,
    PaymentStatus PaymentStatus,
    string CheckInStatus,
    DateTimeOffset? CheckedInAtUtc,
    DateTimeOffset BookedAtUtc);

public sealed record OperatorBookingSummaryDto(
    int TotalBookings,
    int TotalParticipants,
    decimal TotalConfirmedAmount);

public sealed record OperatorBookingsResponseDto(
    int PageNumber,
    int PageSize,
    int TotalCount,
    int TotalPages,
    OperatorBookingSummaryDto Summary,
    IReadOnlyList<OperatorBookingListItemDto> Items);
