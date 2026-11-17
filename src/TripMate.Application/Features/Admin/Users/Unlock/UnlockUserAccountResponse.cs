using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Users.Unlock;

public sealed record UnlockUserAccountResponse(
    long UserId,
    AccountStatus RestoredStatus,
    DateTimeOffset UnlockedAtUtc);