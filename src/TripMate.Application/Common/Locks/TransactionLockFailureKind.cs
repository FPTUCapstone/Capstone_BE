namespace TripMate.Application.Common.Locks;

public enum TransactionLockFailureKind
{
    LockTimeout,
    LockCanceledByServer,
    DeadlockVictim,
    LockProtocolError,
    CommandTimeout,
    ProviderFailure,
    CommitOutcomeUnknown,
}