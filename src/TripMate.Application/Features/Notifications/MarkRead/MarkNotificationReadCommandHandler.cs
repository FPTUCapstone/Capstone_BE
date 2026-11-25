using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Notifications.MarkRead;

public sealed class MarkNotificationReadCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<MarkNotificationReadCommand, Result>
{
    public async Task<Result> Handle(
        MarkNotificationReadCommand request,
        CancellationToken cancellationToken)
    {
        var notification = await dbContext.Notifications.SingleOrDefaultAsync(
            item => item.Id == request.NotificationId
                && item.UserId == request.RecipientUserId
                && item.Channel == NotificationChannel.InApp,
            cancellationToken);
        if (notification is null)
        {
            return Result.Failure(NotificationErrorCodes.NotFound, "Notification not found.");
        }

        if (notification.MarkRead(dateTimeProvider.UtcNow))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}