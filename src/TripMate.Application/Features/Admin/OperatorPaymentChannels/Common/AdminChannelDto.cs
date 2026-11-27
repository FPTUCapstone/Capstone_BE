namespace TripMate.Application.Features.Admin.OperatorPaymentChannels.Common;

public sealed record AdminChannelDto(
    long ChannelId,
    Guid OpaqueId,
    long TourOperatorUserId,
    string? OperatorName,
    string Status,
    int ChannelVersion,
    string? BeneficiaryAccountNumberMasked,
    string? BeneficiaryAccountName,
    string? BeneficiaryBankCode,
    DateTimeOffset? WebhookRegisteredAt,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastVerifiedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AdminChannelListDto(
    IReadOnlyList<AdminChannelDto> Items,
    int TotalCount,
    int Page,
    int PageSize);