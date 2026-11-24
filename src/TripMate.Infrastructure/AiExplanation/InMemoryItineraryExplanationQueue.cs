using System.Threading.Channels;

using TripMate.Application.Features.Scheduling.Explanation;

namespace TripMate.Infrastructure.AiExplanation;

/// <summary>
/// The waiting list of background explanations, kept in memory (specs/UC-10-background-explanation-spec.md, B6).
/// It holds at most <see cref="Capacity"/> itineraries and refuses the next one instead of making the create
/// request wait. Whatever is still waiting when the process stops is lost; those itineraries keep their template
/// sentences.
/// </summary>
public sealed class InMemoryItineraryExplanationQueue : IItineraryExplanationQueue
{
    public const int Capacity = 50;

    private readonly Channel<ItineraryExplanationJob> _channel =
        Channel.CreateBounded<ItineraryExplanationJob>(new BoundedChannelOptions(Capacity)
        {
            // With Wait, TryWrite on a full channel returns false at once: the caller is never blocked.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    public bool TryEnqueue(ItineraryExplanationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return _channel.Writer.TryWrite(job);
    }

    /// <summary>The jobs in the order they were queued; waits for the next one until cancelled.</summary>
    public IAsyncEnumerable<ItineraryExplanationJob> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}