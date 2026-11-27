namespace TripMate.Application.Features.Traveler.Payments.Common;

/// <summary>
/// Creates a per-call IPaymentGateway backed by a TO's decrypted payOS credentials.
/// Never log, serialize or return OperatorChannelSnapshot in a response.
/// </summary>
public interface IOperatorPaymentGatewayFactory
{
    IPaymentGateway Create(OperatorChannelSnapshot snapshot);
}

/// <summary>Decrypted, in-process-only credentials for one TO's payOS channel.</summary>
public sealed record OperatorChannelSnapshot(
    string ClientId,
    string ApiKey,
    string ChecksumKey,
    string ReturnUrl,
    string CancelUrl);
