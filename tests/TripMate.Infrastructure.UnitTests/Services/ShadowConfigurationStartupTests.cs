using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Shadow;

namespace TripMate.Infrastructure.UnitTests.Services;

public class ShadowConfigurationStartupTests
{
    [Fact]
    public void ShippedAppSettings_ContainsValidShadowSection_AndPassesValidation()
    {
        var appsettingsPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../src/TripMate.Api/appsettings.json"));

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(appsettingsPath, optional: false)
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<ShadowOptions>()
            .Bind(configuration.GetSection(ShadowOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ShadowOptions>, ShadowOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ShadowOptions>>().Value;

        options.Should().NotBeNull();
        options.Enabled.Should().BeFalse();
        options.SampleRate.Should().Be(1.0);
        options.TimeoutMs.Should().Be(5000);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Invalid_SampleRate_FailsStartupValidation(double sampleRate)
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["SchedulingGeneration:Shadow:SampleRate"] = sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        var services = new ServiceCollection();
        services.AddOptions<ShadowOptions>()
            .Bind(config.GetSection(ShadowOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ShadowOptions>, ShadowOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOptions<ShadowOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*SampleRate*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Invalid_TimeoutMs_FailsStartupValidation(int timeoutMs)
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["SchedulingGeneration:Shadow:TimeoutMs"] = timeoutMs.ToString()
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        var services = new ServiceCollection();
        services.AddOptions<ShadowOptions>()
            .Bind(config.GetSection(ShadowOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ShadowOptions>, ShadowOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOptions<ShadowOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*TimeoutMs*");
    }

    [Fact]
    public void Missing_ShadowSection_UsesDefaults_AndPassesValidation()
    {
        var emptyConfig = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddOptions<ShadowOptions>()
            .Bind(emptyConfig.GetSection(ShadowOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ShadowOptions>, ShadowOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ShadowOptions>>().Value;

        options.Should().NotBeNull();
        options.Enabled.Should().BeFalse();
        options.SampleRate.Should().Be(1.0);
        options.TimeoutMs.Should().Be(5000);
    }
}