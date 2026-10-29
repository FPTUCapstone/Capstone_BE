using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Tickets.Common;

/// <summary>
/// Issues and rotates the single Active QR of a ticket. UX_TicketQrCodes_OneActivePerTicket decides
/// concurrent first views and refreshes; the loser re-reads and returns the winner's QR (plan A4).
/// Hardened under TM-89 / UC-29 Option B to transactionally verify ticket validity and prevent
/// issuing or returning Active QR codes for Used or Cancelled tickets.
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
    /// D7: the QR is created lazily the first time a usable ticket is viewed.
    /// Hardened: Validates ticket status inside the transaction under serialization.
    /// If the ticket is no longer usable (e.g. Used, Cancelled, Expired), returns null without issuing an Active QR.
    /// </summary>
    public async Task<TicketQrCode?> EnsureActiveAsync(long ticketId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await dbContext.ExecuteInSerializableTransactionAsync(async transactionToken =>
                {
                    var ticketInfo = await dbContext.Tickets
                        .Where(t => t.Id == ticketId)
                        .Select(t => new { t.Status, BookingStatus = t.Booking.Status, t.Booking.TourSchedule.StartAtUtc })
                        .SingleOrDefaultAsync(transactionToken);

                    if (ticketInfo is null)
                        return null;

                    var window = TicketValidityWindow.For(ticketInfo.StartAtUtc);
                    var currentStatus = TicketDisplayStatusEvaluator.Evaluate(
                        ticketInfo.BookingStatus, ticketInfo.Status, window, nowUtc);

                    if (!TicketDisplayStatusEvaluator.ShowsQr(currentStatus))
                    {
                        var lingering = await dbContext.TicketQrCodes
                            .Where(c => c.TicketId == ticketId && c.Status == TicketQrCodeStatus.Active)
                            .ToListAsync(transactionToken);

                        if (lingering.Count > 0)
                        {
                            foreach (var activeCode in lingering)
                                activeCode.Revoke();
                            await dbContext.SaveChangesAsync(transactionToken);
                        }

                        return null;
                    }

                    var active = await dbContext.TicketQrCodes
                        .Where(code => code.TicketId == ticketId && code.Status == TicketQrCodeStatus.Active)
                        .OrderByDescending(code => code.Id)
                        .FirstOrDefaultAsync(transactionToken);

                    if (active is not null)
                        return active;

                    var code = NewCode(ticketId, nowUtc);
                    dbContext.TicketQrCodes.Add(code);
                    await dbContext.SaveChangesAsync(transactionToken);
                    return code;
                }, cancellationToken);
            }
            catch (DbUpdateException exception)
                when (attempt < MaxIssueAttempts && classifier.IsOneActiveCodeConflict(exception))
            {
                dbContext.ClearTrackedEntities();
            }
            catch (Exception exception)
                when (attempt < MaxIssueAttempts && dbContext.IsDeadlock(exception))
            {
                dbContext.ClearTrackedEntities();
                var delayMs = Random.Shared.Next(15, 40) * (1 << (attempt - 1));
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
            }
        }
    }

    /// <summary>
    /// D5: revokes the Active QR and issues a new one in one transaction.
    /// Hardened: Validates ticket status inside the transaction under serialization.
    /// If the ticket is no longer refreshable (e.g. Used or Cancelled), aborts and returns Failure.
    /// </summary>
    public async Task<Result<TicketQrCode>> RotateAsync(long ticketId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await dbContext.ExecuteInSerializableTransactionAsync(async transactionToken =>
                {
                    var ticketInfo = await dbContext.Tickets
                        .Where(t => t.Id == ticketId)
                        .Select(t => new { t.Status, BookingStatus = t.Booking.Status, t.Booking.TourSchedule.StartAtUtc })
                        .SingleOrDefaultAsync(transactionToken);

                    if (ticketInfo is null)
                    {
                        return Result.Failure<TicketQrCode>(TicketErrorCodes.NotFound, "The e-ticket was not found.");
                    }

                    var window = TicketValidityWindow.For(ticketInfo.StartAtUtc);
                    var currentStatus = TicketDisplayStatusEvaluator.Evaluate(
                        ticketInfo.BookingStatus, ticketInfo.Status, window, nowUtc);

                    if (!TicketDisplayStatusEvaluator.CanRefresh(currentStatus))
                    {
                        var lingering = await dbContext.TicketQrCodes
                            .Where(c => c.TicketId == ticketId && c.Status == TicketQrCodeStatus.Active)
                            .ToListAsync(transactionToken);

                        if (lingering.Count > 0)
                        {
                            foreach (var activeCode in lingering)
                                activeCode.Revoke();
                            await dbContext.SaveChangesAsync(transactionToken);
                        }

                        return Result.Failure<TicketQrCode>(
                            TicketErrorCodes.NotRefreshable,
                            "This e-ticket can no longer be refreshed.",
                            new Dictionary<string, object?> { [TicketErrorCodes.TicketStatusMetadataKey] = currentStatus.ToString() });
                    }

                    var active = await dbContext.TicketQrCodes
                        .Where(code => code.TicketId == ticketId && code.Status == TicketQrCodeStatus.Active)
                        .ToListAsync(transactionToken);

                    foreach (var code in active)
                        code.Revoke();

                    await dbContext.SaveChangesAsync(transactionToken);

                    var replacement = NewCode(ticketId, nowUtc);
                    dbContext.TicketQrCodes.Add(replacement);
                    await dbContext.SaveChangesAsync(transactionToken);
                    return Result.Success(replacement);
                }, cancellationToken);
            }
            catch (DbUpdateException exception)
                when (attempt < MaxIssueAttempts && classifier.IsOneActiveCodeConflict(exception))
            {
                dbContext.ClearTrackedEntities();
                var fallback = await EnsureActiveAsync(ticketId, nowUtc, cancellationToken);
                if (fallback is null)
                {
                    return Result.Failure<TicketQrCode>(
                        TicketErrorCodes.NotRefreshable,
                        "This e-ticket can no longer be refreshed.");
                }
                return Result.Success(fallback);
            }
            catch (Exception exception)
                when (attempt < MaxIssueAttempts && dbContext.IsDeadlock(exception))
            {
                dbContext.ClearTrackedEntities();
                var delayMs = Random.Shared.Next(15, 40) * (1 << (attempt - 1));
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
            }
        }
    }

    private TicketQrCode NewCode(long ticketId, DateTimeOffset nowUtc)
    {
        var payload = codec.Issue(ticketId, nowUtc);
        return TicketQrCode.Issue(ticketId, payload.QrToken, payload.Signature, nowUtc);
    }
}