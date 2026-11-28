using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Users.Unlock;

public sealed record UnlockUserAccountCommand(
    long UserId,
    string Reason,
    string? IdempotencyKey,
    UserRole? ExpectedTargetRole = null) : IRequest<Result<UnlockUserAccountResponse>>;
