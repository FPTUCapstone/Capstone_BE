namespace TripMate.Application.Features.Operator.PaymentChannels.Common;

public sealed record ChannelStatusDto(
    long? ChannelId,
    Guid? OpaqueId,
    string Status,
    int? ChannelVersion,
    string? BeneficiaryAccountNumberMasked,
    string? BeneficiaryAccountName,
    string? BeneficiaryBankCode,
    DateTimeOffset? WebhookRegisteredAt,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastVerifiedAt,
    IReadOnlyList<string> AllowedActions);