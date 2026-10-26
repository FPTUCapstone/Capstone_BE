using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Tickets.Common;

/// <summary>Shared by view and refresh so both endpoints return the same body.</summary>
public static class TicketResponseBuilder
{
    public static TicketDisplayStatus StatusOf(TicketSnapshot snapshot, DateTimeOffset nowUtc) =>
        TicketDisplayStatusEvaluator.Evaluate(snapshot.BookingStatus, snapshot.TicketStatus,
            TicketValidityWindow.For(snapshot.StartAtUtc), nowUtc);

    /// <summary>Issues the first Active QR when the status allows one (D7); never changes a status (PC-04).</summary>
    public static async Task<TicketResponse> BuildAsync(IApplicationDbContext dbContext, TicketQrIssuer issuer,
        TicketSnapshot snapshot, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var window = TicketValidityWindow.For(snapshot.StartAtUtc);
        var status = TicketDisplayStatusEvaluator.Evaluate(snapshot.BookingStatus, snapshot.TicketStatus, window, nowUtc);

        TicketQrResponse? qr = null;
        if (TicketDisplayStatusEvaluator.ShowsQr(status))
        {
            var code = await issuer.EnsureActiveAsync(snapshot.TicketId, nowUtc, cancellationToken);
            qr = new TicketQrResponse(issuer.ContentFor(code), code.IssuedAtUtc);
        }

        var counts = await dbContext.BookingParticipants.AsNoTracking()
            .Where(participant => participant.BookingId == snapshot.BookingId)
            .GroupBy(participant => participant.ParticipantType)
            .Select(group => new { Type = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        int CountOf(ParticipantType type) => counts.SingleOrDefault(count => count.Type == type)?.Count ?? 0;

        return new TicketResponse(
            snapshot.TicketId,
            snapshot.TicketCode,
            snapshot.BookingId,
            snapshot.BookingCode,
            status,
            new TicketTourResponse(snapshot.TourId, snapshot.TourTitle),
            snapshot.OperatorName ?? string.Empty,
            new TicketScheduleResponse(snapshot.StartAtUtc, snapshot.EndAtUtc, snapshot.MeetingPoint),
            snapshot.Quantity,
            new TicketParticipantsResponse(CountOf(ParticipantType.Adult), CountOf(ParticipantType.Child),
                CountOf(ParticipantType.Infant)),
            new TicketValidityResponse(window.DepartureDate, TicketValidityWindow.TimeZoneId,
                window.ValidFromUtc, window.ValidUntilUtc),
            snapshot.CheckedInAtUtc,
            qr);
    }
}