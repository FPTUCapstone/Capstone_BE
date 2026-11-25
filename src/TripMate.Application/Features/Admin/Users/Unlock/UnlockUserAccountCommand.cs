using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.Users.Unlock;

public sealed record UnlockUserAccountCommand(
    long UserId,
    string Reason,
    string IdempotencyKey) : IRequest<Result<UnlockUserAccountResponse>>;