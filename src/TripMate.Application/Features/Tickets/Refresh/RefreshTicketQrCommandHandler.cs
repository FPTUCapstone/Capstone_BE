using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Tickets.Common;

namespace TripMate.Application.Features.Tickets.Refresh;

/// <summary>
/// UC-29 spec §5.5 / D5: revokes the Active QR and issues a new one, so a copy of the old QR stops
/// working at check-in. Only a NotYetActive or Valid ticket can be refreshed.
/// </summary>
public sealed class RefreshTicketQrCommandHandler(
    IApplicationDbContext dbContext,
    ITicketQrCodec codec,
    ITicketQrPersistenceErrorClassifier classifier,
    IDateTimeProvider clock)
    : IRequestHandler<RefreshTicketQrCommand, Result<TicketResponse>>
{
    public async Task<Result<TicketResponse>> Handle(RefreshTicketQrCommand request,
        CancellationToken cancellationToken)
    {
        var snapshot = await TicketSnapshot.ReadAsync(dbContext, request.TicketId, cancellationToken);
        if (snapshot is null)
            return Result.Failure<TicketResponse>(TicketErrorCodes.NotFound, "The e-ticket was not found.");

        if (snapshot.CheckAccess(request.TravelerUserId) is { } denied)
            return Result.Failure<TicketResponse>(denied.ErrorCode!, denied.ErrorMessage!);

        var now = clock.UtcNow;
        var status = TicketResponseBuilder.StatusOf(snapshot, now);
        if (!TicketDisplayStatusEvaluator.CanRefresh(status))
        {
            return Result.Failure<TicketResponse>(
                TicketErrorCodes.NotRefreshable,
                "This e-ticket can no longer be refreshed.",
                new Dictionary<string, object?> { [TicketErrorCodes.TicketStatusMetadataKey] = status.ToString() });
        }

        var issuer = new TicketQrIssuer(dbContext, codec, classifier);
        var rotation = await issuer.RotateAsync(snapshot.TicketId, now, cancellationToken);
        if (rotation.IsFailure)
        {
            return Result.Failure<TicketResponse>(
                rotation.ErrorCode!,
                rotation.ErrorMessage!,
                rotation.ErrorMetadata);
        }

        return Result.Success(
            await TicketResponseBuilder.BuildAsync(dbContext, issuer, snapshot, now, cancellationToken, rotation.Value));
    }
}