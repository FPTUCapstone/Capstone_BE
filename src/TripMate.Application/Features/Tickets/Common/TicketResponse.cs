using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Tickets.Common;

/// <summary>
/// UC-29 e-ticket as defined in docs/UC-29-api-contract.md. <see cref="Qr"/> is written as null, not
/// omitted, when the ticket cannot be used.
/// </summary>
public sealed record TicketResponse(
    long TicketId,
    string TicketCode,
    long BookingId,
    string BookingCode,
    TicketDisplayStatus TicketStatus,
    TicketTourResponse Tour,
    string OperatorName,
    TicketScheduleResponse Schedule,
    int ParticipantCount,
    TicketParticipantsResponse Participants,
    TicketValidityResponse Validity,
    DateTimeOffset? CheckedInAtUtc,
    TicketQrResponse? Qr);

public sealed record TicketTourResponse(long TourId, string Title);

public sealed record TicketScheduleResponse(DateTimeOffset StartAtUtc, DateTimeOffset EndAtUtc, string? MeetingPoint);

public sealed record TicketParticipantsResponse(int Adult, int Child, int Infant);

public sealed record TicketValidityResponse(
    DateOnly DepartureDate,
    string TimeZone,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ValidUntilUtc);

public sealed record TicketQrResponse(string Content, DateTimeOffset IssuedAtUtc);