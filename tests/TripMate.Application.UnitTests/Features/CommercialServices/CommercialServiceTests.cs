using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.CommercialServices;

public sealed class CommercialServiceTests
{
    [Fact]
    public void Create_WithNegativePrice_Throws()
    {
        var provider = ServiceProvider.Create(
            "Da Nang Ride",
            CommercialService.CategoryVehicle,
            "Active");

        var create = () => CommercialService.Create(
            provider,
            CommercialService.CategoryVehicle,
            "Honda Wave 110cc",
            -1m,
            CommercialService.PriceUnitPerDay,
            "Available",
            "VND",
            true,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithSupportedDisclosureData_PreservesPublicValues()
    {
        var provider = ServiceProvider.Create(
            "Da Nang Ride",
            CommercialService.CategoryVehicle,
            "Active");

        var service = CommercialService.Create(
            provider,
            CommercialService.CategoryVehicle,
            "Honda Wave 110cc",
            180000m,
            CommercialService.PriceUnitPerDay,
            "Available",
            "VND",
            true,
            500000m,
            "https://images.example.test/wave.jpg",
            """{"transmission":"Automatic","seats":2}""",
            DateTimeOffset.UtcNow);

        service.CurrencyCode.Should().Be("VND");
        service.PriceIncludesTax.Should().BeTrue();
        service.RefundableDepositAmount.Should().Be(500000m);
        service.Provider.Should().BeSameAs(provider);
    }

    [Fact]
    public void Create_WithNonHttpsCoverImage_Throws()
    {
        var provider = ServiceProvider.Create(
            "Da Nang Ride",
            CommercialService.CategoryVehicle,
            ServiceProvider.StatusActive);

        var create = () => CommercialService.Create(
            provider,
            CommercialService.CategoryVehicle,
            "Honda Wave 110cc",
            180000m,
            CommercialService.PriceUnitPerDay,
            CommercialService.AvailabilityAvailable,
            "VND",
            true,
            null,
            "http://untrusted.example/image.jpg",
            null,
            DateTimeOffset.UtcNow);

        create.Should().Throw<ArgumentException>();
    }
}