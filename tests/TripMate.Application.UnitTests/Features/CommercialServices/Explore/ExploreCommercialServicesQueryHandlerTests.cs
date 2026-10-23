using FluentAssertions;

using TripMate.Application.Features.CommercialServices.Explore;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.CommercialServices.Explore;

public sealed class ExploreCommercialServicesQueryHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsOnlyAvailableServicesFromActiveProvidersInStableOrder()
    {
        await using var dbContext = TestDbContext.Create();

        var activeProvider = ServiceProvider.Create(
            "Da Nang Ride",
            CommercialService.CategoryVehicle,
            ServiceProvider.StatusActive);
        var inactiveProvider = ServiceProvider.Create(
            "Closed Hotel",
            CommercialService.CategoryHotel,
            ServiceProvider.StatusInactive);
        dbContext.ServiceProviders.AddRange(activeProvider, inactiveProvider);

        dbContext.CommercialServices.AddRange(
            CommercialService.Create(
                activeProvider,
                CommercialService.CategoryVehicle,
                "Zeta Scooter",
                180000m,
                CommercialService.PriceUnitPerDay,
                CommercialService.AvailabilityAvailable,
                "VND",
                true,
                null,
                null,
                """{"transmission":"Automatic","internalFlag":"never-public"}""",
                DateTimeOffset.UtcNow),
            CommercialService.Create(
                activeProvider,
                CommercialService.CategoryVehicle,
                "Alpha Scooter",
                150000m,
                CommercialService.PriceUnitPerDay,
                CommercialService.AvailabilityAvailable,
                "VND",
                true,
                null,
                null,
                """{"seats":2}""",
                DateTimeOffset.UtcNow),
            CommercialService.Create(
                activeProvider,
                CommercialService.CategoryVehicle,
                "Unavailable Scooter",
                120000m,
                CommercialService.PriceUnitPerDay,
                CommercialService.AvailabilityUnavailable,
                "VND",
                true,
                null,
                null,
                null,
                DateTimeOffset.UtcNow),
            CommercialService.Create(
                inactiveProvider,
                CommercialService.CategoryHotel,
                "Hidden Room",
                600000m,
                CommercialService.PriceUnitPerNight,
                CommercialService.AvailabilityAvailable,
                "VND",
                true,
                null,
                null,
                null,
                DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();

        var handler = new ExploreCommercialServicesQueryHandler(dbContext);

        var result = await handler.Handle(
            new ExploreCommercialServicesQuery(null, null, 1, 20),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items.Select(item => item.Name)
            .Should()
            .ContainInOrder("Alpha Scooter", "Zeta Scooter");
        result.Value.Items.Should().OnlyContain(item => item.ProviderName == "Da Nang Ride");
        result.Value.Items.Single(item => item.Name == "Zeta Scooter").Attributes
            .Should()
            .ContainKey("transmission")
            .And.NotContainKey("internalFlag");
    }
}