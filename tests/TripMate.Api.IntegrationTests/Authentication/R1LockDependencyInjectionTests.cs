using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Application.Common.Interfaces;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Authentication;

public sealed class R1LockDependencyInjectionTests
{
    [Fact]
    public void AddInfrastructure_RegistersExactlyOneScopedAccountStatusLock()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] =
                    "Server=localhost;Database=TripMate_DI_Only;Integrated Security=True;TrustServerCertificate=True",
            })
            .Build();

        TripMate.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);

        var descriptor = services
            .Where(candidate => candidate.ServiceType == typeof(IAccountStatusMutationLock))
            .Should().ContainSingle().Subject;
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);

        using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IAccountStatusMutationLock>();
        var sameScope = firstScope.ServiceProvider.GetRequiredService<IAccountStatusMutationLock>();
        var second = secondScope.ServiceProvider.GetRequiredService<IAccountStatusMutationLock>();

        first.Should().BeOfType<SqlServerAccountStatusMutationLock>();
        sameScope.Should().BeSameAs(first);
        second.Should().NotBeSameAs(first);
    }
}