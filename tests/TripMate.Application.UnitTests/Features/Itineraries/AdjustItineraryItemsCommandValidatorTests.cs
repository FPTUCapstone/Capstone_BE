using FluentAssertions;

using TripMate.Application.Features.Itineraries.AdjustItems;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Itineraries;

public sealed class AdjustItineraryItemsCommandValidatorTests
{
    private readonly AdjustItineraryItemsCommandValidator _validator = new();

    [Fact]
    public void Validate_LegacyIdsOnly_IsValid()
    {
        _validator.Validate(Command([1, 2])).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_StopsWithLegModes_IsValid()
    {
        var command = Command([1, 2], [new ItineraryStop(1, TransportMode.Walking), new ItineraryStop(2)]);

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_PublicTransitLeg_IsInvalid()
    {
        var command = Command([1, 2], [new ItineraryStop(1, TransportMode.PublicTransit), new ItineraryStop(2)]);

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_StopsInADifferentOrderThanIds_IsInvalid()
    {
        var command = Command([1, 2], [new ItineraryStop(2), new ItineraryStop(1)]);

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_MoreThanTheMaximumStops_IsInvalid()
    {
        var ids = Enumerable.Range(1, AdjustItineraryItemsCommand.MaxStops + 1).Select(id => (long)id).ToArray();

        _validator.Validate(Command(ids)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void EffectiveStops_ForLegacyIds_KeepTheTripTransport()
    {
        Command([5, 6]).EffectiveStops.Should().Equal(new ItineraryStop(5), new ItineraryStop(6));
    }

    private static AdjustItineraryItemsCommand Command(long[] ids, ItineraryStop[]? stops = null) =>
        new(10, 42, Guid.NewGuid(), ids, stops);
}