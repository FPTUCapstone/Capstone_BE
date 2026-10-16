using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Traveler.Preferences.Common;
using TripMate.Application.Features.Traveler.Preferences.Get;
using TripMate.Application.Features.Traveler.Preferences.GetOptions;
using TripMate.Application.Features.Traveler.Preferences.Update;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Application.UnitTests.Features.Traveler.Preferences;

public sealed class TravelerPreferencesHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetOptions_ReturnsTheFixedInterestContractRegardlessOfPoiData()
    {
        await using var db = CreateDbContext();
        var travelerId = await SeedTravelerAsync(db);
        db.PoiCategories.AddRange(
            PoiCategory.Create("Museum", null),
            PoiCategory.Create("Beach", null),
            PoiCategory.Create("Hotel", null),
            PoiCategory.Create("Restaurant", null),
            PoiCategory.Create("Vehicle Rental", null));
        db.Tags.AddRange(Tag.Create("street food"), Tag.Create("BEACH"));
        await db.SaveChangesAsync();

        var result = await new GetTravelerPreferenceOptionsQueryHandler(db)
            .Handle(new GetTravelerPreferenceOptionsQuery(travelerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Interests.Select(option => option.Key).Should().Equal(
            "adventure", "architecture", "beach", "culinary", "culture", "heritage",
            "history", "museum", "nature", "photography", "relaxation", "shopping");
        result.Value.PreferredTransportModes.Should().Equal("Walking", "Motorbike", "Car", "PublicTransit");
        result.Value.TravelPaces.Should().Equal("Relaxed", "Moderate", "Fast");
        result.Value.RiskTolerances.Should().Equal("Low", "Medium", "High");
        result.Value.FoodPreferences.Should().Equal(TravelerPreferenceKeys.FoodPreferences);
    }

    [Fact]
    public async Task GetOptions_WithNoPoiData_StillReturnsTheFixedInterestContract()
    {
        await using var db = CreateDbContext();
        var travelerId = await SeedTravelerAsync(db);

        var result = await new GetTravelerPreferenceOptionsQueryHandler(db)
            .Handle(new GetTravelerPreferenceOptionsQuery(travelerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Interests.Select(option => option.Key).Should().Equal(
            "adventure", "architecture", "beach", "culinary", "culture", "heritage",
            "history", "museum", "nature", "photography", "relaxation", "shopping");
    }

    [Fact]
    public async Task Get_WithoutProfileRow_ReturnsUnspecifiedRepresentation()
    {
        await using var db = CreateDbContext();
        var travelerId = await SeedTravelerAsync(db);

        var result = await new GetTravelerPreferencesQueryHandler(db)
            .Handle(new GetTravelerPreferencesQuery(travelerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new TravelerPreferencesDto([], null, null, null, [], null));
    }

    [Fact]
    public async Task Get_OmitsStoredInterestsThatAreNoLongerOffered()
    {
        await using var db = CreateDbContext();
        var travelerId = await SeedTravelerAsync(db);
        db.PoiCategories.Add(PoiCategory.Create("Beach", null));
        db.TravelerProfiles.Add(TravelerProfile.Create(travelerId, "[\"beach\",\"Removed tag\"]", Now));
        await db.SaveChangesAsync();

        var result = await new GetTravelerPreferencesQueryHandler(db)
            .Handle(new GetTravelerPreferencesQuery(travelerId), CancellationToken.None);

        result.Value.Interests.Should().Equal("beach");
        result.Value.UpdatedAtUtc.Should().Be(Now);
    }

    [Theory]
    [InlineData(UserRole.TourOperator, AccountStatus.Active)]
    [InlineData(UserRole.Administrator, AccountStatus.Active)]
    [InlineData(UserRole.Traveler, AccountStatus.Locked)]
    [InlineData(UserRole.Traveler, AccountStatus.PendingEmailVerification)]
    public async Task AllHandlers_RejectUsersThatAreNotActiveTravelers(UserRole role, AccountStatus status)
    {
        await using var db = CreateDbContext();
        var userId = await SeedUserAsync(db, role, status);

        var options = await new GetTravelerPreferenceOptionsQueryHandler(db)
            .Handle(new GetTravelerPreferenceOptionsQuery(userId), CancellationToken.None);
        var get = await new GetTravelerPreferencesQueryHandler(db)
            .Handle(new GetTravelerPreferencesQuery(userId), CancellationToken.None);
        var update = await CreateUpdateHandler(db).Handle(Update(userId), CancellationToken.None);

        options.ErrorCode.Should().Be(TravelerPreferenceErrors.Forbidden);
        get.ErrorCode.Should().Be(TravelerPreferenceErrors.Forbidden);
        update.ErrorCode.Should().Be(TravelerPreferenceErrors.Forbidden);
        (await db.TravelerProfiles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Update_WithoutRow_InsertsCanonicalValues()
    {
        await using var db = CreateDbContext();
        var travelerId = await SeedTravelerAsync(db);
        db.PoiCategories.Add(PoiCategory.Create("Beach", null));
        db.Tags.Add(Tag.Create("Street food"));
        await db.SaveChangesAsync();

        var result = await CreateUpdateHandler(db).Handle(
            Update(
                travelerId,
                interests: ["beach", "CULINARY", "Beach"],
                transport: "motorbike",
                pace: "moderate",
                risk: "medium",
                food: ["vegan", "Halal"]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new TravelerPreferencesDto(
            ["beach", "culinary"],
            "Motorbike",
            "Moderate",
            "Medium",
            ["Vegan", "Halal"],
            Now));
        var stored = await db.TravelerProfiles.AsNoTracking().SingleAsync();
        stored.InterestTagsJson.Should().Be("[\"beach\",\"culinary\"]");
        stored.FoodPreferencesJson.Should().Be("[\"Vegan\",\"Halal\"]");
        stored.PreferredTransportMode.Should().Be(TransportMode.Motorbike);
        stored.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public async Task Update_WithExistingRow_ReplacesEveryDimension()
    {
        await using var db = CreateDbContext();
        var travelerId = await SeedTravelerAsync(db);
        db.PoiCategories.Add(PoiCategory.Create("Museum", null));
        var existing = TravelerProfile.CreateEmpty(travelerId, Now.AddDays(-1));
        existing.ReplacePreferences(
            "[\"Museum\"]",
            TransportMode.Car,
            TravelerPace.Fast,
            RiskToleranceLevel.High,
            "[\"Halal\"]",
            Now.AddDays(-1));
        db.TravelerProfiles.Add(existing);
        await db.SaveChangesAsync();

        var result = await CreateUpdateHandler(db).Handle(
            Update(travelerId, transport: "Walking"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var stored = await db.TravelerProfiles.AsNoTracking().SingleAsync();
        stored.InterestTagsJson.Should().BeNull();
        stored.PreferredTransportMode.Should().Be(TransportMode.Walking);
        stored.TravelPace.Should().BeNull();
        stored.RiskTolerance.Should().BeNull();
        stored.FoodPreferencesJson.Should().BeNull();
        stored.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public async Task Update_WithEverythingEmpty_StoresUnspecifiedValues()
    {
        await using var db = CreateDbContext();
        var travelerId = await SeedTravelerAsync(db);

        var result = await CreateUpdateHandler(db).Handle(
            Update(travelerId, interests: [], food: []),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Interests.Should().BeEmpty();
        result.Value.FoodPreferences.Should().BeEmpty();
        var stored = await db.TravelerProfiles.AsNoTracking().SingleAsync();
        stored.InterestTagsJson.Should().BeNull();
        stored.FoodPreferencesJson.Should().BeNull();
    }

    [Fact]
    public async Task Update_WithUnknownInterest_FailsWithoutWriting()
    {
        await using var db = CreateDbContext();
        var travelerId = await SeedTravelerAsync(db);
        db.PoiCategories.Add(PoiCategory.Create("Beach", null));
        await db.SaveChangesAsync();

        var result = await CreateUpdateHandler(db).Handle(
            Update(travelerId, interests: ["Beach", "Skydiving"], transport: "Car"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelerPreferenceErrors.InvalidOption);
        result.ErrorMetadata[TravelerPreferenceErrors.FieldMetadataKey]
            .Should().Be(TravelerPreferenceErrors.InterestsField);
        result.ErrorMetadata[TravelerPreferenceErrors.InvalidValuesMetadataKey]
            .Should().BeEquivalentTo(new[] { "Skydiving" });
        (await db.TravelerProfiles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Update_WithCommercialCategoryAsInterest_FailsWithInvalidOption()
    {
        await using var db = CreateDbContext();
        var travelerId = await SeedTravelerAsync(db);
        db.PoiCategories.Add(PoiCategory.Create("Hotel", null));
        await db.SaveChangesAsync();

        var result = await CreateUpdateHandler(db).Handle(
            Update(travelerId, interests: ["Hotel"]),
            CancellationToken.None);

        result.ErrorCode.Should().Be(TravelerPreferenceErrors.InvalidOption);
    }

    [Fact]
    public async Task Update_DoesNotTouchUserColumns()
    {
        await using var db = CreateDbContext();
        var travelerId = await SeedTravelerAsync(db);
        var before = await db.Users.AsNoTracking().SingleAsync(user => user.Id == travelerId);

        await CreateUpdateHandler(db).Handle(Update(travelerId), CancellationToken.None);

        var after = await db.Users.AsNoTracking().SingleAsync(user => user.Id == travelerId);
        after.Should().BeEquivalentTo(before, options => options.Excluding(user => user.RefreshTokens));
    }

    [Fact]
    public async Task Update_SavedInterestIsMatchedBySchedulingPreferenceScoring()
    {
        await using var db = CreateDbContext();
        var travelerId = await SeedTravelerAsync(db);
        var category = PoiCategory.Create("nature", null);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync();

        (await CreateUpdateHandler(db).Handle(
            Update(travelerId, interests: ["NATURE"]),
            CancellationToken.None)).IsSuccess.Should().BeTrue();

        var json = await db.TravelerProfiles.AsNoTracking()
            .Select(profile => profile.InterestTagsJson)
            .SingleAsync();
        var poi = PointOfInterest.Create(category, "Scoring POI", 16.05m, 108.24m, travelerId, Now);
        var tokens = TravelerPreferenceScoring.ParsePreferenceTokens(json);

        TravelerPreferenceScoring.CalculatePreferenceScore(poi, tokens).Should().Be(100);
    }

    private static UpdateTravelerPreferencesCommandHandler CreateUpdateHandler(ApplicationDbContext db) =>
        new(db, new FakeDateTimeProvider { UtcNow = Now });

    private static UpdateTravelerPreferencesCommand Update(
        long userId,
        string?[]? interests = null,
        string? transport = null,
        string? pace = null,
        string? risk = null,
        string?[]? food = null) =>
        new(userId, interests, transport, pace, risk, food);

    private static Task<long> SeedTravelerAsync(ApplicationDbContext db) =>
        SeedUserAsync(db, UserRole.Traveler, AccountStatus.Active);

    private static async Task<long> SeedUserAsync(
        ApplicationDbContext db,
        UserRole role,
        AccountStatus status)
    {
        var user = new User
        {
            Email = $"preferences-{Guid.NewGuid():N}@example.test",
            FullName = "Preference User",
            Role = role,
            Status = status,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }
}