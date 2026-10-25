using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Tickets.Common;

namespace TripMate.Application.Features.Tickets.Refresh;

/// <summary>UC-29 alternative flow [Refresh QR Code]. No idempotency key: repeating it only rotates again.</summary>
public sealed record RefreshTicketQrCommand(long TravelerUserId, long TicketId) : IRequest<Result<TicketResponse>>;
