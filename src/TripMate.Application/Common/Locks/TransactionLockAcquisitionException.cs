namespace TripMate.Application.Common.Locks;

public sealed class TransactionLockAcquisitionException : Exception
{
    public TransactionLockAcquisitionException(
        TransactionLockFailureKind failureKind,
        string resourceCategory,
        Exception? innerException = null)
        : base($"Could not acquire the {resourceCategory} transaction lock ({failureKind}).", innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceCategory);
        FailureKind = failureKind;
        ResourceCategory = resourceCategory;
    }

    public TransactionLockFailureKind FailureKind { get; }

    public string ResourceCategory { get; }
}