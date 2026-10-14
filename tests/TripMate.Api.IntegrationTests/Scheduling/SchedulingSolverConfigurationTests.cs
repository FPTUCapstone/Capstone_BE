using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Scheduling.Common;

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
}