using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Tickets.Common;

/// <summary>
/// Issues and rotates the single Active QR of a ticket. UX_TicketQrCodes_OneActivePerTicket decides
/// concurrent first views and refreshes; the loser re-reads and returns the winner's QR (plan A4).
/// </summary>
public sealed class TicketQrIssuer(
    IApplicationDbContext dbContext,
    ITicketQrCodec codec,
    ITicketQrPersistenceErrorClassifier classifier)
{
    public string ContentFor(TicketQrCode code) => codec.ContentFor(code.QrToken, code.Signature);

    /// <summary>D7: the QR is created lazily the first time a usable ticket is viewed.</summary>
    public async Task<TicketQrCode> EnsureActiveAsync(long ticketId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var active = await FindActiveAsync(ticketId, cancellationToken);
        if (active is not null)
            return active;

        try
        {
            var code = NewCode(ticketId, nowUtc);
            dbContext.TicketQrCodes.Add(code);
            await dbContext.SaveChangesAsync(cancellationToken);
            return code;
        }
        catch (DbUpdateException exception) when (classifier.IsOneActiveCodeConflict(exception))
        {
            return await ReadWinnerAsync(ticketId, cancellationToken);
        }
    }

    /// <summary>D5: revokes the Active QR and issues a new one in one transaction.</summary>
    public async Task<TicketQrCode> RotateAsync(long ticketId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.ExecuteInTransactionAsync(async transactionToken =>
            {
                var active = await dbContext.TicketQrCodes
                    .Where(code => code.TicketId == ticketId && code.Status == TicketQrCodeStatus.Active)
                    .ToListAsync(transactionToken);
                foreach (var code in active)
                    code.Revoke();
                await dbContext.SaveChangesAsync(transactionToken);

                var replacement = NewCode(ticketId, nowUtc);
                dbContext.TicketQrCodes.Add(replacement);
                await dbContext.SaveChangesAsync(transactionToken);
                return replacement;
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (classifier.IsOneActiveCodeConflict(exception))
        {
            return await ReadWinnerAsync(ticketId, cancellationToken);
        }
    }

    private TicketQrCode NewCode(long ticketId, DateTimeOffset nowUtc)
    {
        var payload = codec.Issue(ticketId, nowUtc);
        return TicketQrCode.Issue(ticketId, payload.QrToken, payload.Signature, nowUtc);
    }

    private async Task<TicketQrCode> ReadWinnerAsync(long ticketId, CancellationToken cancellationToken)
    {
        dbContext.ClearTrackedEntities();
        return await FindActiveAsync(ticketId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Ticket {ticketId} lost a QR issuance race but no Active QR exists.");
    }

    private Task<TicketQrCode?> FindActiveAsync(long ticketId, CancellationToken cancellationToken) =>
        dbContext.TicketQrCodes.AsNoTracking()
            .Where(code => code.TicketId == ticketId && code.Status == TicketQrCodeStatus.Active)
            .OrderByDescending(code => code.Id)
            .FirstOrDefaultAsync(cancellationToken);
}