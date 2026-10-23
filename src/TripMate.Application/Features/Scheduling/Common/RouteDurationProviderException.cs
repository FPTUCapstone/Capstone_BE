namespace TripMate.Application.Features.Scheduling.Common;

public enum RouteDurationProviderFailureKind
{
    Timeout,
    Unavailable,
}

public sealed class RouteDurationProviderException(
    string providerName,
    RouteDurationProviderFailureKind failureKind,
    string message,
    Exception innerException) : Exception(message, innerException)
{
    public string ProviderName { get; } = providerName;

    public RouteDurationProviderFailureKind FailureKind { get; } = failureKind;
}