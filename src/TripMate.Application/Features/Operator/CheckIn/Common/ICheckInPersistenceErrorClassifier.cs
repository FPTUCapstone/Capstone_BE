namespace TripMate.Application.Features.Operator.CheckIn.Common;

/// <summary>
/// Classifies provider-specific persistence failures that make check-in verification temporarily unavailable.
/// </summary>
public interface ICheckInPersistenceErrorClassifier
{
    bool IsTemporarilyUnavailable(Exception exception);
}