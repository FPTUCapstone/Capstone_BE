using System.Threading.Channels;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Infrastructure.Email;

internal sealed class PasswordResetEmailQueue : IPasswordResetEmailQueue
{
    internal const int Capacity = 32;

    private readonly Channel<PasswordResetEmailDelivery> _channel =
        Channel.CreateBounded<PasswordResetEmailDelivery>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });

    public bool TryEnqueue(PasswordResetEmailDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return _channel.Writer.TryWrite(delivery);
    }

    internal IAsyncEnumerable<PasswordResetEmailDelivery> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}