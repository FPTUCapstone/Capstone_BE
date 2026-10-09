using System.Net;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Infrastructure.Reviews.Media;

namespace TripMate.Api.IntegrationTests.Reviews;

[Collection(nameof(TripMateApiFactory))]
public sealed class TripReviewContextDependencyInjectionTests
{
    [Fact]
    public void SqlServerFactory_DoesNotStartMediaWorkersByDefault()
    {
        bool? recoveryWorkerRegistered = null;
        bool? tourCleanupWorkerRegistered = null;
        using var factory = new TripMateApiFactory(
            sqlServerConnectionString: "Server=127.0.0.1,1;Database=unused;User Id=unused;Password=unused;TrustServerCertificate=True;Encrypt=False;Connect Timeout=1",
            configureTestServices: services =>
            {
                recoveryWorkerRegistered = services.Any(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(ReviewMediaRecoveryService));
                tourCleanupWorkerRegistered = services.Any(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType?.FullName
                        == "TripMate.Infrastructure.Services.TourMediaCleanupBackgroundService");
            });

        _ = factory.Services;

        recoveryWorkerRegistered.Should().BeFalse();
        tourCleanupWorkerRegistered.Should().BeFalse();
    }

    [Fact]
    public async Task InMemoryDevelopmentFactory_BuildsWithoutSqlContextButCannotFakeBookingEvidence()
    {
        await using var factory = new TripMateApiFactory(environmentName: Environments.Development);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ITripReviewContextReader>();
        Func<Task> read = () => reader.ReadOwnedAsync(1, 1, default);
        await read.Should().ThrowAsync<NotSupportedException>();
    }
}