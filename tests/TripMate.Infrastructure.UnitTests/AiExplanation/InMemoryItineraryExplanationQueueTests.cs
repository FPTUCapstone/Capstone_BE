using FluentAssertions;

using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Infrastructure.AiExplanation;

namespace TripMate.Infrastructure.UnitTests.AiExplanation;

/// <summary>
/// The waiting list of background explanations (specs/UC-10-background-explanation-spec.md, B6): in memory, at most
/// 50 itineraries, and a full list refuses instead of making the create request wait.
/// </summary>
public sealed class InMemoryItineraryExplanationQueueTests
{
    [Fact]
    public void TryEnqueue_AcceptsFiftyJobsAndRefusesTheNext()
    {
        var queue = new InMemoryItineraryExplanationQueue();

        for (int itineraryId = 1; itineraryId <= InMemoryItineraryExplanationQueue.Capacity; itineraryId++)
        {
            queue.TryEnqueue(Job(itineraryId)).Should().BeTrue();
        }

        InMemoryItineraryExplanationQueue.Capacity.Should().Be(50);
        queue.TryEnqueue(Job(51)).Should().BeFalse();
    }

    [Fact]
    public async Task ReadAllAsync_HandsJobsOutInTheOrderTheyWereQueued()
    {
        var queue = new InMemoryItineraryExplanationQueue();
        queue.TryEnqueue(Job(7));
        queue.TryEnqueue(Job(3));
        queue.TryEnqueue(Job(9));

        var read = new List<long>();
        await foreach (ItineraryExplanationJob job in queue.ReadAllAsync(CancellationToken.None))
        {
            read.Add(job.ItineraryId);
            if (read.Count == 3)
            {
                break;
            }
        }

        read.Should().Equal(7, 3, 9);
    }

    [Fact]
    public async Task TryEnqueue_AfterAJobWasRead_HasRoomAgain()
    {
        var queue = new InMemoryItineraryExplanationQueue();
        for (int itineraryId = 1; itineraryId <= InMemoryItineraryExplanationQueue.Capacity; itineraryId++)
        {
            queue.TryEnqueue(Job(itineraryId));
        }

        await foreach (ItineraryExplanationJob _ in queue.ReadAllAsync(CancellationToken.None))
        {
            break;
        }

        queue.TryEnqueue(Job(51)).Should().BeTrue();
        queue.TryEnqueue(Job(52)).Should().BeFalse();
    }

    [Fact]
    public async Task ReadAllAsync_StopsWaitingWhenCancelled()
    {
        var queue = new InMemoryItineraryExplanationQueue();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Func<Task> read = async () =>
        {
            await foreach (ItineraryExplanationJob _ in queue.ReadAllAsync(cancellation.Token))
            {
            }
        };

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void TryEnqueue_NullJob_Throws()
    {
        var queue = new InMemoryItineraryExplanationQueue();

        Action act = () => queue.TryEnqueue(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static ItineraryExplanationJob Job(long itineraryId) => new(
        itineraryId,
        new ItineraryExplanationInput(
            [],
            new ItineraryExplanationContext(
                [],
                new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero),
                "Asia/Ho_Chi_Minh",
                0)),
        []);
}