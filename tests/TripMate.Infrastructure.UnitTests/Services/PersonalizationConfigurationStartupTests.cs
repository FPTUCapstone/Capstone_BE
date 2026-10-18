using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Infrastructure.UnitTests.Services;

public class PersonalizationConfigurationStartupTests
{
    [Fact]
    public void ShippedAppSettings_ContainsValidPersonalizationSection_AndPassesValidation()
    {
        var appsettingsPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../src/TripMate.Api/appsettings.json"));

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(appsettingsPath, optional: false)
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<PersonalizationOptions>()
            .Bind(configuration.GetSection(PersonalizationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PersonalizationOptions>, PersonalizationOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<PersonalizationOptions>>().Value;

        options.Should().NotBeNull();
        options.Pace.Should().HaveCount(3);
        options.RhoEffortPerHour.Should().Be(0.05);
    }

    [Fact]
    public void Missing_PersonalizationSection_FailsStartupValidation()
    {
        var emptyConfig = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddOptions<PersonalizationOptions>()
            .Bind(emptyConfig.GetSection(PersonalizationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PersonalizationOptions>, PersonalizationOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOptions<PersonalizationOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Pace*");
    }
}
