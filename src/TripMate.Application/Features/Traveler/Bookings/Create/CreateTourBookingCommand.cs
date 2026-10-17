using System.Text.Json.Serialization;

using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Bookings.Create;

public sealed record CreateTourBookingParticipantRequest(
    string FullName,
    ParticipantType ParticipantType,
    string? PhoneNumber,
    string? Email,
    BookingIdDocumentType? IdDocumentType,
    string? IdDocumentNumber,
    string? Nationality);

public sealed record CreateTourBookingCommand(
    string TourScheduleId,
    IReadOnlyList<CreateTourBookingParticipantRequest> Participants,
    string? VoucherCode,
    Guid IdempotencyKey = default) : IRequest<Result<CreateTourBookingResponse>>;

public sealed record CreateTourBookingResponse(
    string BookingId,
    string BookingCode,
    string Status,
    string TourScheduleId,
    string Currency,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TotalAmount,
    DateTimeOffset PaymentExpiresAtUtc,
    DateTimeOffset CreatedAtUtc)
{
    /// <summary>Not serialized; tells the controller whether to answer 201 or 200.</summary>
    [JsonIgnore]
    public bool IsReplay { get; init; }
}
