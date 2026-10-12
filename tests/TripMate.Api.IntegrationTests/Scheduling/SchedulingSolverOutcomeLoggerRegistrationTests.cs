using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using TripMate.Api.Diagnostics;
using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Scheduling;

[Collection(nameof(TripMateApiFactory))]
public sealed class SchedulingSolverOutcomeLoggerRegistrationTests
{
    /// <summary>
    /// UC-10 Phase 3: CSP monitoring relies on the solver outcome log line, so the host must register its writer.
    /// </summary>
    [Fact]
    public async Task Host_RegistersTheSolverOutcomeLogger()
    {
        await using var factory = new TripMateApiFactory();

        factory.Services.GetServices<IHostedService>().OfType<SchedulingSolverOutcomeLogger>().Should().ContainSingle();
    }
}