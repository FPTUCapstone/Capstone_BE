namespace TripMate.Application.Common.Interfaces;

public interface IAiProviderBudget
{
    bool TryAcquire(string provider, out IDisposable? permit);
}
