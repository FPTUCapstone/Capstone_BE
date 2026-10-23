using FluentAssertions;

using TripMate.Application.Features.CommercialServices.Common;
using TripMate.Application.Features.CommercialServices.Detail;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.CommercialServices.Detail;

public sealed class GetCommercialServiceDetailQueryHandlerTests
{
    [Fact]
    public async Task Handle_WhenProviderIsInactive_ReturnsConcealedNotFound()
    {
        await using var dbContext = TestDbContext.Create();
        var provider = ServiceProvider.Create(
            "Paused provider",
            CommercialService.CategoryHotel,
            ServiceProvider.StatusInactive);
        var service = CommercialService.Create(
            provider,
            CommercialService.CategoryHotel,
            "Hidden room",
            800000m,
            CommercialService.PriceUnitPerNight,
            CommercialService.AvailabilityAvailable,
            "VND",
            true,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);

        dbContext.CommercialServices.Add(service);
        await dbContext.SaveChangesAsync();

        var result = await new GetCommercialServiceDetailQueryHandler(dbContext).Handle(
            new GetCommercialServiceDetailQuery(service.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(CommercialServiceErrorCodes.NotFound);
    }
}