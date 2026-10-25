using Microsoft.EntityFrameworkCore;

namespace TripMate.Application.Features.Tickets.Common;

public interface ITicketQrPersistenceErrorClassifier
{
    /// <summary>True when a save lost the race on UX_TicketQrCodes_OneActivePerTicket.</summary>
    bool IsOneActiveCodeConflict(DbUpdateException exception);
}
