using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Bookings.GetList;

/// <summary>
/// Read-only operational register for UC-62. It deliberately exposes only the payment state,
/// not gateway references, callback data, or participant details.
/// </summary>
public sealed record GetPlatformBookingsQuery(
    string? Keyword = null,
    BookingStatus? Status = null,
    PaymentStatus? PaymentStatus = null,
    DateTimeOffset? BookedFromUtc = null,
    DateTimeOffset? BookedToUtc = null,
    long? TourId = null,
    int PageNumber = 1,
    int PageSize = GetPlatformBookingsQuery.DefaultPageSize)
    : IRequest<Result<PlatformBookingsResponseDto>>
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;
    public const int MaximumKeywordLength = 200;
}

public sealed record PlatformBookingTravelerDto(
    string UserId,
    string FullName,
    string? Email);

public sealed record PlatformBookingTourDto(
    string TourId,
    string Title,
    string OperatorUserId,
    string OperatorName,
    string ScheduleId,
    DateTimeOffset StartAtUtc);

public sealed record PlatformBookingListItemDto(
    string BookingId,
    string BookingCode,
    PlatformBookingTravelerDto Traveler,
    PlatformBookingTourDto Tour,
    int Quantity,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TotalAmount,
    BookingStatus Status,
    PaymentStatus PaymentStatus,
    DateTimeOffset BookedAtUtc);

public sealed record PlatformBookingsResponseDto(
    int PageNumber,
    int PageSize,
    int TotalCount,
    int TotalPages,
    IReadOnlyList<PlatformBookingListItemDto> Items);
