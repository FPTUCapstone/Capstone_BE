using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.UserAccounts;

public static class UserAccountErrors
{
    public const string Forbidden = "account_management.forbidden";
    public const string NotFound = "account_management.not_found";
    public const string InvalidRequest = "account_management.invalid_request";
    public const string Conflict = "account_management.conflict";
    public const string OutstandingDebt = "account_management.outstanding_debt";
    public const string ProviderSettlementUnavailable = "account_management.provider_settlement_unavailable";
}

public sealed record UserAccountDto(string UserId, string FullName, string? Email, string Role,
    string Status, DateTime CreatedAtUtc, DateTime? LastLoginAtUtc);
public sealed record UserAccountPageDto(int Page, int PageSize, int TotalCount, int TotalPages,
    IReadOnlyList<UserAccountDto> Items);
public sealed record AccountModerationDto(string Action, string? Reason, DateTime AtUtc);
public sealed record UserAccountDetailDto(UserAccountDto Account, int BookingCount,
    IReadOnlyList<AccountModerationDto> ModerationHistory);

public sealed record ListUserAccountsQuery(int Page = 1, int PageSize = 20,
    string? Role = null, string? Status = null, string? Search = null,
    DateTimeOffset? RegisteredFrom = null, DateTimeOffset? RegisteredTo = null)
    : IRequest<Result<UserAccountPageDto>>;
public sealed record GetUserAccountQuery(long UserId) : IRequest<Result<UserAccountDetailDto>>;
public sealed record ModerateUserAccountCommand(long UserId, bool Lock, string Reason)
    : IRequest<Result<UserAccountDto>>;
