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
        ["SchedulingGeneration:Personalization:Version"] = "2026.1",
        ["SchedulingGeneration:Personalization:Pace:Relaxed:LambdaPerMin"] = "0.025",
        ["SchedulingGeneration:Personalization:Pace:Relaxed:MaxStopsPerDay"] = "3",
        ["SchedulingGeneration:Personalization:Pace:Relaxed:MaxDailyEffort"] = "200",
        ["SchedulingGeneration:Personalization:Pace:Relaxed:RestAfterMin"] = "90",
        ["SchedulingGeneration:Personalization:Pace:Moderate:LambdaPerMin"] = "0.015",
        ["SchedulingGeneration:Personalization:Pace:Moderate:MaxStopsPerDay"] = "5",
        ["SchedulingGeneration:Personalization:Pace:Moderate:MaxDailyEffort"] = "300",
        ["SchedulingGeneration:Personalization:Pace:Moderate:RestAfterMin"] = "120",
        ["SchedulingGeneration:Personalization:Pace:Fast:LambdaPerMin"] = "0.008",
        ["SchedulingGeneration:Personalization:Pace:Fast:MaxStopsPerDay"] = "8",
        ["SchedulingGeneration:Personalization:Pace:Fast:MaxDailyEffort"] = "450",
        ["SchedulingGeneration:Personalization:Pace:Fast:RestAfterMin"] = "180",
        ["SchedulingGeneration:TimeFit:Version"] = "2026.1",
        ["SchedulingGeneration:Shadow:Enabled"] = "false",
        ["SchedulingGeneration:Shadow:SampleRate"] = "1.0",
        ["SchedulingGeneration:Shadow:TimeoutMs"] = "5000",
    };
}