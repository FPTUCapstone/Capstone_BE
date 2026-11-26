using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Rerouting.Common;

namespace TripMate.Api.IntegrationTests.Rerouting;

[Collection(nameof(TripMateApiFactory))]
public sealed class ReroutingOptionsWiringTests
{
    [Fact]
    public void Host_RegistersTheReroutingOptionsValidator()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        factory.Services.GetServices<IValidateOptions<ReroutingOptions>>()
            .Should().ContainSingle(validator => validator is ReroutingOptionsValidator);
    }

    [Fact]
    public void Host_BindsTheReroutingSectionOfAppSettings()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var options = factory.Services.GetRequiredService<IOptions<ReroutingOptions>>().Value;

        options.ProposalTtl.Should().Be(TimeSpan.FromMinutes(15));
        options.MaxVariants.Should().Be(8);
        options.MaxReplacementsPerStop.Should().Be(3);
        options.ProposalRetryWindow.Should().Be(TimeSpan.FromMinutes(30));
    }
}