namespace TripMate.Application.Features.Scheduling.Common;

/// <summary>A bounded scheduling DB phase could not acquire its lock or recover from deadlock.</summary>
public sealed class SchedulingRequestContentionException(Exception? innerException = null)
    : Exception("Scheduling is busy. Retry with the same Idempotency-Key.", innerException);