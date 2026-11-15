namespace TripMate.Application.Features.AccountProvisioning;

/// <summary>Serializes provisioning writers across API instances for the current SQL transaction.</summary>
public interface IAccountProvisioningLock
{
    Task AcquireAsync(string normalizedEmail, string normalizedPhone, IReadOnlyCollection<long> poiIds,
        CancellationToken cancellationToken);
}