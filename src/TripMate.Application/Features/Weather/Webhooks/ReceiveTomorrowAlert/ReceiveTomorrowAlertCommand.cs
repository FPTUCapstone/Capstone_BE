using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Weather.Webhooks.ReceiveTomorrowAlert;

public sealed record ReceiveTomorrowAlertCommand(string RawPayload) : IRequest<Result<ReceiveTomorrowAlertResult>>;

public sealed record ReceiveTomorrowAlertResult(
    long InboxId,
    string Status,
    bool IsDuplicate);
