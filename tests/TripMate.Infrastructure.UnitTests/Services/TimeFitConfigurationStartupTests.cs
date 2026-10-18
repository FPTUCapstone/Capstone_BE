using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TripMate.Application.Features.Scheduling.TimeOfDay;

namespace TripMate.Infrastructure.UnitTests.Services;

public class TimeFitConfigurationStartupTests
{
    [Fact]
    public void ShippedAppSettings_ContainsValidTimeFitSection_AndPassesValidation()
    {
        var appsettingsPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../src/TripMate.Api/appsettings.json"));

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(appsettingsPath, optional: false)
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<TimeFitOptions>()
            .Bind(configuration.GetSection(TimeFitOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<TimeFitOptions>, TimeFitOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TimeFitOptions>>().Value;

        options.Should().NotBeNull();
        options.Version.Should().Be("2026.1");
        options.CoolMorningFactor.Should().Be(1.10);
        options.MiddayOutdoorFactor.Should().Be(0.70);
        options.MiddayMixedFactor.Should().Be(0.85);
        options.SunsetFactor.Should().Be(1.20);
        options.NightNatureFactor.Should().Be(0.30);
    }

    [Fact]
    public void Missing_TimeFitSection_FailsStartupValidation()
    {
        var emptyConfig = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddOptions<TimeFitOptions>()
            .Bind(emptyConfig.GetSection(TimeFitOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<TimeFitOptions>, TimeFitOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOptions<TimeFitOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Version*");
    }
}
