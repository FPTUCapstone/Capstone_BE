using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripReviews.Media;
using TripMate.Infrastructure.Media.Cloudinary;

namespace TripMate.Infrastructure.UnitTests.Media;

public sealed class ReviewMediaRegistrationTests
{
    [Fact]
    public void InfrastructureRegistersScopedMediaWorkflowAndRecoveryWorker()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().Build());
        foreach (var contract in new[] { typeof(IReviewMediaRecoveryJournal), typeof(ReviewMediaCoordinator), typeof(ReviewMediaRecoveryRunner) })
            services.Should().Contain(x => x.ServiceType == contract && x.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(x => x.ServiceType == typeof(IReviewMediaStorage));
        services.Should().Contain(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType!.Name == "ReviewMediaRecoveryService");
    }

    [Fact]
    public void InfrastructureRejectsChangedReviewNamespace()
    {
        var settings = ValidCloudinarySettings();
        settings["Cloudinary:ReviewMediaFolderRoot"] = "tripmate/reviews-moved";
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(settings))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddInfrastructure(context.Configuration);
            })
            .Build();

        Action start = host.Start;
        start.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void InfrastructureAcceptsStableReviewNamespaceAtHostStart()
    {
        var settings = ValidCloudinarySettings();
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(settings))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddInfrastructure(context.Configuration);
            })
            .Build();

        Action start = host.Start;
        start.Should().NotThrow();
    }

    private static Dictionary<string, string?> ValidCloudinarySettings() => new()
    {
        ["ConnectionStrings:Default"] = "Server=unused;Database=unused;",
        ["SchedulingRateLimit:Provider"] = "SingleInstance",
        ["Cloudinary:CloudName"] = "test-cloud",
        ["Cloudinary:ApiKey"] = "test-api-key",
        ["Cloudinary:ApiSecret"] = "test-api-secret",
        ["Cloudinary:TourMediaFolderRoot"] = "tripmate/tests/tours",
        ["Cloudinary:ReviewMediaFolderRoot"] = ReviewCloudinaryOptions.StableReviewMediaFolderRoot,
        ["Cloudinary:OperatorDocumentsFolderRoot"] = "tripmate/tests/operator-documents",
    };
}