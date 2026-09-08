using FluentAssertions;
using TripMate.Application.Common.Models;

namespace TripMate.Application.UnitTests.Common.Models;

public class ResultTests
{
    [Fact]
    public void Success_HasNoErrorMetadata()
    {
        var result = Result.Success();

        result.ErrorMetadata.Should().BeEmpty();
    }

    [Fact]
    public void Failure_CopiesAndExposesErrorMetadataAsReadOnly()
    {
        var source = new Dictionary<string, object?>
        {
            ["existingPoiId"] = 42L,
        };

        var result = Result.Failure("poi.possible_duplicate", "A possible duplicate exists.", source);
        source["existingPoiId"] = 99L;

        result.ErrorMetadata.Should().ContainKey("existingPoiId")
            .WhoseValue.Should().Be(42L);
        result.ErrorMetadata.Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>();
        var mutation = () => ((IDictionary<string, object?>)result.ErrorMetadata)
            .Add("another", "value");
        mutation.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void GenericFailure_RetainsErrorMetadata()
    {
        var result = Result.Failure<long>(
            "poi.possible_duplicate",
            "A possible duplicate exists.",
            new Dictionary<string, object?> { ["existingPoiId"] = 7L });

        result.ErrorMetadata["existingPoiId"].Should().Be(7L);
    }
}
