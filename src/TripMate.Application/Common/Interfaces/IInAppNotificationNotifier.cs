namespace TripMate.Application.Common.Interfaces;

public interface IInAppNotificationNotifier
{
    Task NotifyInboxChangedAsync(long userId, CancellationToken cancellationToken);
}

