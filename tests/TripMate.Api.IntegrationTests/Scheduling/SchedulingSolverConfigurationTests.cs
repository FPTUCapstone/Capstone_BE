using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Api.IntegrationTests.Scheduling;

[Collection(nameof(TripMateApiFactory))]
public sealed class SchedulingSolverConfigurationTests
{
    [Fact]
    public async Task ShippedConfiguration_SelectsTheHeuristicSolverExplicitly()
    {
        await using var factory = new TripMateApiFactory();

        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var options = factory.Services.GetRequiredService<SchedulingGenerationOptions>();

        configuration[$"{SchedulingGenerationOptions.SectionName}:{nameof(SchedulingGenerationOptions.SolverMode)}"]
            .Should().Be(nameof(SchedulingSolverMode.Heuristic));
        options.SolverMode.Should().Be(SchedulingSolverMode.Heuristic);
    }
}