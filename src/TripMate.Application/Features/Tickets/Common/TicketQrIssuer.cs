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
    /// <summary>Bounded so a fault the classifier misreads can never loop forever.</summary>
    private const int MaxIssueAttempts = 5;

    public string ContentFor(TicketQrCode code) => codec.ContentFor(code.QrToken, code.Signature);

    /// <summary>
    /// D7: the QR is created lazily the first time a usable ticket is viewed. Losing the race on the
    /// one-Active index is retried: the winner may itself have been revoked by a concurrent refresh
    /// before this request reads it, so the loop either finds an Active QR or issues one.
    /// </summary>
    public async Task<TicketQrCode> EnsureActiveAsync(long ticketId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
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
            catch (DbUpdateException exception)
                when (attempt < MaxIssueAttempts && classifier.IsOneActiveCodeConflict(exception))
            {
                dbContext.ClearTrackedEntities();
            }
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
            // A concurrent refresh won; return its QR, or issue one if that QR was revoked meanwhile.
            dbContext.ClearTrackedEntities();
            return await EnsureActiveAsync(ticketId, nowUtc, cancellationToken);
        }
    }

    private TicketQrCode NewCode(long ticketId, DateTimeOffset nowUtc)
    {
        var payload = codec.Issue(ticketId, nowUtc);
        return TicketQrCode.Issue(ticketId, payload.QrToken, payload.Signature, nowUtc);
    }

    private Task<TicketQrCode?> FindActiveAsync(long ticketId, CancellationToken cancellationToken) =>
        dbContext.TicketQrCodes.AsNoTracking()
            .Where(code => code.TicketId == ticketId && code.Status == TicketQrCodeStatus.Active)
            .OrderByDescending(code => code.Id)
            .FirstOrDefaultAsync(cancellationToken);
}