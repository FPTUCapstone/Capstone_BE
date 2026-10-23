using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public sealed class Ticket : BaseEntity
{
    private Ticket()
    {
    }

    public long BookingId { get; private set; }
    public TicketStatus Status { get; private set; }

    public void Cancel()
    {
        if (Status == TicketStatus.Used)
        {
            throw new InvalidOperationException("A used ticket cannot be cancelled.");
        }

        if (Status == TicketStatus.Valid)
        {
            Status = TicketStatus.Cancelled;
        }
    }
}