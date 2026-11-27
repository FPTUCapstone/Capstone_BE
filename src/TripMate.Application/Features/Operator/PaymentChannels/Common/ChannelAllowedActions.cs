using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.PaymentChannels.Common;

internal static class ChannelAllowedActions
{
    public const string SubmitCredentials = "submit_credentials";
    public const string Verify = "verify";

    internal static IReadOnlyList<string> For(string status) => status switch
    {
        OperatorPaymentChannel.StatusNotConnected => [SubmitCredentials],
        OperatorPaymentChannel.StatusPendingVerification => [SubmitCredentials, Verify],
        OperatorPaymentChannel.StatusConnectionError => [SubmitCredentials, Verify],
        OperatorPaymentChannel.StatusReady => [SubmitCredentials],
        OperatorPaymentChannel.StatusDisabled => [],
        _ => [],
    };

    internal static ChannelStatusDto ToDto(OperatorPaymentChannel channel) => new(
        channel.Id,
        channel.OpaqueId,
        channel.Status,
        channel.ChannelVersion,
        MaskAccountNumber(channel.BeneficiaryAccountNumber),
        channel.BeneficiaryAccountName,
        channel.BeneficiaryBankCode,
        channel.WebhookRegisteredAtUtc,
        channel.ConnectedAtUtc,
        channel.LastVerifiedAtUtc,
        For(channel.Status));

    internal static ChannelStatusDto NotConnected() => new(
        null, null,
        OperatorPaymentChannel.StatusNotConnected,
        null, null, null, null, null, null, null,
        [SubmitCredentials]);

    private static string? MaskAccountNumber(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length <= 4)
            return raw;
        return "••••" + raw[^4..];
    }
}
