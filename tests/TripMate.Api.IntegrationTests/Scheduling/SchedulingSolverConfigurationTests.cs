using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;

namespace TripMate.Api.IntegrationTests.Scheduling;

[Collection(nameof(TripMateApiFactory))]
public sealed class SchedulingSolverConfigurationTests
{
    /// <summary>
    /// UC-10 Phase 3: cấu hình phát hành chọn CSP tường minh. Mặc định trong code vẫn là Heuristic, nên thiếu cấu hình thì
    /// hệ thống dùng thuật toán cũ, và quay lại cũng chỉ cần đổi cấu hình này.
    /// </summary>
    [Fact]
    public async Task ShippedConfiguration_SelectsTheCspSolverExplicitly()
    {
        await using var factory = new TripMateApiFactory();

        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var options = factory.Services.GetRequiredService<SchedulingGenerationOptions>();

        configuration[$"{SchedulingGenerationOptions.SectionName}:{nameof(SchedulingGenerationOptions.SolverMode)}"]
            .Should().Be(nameof(SchedulingSolverMode.Csp));
        options.SolverMode.Should().Be(SchedulingSolverMode.Csp);
        new SchedulingGenerationOptions().SolverMode.Should().Be(SchedulingSolverMode.Heuristic, "the code default stays the fallback");
    }

    /// <summary>
    /// ADR-002 §8: the gates passed on local verification (2026-11-11), so WeightedUtility is the shipped objective.
    /// Rollback is the setting <c>SchedulingGeneration__Csp__Mode=Lexicographic</c>, which must also change this test.
    /// </summary>
    [Fact]
    public async Task ShippedConfiguration_UsesTheWeightedUtilityObjective()
    {
        await using var factory = new TripMateApiFactory();

        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var options = factory.Services.GetRequiredService<SchedulingGenerationOptions>();

        configuration[$"{SchedulingGenerationOptions.SectionName}:Csp:{nameof(CspOptions.Mode)}"]
            .Should().Be(nameof(ObjectiveMode.WeightedUtility));
        options.Csp.Mode.Should().Be(ObjectiveMode.WeightedUtility);
    }
}