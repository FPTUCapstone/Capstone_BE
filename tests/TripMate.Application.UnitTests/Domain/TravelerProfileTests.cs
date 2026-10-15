using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public sealed class TravelerProfileTests
{
    private static readonly DateTimeOffset LocalNow =
        new(2026, 10, 13, 9, 30, 0, TimeSpan.FromHours(7));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateEmpty_WithNonPositiveUserId_Throws(long userId)
    {
        var act = () => TravelerProfile.CreateEmpty(userId, LocalNow);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateEmpty_LeavesEveryPreferenceUnspecifiedAndStoresUtc()
    {
        var profile = TravelerProfile.CreateEmpty(7, LocalNow);

        profile.UserId.Should().Be(7);
        profile.InterestTagsJson.Should().BeNull();
        profile.PreferredTransportMode.Should().BeNull();
        profile.TravelPace.Should().BeNull();
        profile.RiskTolerance.Should().BeNull();
        profile.FoodPreferencesJson.Should().BeNull();
        profile.UpdatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        profile.UpdatedAtUtc.Should().Be(LocalNow.ToUniversalTime());
    }

    [Fact]
    public void ReplacePreferences_SetsAllFiveDimensionsAndUtcTimestamp()
    {
        var profile = TravelerProfile.CreateEmpty(7, LocalNow);
        var later = LocalNow.AddHours(1);

        profile.ReplacePreferences(
            "[\"Beach\"]",
            TransportMode.Motorbike,
            TravelerPace.Moderate,
            RiskToleranceLevel.Medium,
            "[\"Vegan\"]",
            later);

        profile.InterestTagsJson.Should().Be("[\"Beach\"]");
        profile.PreferredTransportMode.Should().Be(TransportMode.Motorbike);
        profile.TravelPace.Should().Be(TravelerPace.Moderate);
        profile.RiskTolerance.Should().Be(RiskToleranceLevel.Medium);
        profile.FoodPreferencesJson.Should().Be("[\"Vegan\"]");
        profile.UpdatedAtUtc.Should().Be(later.ToUniversalTime());
        profile.UpdatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ReplacePreferences_WithAllNulls_ClearsEveryDimension()
    {
        var profile = TravelerProfile.CreateEmpty(7, LocalNow);
        profile.ReplacePreferences(
            "[\"Beach\"]",
            TransportMode.Car,
            TravelerPace.Fast,
            RiskToleranceLevel.High,
            "[\"Halal\"]",
            LocalNow);

        profile.ReplacePreferences(null, null, null, null, null, LocalNow.AddMinutes(5));

        profile.InterestTagsJson.Should().BeNull();
        profile.PreferredTransportMode.Should().BeNull();
        profile.TravelPace.Should().BeNull();
        profile.RiskTolerance.Should().BeNull();
        profile.FoodPreferencesJson.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReplacePreferences_NormalizesBlankJsonToNull(string blank)
    {
        var profile = TravelerProfile.CreateEmpty(7, LocalNow);

        profile.ReplacePreferences(blank, null, null, null, blank, LocalNow);

        profile.InterestTagsJson.Should().BeNull();
        profile.FoodPreferencesJson.Should().BeNull();
    }

    [Fact]
    public void ReplacePreferences_RejectsJsonLongerThanColumn()
    {
        var profile = TravelerProfile.CreateEmpty(7, LocalNow);
        var tooLong = new string('a', TravelerProfile.PreferenceJsonMaxLength + 1);

        var interests = () => profile.ReplacePreferences(tooLong, null, null, null, null, LocalNow);
        var food = () => profile.ReplacePreferences(null, null, null, null, tooLong, LocalNow);

        interests.Should().Throw<ArgumentException>();
        food.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReplacePreferences_DoesNotChangeUserId()
    {
        var profile = TravelerProfile.CreateEmpty(7, LocalNow);

        profile.ReplacePreferences(null, TransportMode.Walking, null, null, null, LocalNow);

        profile.UserId.Should().Be(7);
    }
}
