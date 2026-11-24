using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Scheduling.Explanation;

namespace TripMate.Infrastructure.AiExplanation;

/// <summary>
/// Writes the AI explanations of queued itineraries after their create request has answered
/// (specs/UC-10-background-explanation-spec.md, B2, B5 and B6). One itinerary at a time, because the explanation
/// model answers one request at a time; each in its own scope, so a failed save never reaches the next
/// itinerary; and an itinerary that fails is logged and skipped, never retried.
/// </summary>
internal sealed class ItineraryExplanationBackgroundService(
    InMemoryItineraryExplanationQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<ItineraryExplanationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (ItineraryExplanationJob job in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                    var processor = scope.ServiceProvider
                        .GetRequiredService<IBackgroundItineraryExplanationProcessor>();
                    await processor.ProcessAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // The host is stopping: the itinerary in flight keeps its template sentences.
                    break;
                }
                catch (Exception exception)
                {
                    // The processor reports the failures it expects itself; this is anything else. The itinerary
                    // keeps its template sentences and the worker goes on with the next one.
                    logger.LogError(
                        exception,
                        "Itinerary explanation (background) itineraryId={ItineraryId}; outcome=worker-error.",
                        job.ItineraryId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopped while waiting for the next itinerary.
        }
    }
}