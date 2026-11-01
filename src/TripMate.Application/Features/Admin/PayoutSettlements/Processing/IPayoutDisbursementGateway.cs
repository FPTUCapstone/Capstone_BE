namespace TripMate.Application.Features.Admin.PayoutSettlements.Processing;

/// <summary>
/// Port to the bank/payment gateway that pays an Operator (UC-65). It is separate from
/// <c>IPaymentGateway</c>, which collects money and refunds it. Adapters translate the provider's
/// wire format into the normalized results below and never leak credentials, account numbers, or raw
/// provider responses into them.
/// </summary>
public interface IPayoutDisbursementGateway
{
    Task<PayoutSubmitResult> SubmitAsync(PayoutSubmitRequest request, CancellationToken cancellationToken);

    Task<PayoutStatusResult> GetStatusAsync(string gatewayPayoutId, CancellationToken cancellationToken);

    Task<PayoutLookupResult> FindByReferenceAsync(string referenceId, CancellationToken cancellationToken);
}

public sealed record PayoutSubmitRequest(
    string ReferenceId,
    decimal Amount,
    string Description,
    string ToBin,
    string ToAccountNumber,
    string IdempotencyKey);

public enum PayoutSubmitOutcome
{
    /// <summary>The gateway accepted the transfer and returned its payout identifier.</summary>
    Accepted,

    /// <summary>The gateway refused it for a reason that will not change on retry (validation,
    /// invalid account, insufficient payout balance, authentication). Nothing was created.</summary>
    DefinitiveRejection,

    /// <summary>Timeout, network failure, 5xx, 408, or 429: the transfer may or may not exist.</summary>
    Transient,

    /// <summary>The gateway says this idempotency key was already used: a previous attempt may have
    /// created the payout, so look it up by reference before doing anything else.</summary>
    IdempotencyKeyExists,
}

public sealed record PayoutSubmitResult(
    PayoutSubmitOutcome Outcome,
    string? GatewayPayoutId = null,
    string? FailureCode = null)
{
    public static PayoutSubmitResult Accepted(string gatewayPayoutId) =>
        new(PayoutSubmitOutcome.Accepted, gatewayPayoutId);

    public static PayoutSubmitResult Rejected(string failureCode) =>
        new(PayoutSubmitOutcome.DefinitiveRejection, FailureCode: failureCode);

    public static PayoutSubmitResult Transient(string failureCode) =>
        new(PayoutSubmitOutcome.Transient, FailureCode: failureCode);

    public static PayoutSubmitResult KeyExists() =>
        new(PayoutSubmitOutcome.IdempotencyKeyExists, FailureCode: "gateway_idempotency_key_exists");
}

public enum PayoutGatewayState
{
    Processing,
    Succeeded,
    Failed,
    Cancelled,

    /// <summary>The bank or gateway refused the transfer after accepting it. Terminal failure.</summary>
    Rejected,

    /// <summary>The transfer was returned to the payer's balance. Terminal failure.</summary>
    Reversed,

    /// <summary>A value outside the confirmed contract. Callers treat it exactly like Processing.</summary>
    Unknown,
}

/// <summary>Result of reading one payout's state. <see cref="IsAvailable"/> is false when the call
/// itself failed (network, 5xx, 429); that says nothing about the transfer.</summary>
public sealed record PayoutStatusResult(bool IsAvailable, PayoutGatewayState State = PayoutGatewayState.Unknown)
{
    public static PayoutStatusResult Unavailable() => new(false);

    public static PayoutStatusResult Of(PayoutGatewayState state) => new(true, state);
}

public enum PayoutLookupOutcome
{
    /// <summary>The gateway has a payout with this reference.</summary>
    Found,

    /// <summary>The gateway answered successfully and has no payout with this reference.</summary>
    NotFound,

    /// <summary>The lookup call itself failed; the transfer's existence is unknown.</summary>
    Failed,
}

public sealed record PayoutLookupResult(
    PayoutLookupOutcome Outcome,
    string? GatewayPayoutId = null,
    PayoutGatewayState State = PayoutGatewayState.Unknown)
{
    public static PayoutLookupResult Found(string gatewayPayoutId, PayoutGatewayState state) =>
        new(PayoutLookupOutcome.Found, gatewayPayoutId, state);

    public static PayoutLookupResult NotFound() => new(PayoutLookupOutcome.NotFound);

    public static PayoutLookupResult Failed() => new(PayoutLookupOutcome.Failed);
}
