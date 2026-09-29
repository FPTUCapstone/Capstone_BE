namespace TripMate.Application.Common.Models;

/// <summary>Classification of one outbound email attempt.</summary>
public enum EmailDeliveryStatus
{
    /// <summary>The transport operation completed; the matching reset generation may be marked Sent.</summary>
    Delivered,

    /// <summary>A known rejection/failure: delivery definitely did not succeed.</summary>
    DefiniteFailure,

    /// <summary>Timeout/cancellation/ambiguous transport failure: delivery cannot be proven either way.</summary>
    Unknown,
}

/// <summary>
/// Explicit delivery outcome for Application code. Carries no provider exception detail —
/// implementations must not leak raw SMTP exception payloads through this contract.
/// </summary>
public sealed record EmailDeliveryResult(EmailDeliveryStatus Status)
{
    public static EmailDeliveryResult Delivered { get; } = new(EmailDeliveryStatus.Delivered);

    public static EmailDeliveryResult DefiniteFailure { get; } = new(EmailDeliveryStatus.DefiniteFailure);

    public static EmailDeliveryResult Unknown { get; } = new(EmailDeliveryStatus.Unknown);
}